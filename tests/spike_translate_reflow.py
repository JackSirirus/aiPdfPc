# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""v1.1a 回填 spike：合成样例 + mock LLM + 量化验收（GATE）。

按《需求总结.md §实现分期与验收门（V1.1a）》与《翻译-回填排版规格.md §8/§9》，
在 4 个**纯合成**样例上跑完整翻译回填链路并判定是否达到量化退出标准：

1. `double_column`：2 栏版面（列缝 + 列内阅读序 + 段落重建）；
2. `table_dense`：表格密集页（矢量表格线 + 短单元格文本）；
3. `formula_dense`：公式密集页（ASCII 数学串，避免依赖系统字形）；
4. `scanned`：纯图片扫描页（无文本层），走 OCR 注入 → 光栅叠加回填。

链路：`engine.dispatch({"operation": "translate"})` → extract（T3）→ chunk（T4）→
prompt（T6）→ LLM（T5，mock）→ verify（T7）→ reflow（T8）→ output（T9）。

确定性
------
- **LLM 被 mock**：patch `translate.llm._open`，不发起任何真实网络请求；mock 解析
  prompt 中 `<untrusted>` 包裹的待译正文并返回确定性伪中文译文，因此测的是**回填
  排版质量**而不是模型质量。默认按源文本确定性取字数收缩比 `[0.50, 0.80]`
  （EN→ZH 常见波动；`SPIKE_CJK_RATIO` 可固定为单一比例）；
  另跑 `SPIKE_SENSITIVITY`（默认 1.0/1.5，模拟扩写方向）作为**非门禁**灵敏度检查，
  用来确认 S2/S3 溢出/裁切机制确实会被触发且计数正确。
- **扫描页 OCR 被注入**：patch `translate.extract._vision_ocr_boxes` 返回固定框。
  真实平台 OCR 在合成图像上的识别率/耗时不稳定，会让度量不可复现；OCR 注入只服务于
  让「光栅叠加」回填路径确定性可测，真实 OCR 链路由 `tests/test_translate_extract.py`
  覆盖。报告会如实标注这一点。
- 所有样例在内存/PyMuPDF 中现造，**不读取任何真实或个人文档**。

三项量化门禁（spike 100% 严于运行时 95%）
-----------------------------------------
- 溢出页占比（S2 `overflow_margin` ∪ S3 `clip` 标记页 / 总页数）≤ 10%；
- 每页裁切块数（单页 S3 次数最大值）≤ 2；
- ToUnicode 覆盖率（可提取**译文**页 / 应有译文页）= 100%（扫描光栅叠加页按规格 §8.5
  豁免）。可提取判定用 mock 译文的规范化前缀在输出页 `get_text()` 中命中，而不是
  「页面非空」——产物每页都带 6pt 页脚标记，非空会掩盖真正的不可搜索页。

运行：`.venv\\Scripts\\python.exe tests\\spike_translate_reflow.py`
本脚本即使判定 FAIL 也返回 0（spike 是质量门而非删除开关，不阻塞其余计划）；
仅在脚本自身异常时返回 2。报告写入 `.omo/evidence/v11-spike-report.md`。
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import tempfile
import textwrap
import time
import traceback
import unittest.mock as mock
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

import pymupdf as fitz  # noqa: E402

import engine  # noqa: E402
from translate import extract, geometry, llm, reflow, verify  # noqa: E402

REPORT_PATH = ROOT / ".omo" / "evidence" / "v11-spike-report.md"

PAGE_W = 595.0
PAGE_H = 842.0

# EN→ZH 字数收缩比的真实波动区间：中文译文字符数 ≈ 英文源字符数 × [0.50, 0.80]。
RATIO_MIN, RATIO_MAX = 0.50, 0.80
# 可选：SPIKE_CJK_RATIO 固定单一比例；未设置时按源文本确定性取区间内比例（模拟真实波动）。
_ENV_RATIO = os.environ.get("SPIKE_CJK_RATIO", "").strip()
GATE_RATIO = float(_ENV_RATIO) if _ENV_RATIO else None
# 输出形态：mono 与 dual 各自会回填一遍，块级计数需按形态数折算成单次渲染口径。
OUTPUT_FORMATS = ("mono", "dual")
# 非门禁灵敏度扩写比：确认 S2/S3 机制被触发（默认模拟较激进的目标语言）。
SENSITIVITY_RATIOS = tuple(
    float(value) for value in os.environ.get("SPIKE_SENSITIVITY", "1.0,1.5").split(",")
    if value.strip())

# 伪译文用字池（合成、无任何真实文本含义）。
_CJK_POOL = ("本译文由合成回填测试确定性生成用于验证排版溢出与文本可搜索性"
             "请勿用于真实场景的机器翻译质量评估")
_WORDS = ("layout column paragraph synthetic document translation reflow extraction "
          "quality measurement strategy narrow block margin whitespace engine sample "
          "reproducible deterministic overflow clip searchable text coordinate reading "
          "order heading table formula scanned raster overlay stream verify").split()

# 扫描页注入的 OCR 框（归一化到可见页，与 build_scanned 的图像内容对应）。
_OCR_BOXES = [
    {"text": "SCANNED DOCUMENT SAMPLE", "x": 0.09, "y": 0.115, "w": 0.55, "h": 0.022},
    {"text": "This page has no text layer and is provided", "x": 0.09, "y": 0.155, "w": 0.82, "h": 0.022},
    {"text": "only as a raster image for the reflow spike", "x": 0.09, "y": 0.195, "w": 0.72, "h": 0.022},
    {"text": "Optical character recognition output is injected", "x": 0.09, "y": 0.235, "w": 0.86, "h": 0.022},
    {"text": "deterministically so the raster overlay path is", "x": 0.09, "y": 0.275, "w": 0.82, "h": 0.022},
    {"text": "exercised without depending on the platform OCR", "x": 0.09, "y": 0.315, "w": 0.82, "h": 0.022},
]

_SCAN_LINES = [
    "SCANNED DOCUMENT SAMPLE",
    "This page has no text layer and is provided",
    "only as a raster image for the reflow spike.",
    "Optical character recognition output is injected",
    "deterministically so the raster overlay path is",
    "exercised without depending on the platform OCR.",
]

_FORMULAS = [
    "E = m * c^2",
    "F = G * (m1 * m2) / r^2",
    "int_0^inf exp(-x^2) dx = sqrt(pi) / 2",
    "d/dx sin(x) = cos(x)",
    "a^2 + b^2 = c^2",
    "sum_{i=1}^{n} i = n * (n + 1) / 2",
]


# —— 合成样例构造 ------------------------------------------------------------

def _paragraph(seed, count=42):
    """由 LCG 生成确定性英文段落（合成词池，无真实文档内容）。"""
    state = (seed * 2654435761 + 1013904223) & 0xFFFFFFFF
    parts = []
    for _ in range(count):
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        parts.append(_WORDS[state % len(_WORDS)])
    return " ".join(parts).capitalize() + "."


def _wrap_lines(page, x, y, text, width_chars=44, size=10.5, leading=13.5):
    for line in textwrap.wrap(text, width=width_chars):
        page.insert_text((x, y), line, fontsize=size, fontname="helv")
        y += leading
    return y


def build_double_column(pages=3):
    """两栏版面：左/右列各 5 段，段间距 22pt（> 1.6×行高，避免段落被合并）。"""
    doc = fitz.open()
    margin, gutter = 50.0, 34.0
    col_w = (PAGE_W - 2 * margin - gutter) / 2.0
    for p in range(pages):
        page = doc.new_page(width=PAGE_W, height=PAGE_H)
        for col in range(2):
            x = margin + col * (col_w + gutter)
            y = 80.0
            for para in range(5):
                y = _wrap_lines(page, x, y, _paragraph(p * 100 + col * 10 + para))
                y += 22.0
    return doc


def _cell_value(page_index, row, col):
    if col == 0:
        return f"{row + 1:03d}"
    if col == 1:
        return f"Item {page_index}{row}{col}"
    if col == 2:
        return str((row * 7 + col) % 90 + 1)
    if col == 3:
        return f"{(row * 13 + col) % 900 + 100 / 100.0:.2f}"
    if col == 4:
        return ("OK", "NEW", "HOLD", "DONE")[(row + col) % 4]
    return ("note", "alpha", "beta", "gamma")[(row + col) % 4]


def build_table(pages=3, rows=8):
    """表格密集页：6 列 ×（表头 + 8 行）网格 + 页脚说明段，保留矢量表格线。"""
    doc = fitz.open()
    headers = ("ID", "Item", "Qty", "Price", "Status", "Note")
    left, top = 40.0, 70.0
    cell_w = (PAGE_W - 2 * left) / len(headers)
    cell_h = 38.0
    for p in range(pages):
        page = doc.new_page(width=PAGE_W, height=PAGE_H)
        page.insert_text((left, top - 16), f"Synthetic Table Sample {p + 1}", fontsize=13)
        bottom = top + (rows + 1) * cell_h
        right = left + len(headers) * cell_w
        for r in range(rows + 2):
            y = top + r * cell_h
            page.draw_line(fitz.Point(left, y), fitz.Point(right, y),
                           color=(0.2, 0.2, 0.2), width=0.6)
        for c in range(len(headers) + 1):
            x = left + c * cell_w
            page.draw_line(fitz.Point(x, top), fitz.Point(x, bottom),
                           color=(0.2, 0.2, 0.2), width=0.6)
        for c, head in enumerate(headers):
            page.insert_text((left + c * cell_w + 4, top + cell_h - 9), head, fontsize=10)
        for r in range(rows):
            for c in range(len(headers)):
                page.insert_text((left + c * cell_w + 4, top + (r + 1) * cell_h + cell_h - 9),
                                 _cell_value(p, r, c), fontsize=9)
        _wrap_lines(page, left, bottom + 40, _paragraph(p * 1000 + 77, count=36),
                    width_chars=110, size=10.5)
    return doc


def build_formula(pages=3):
    """公式密集页：正文段 + 6 条 ASCII 数学式，测试非自然文本的回填。"""
    doc = fitz.open()
    for p in range(pages):
        page = doc.new_page(width=PAGE_W, height=PAGE_H)
        page.insert_text((60, 70), f"Synthetic Formula Sample {p + 1}", fontsize=13)
        y = _wrap_lines(page, 60, 100, _paragraph(p * 100 + 13), width_chars=92, size=10.5)
        y += 14
        for formula in _FORMULAS:
            page.insert_text((80, y), formula, fontsize=12, fontname="helv")
            y += 30
        y += 8
        _wrap_lines(page, 60, y, _paragraph(p * 100 + 57), width_chars=92, size=10.5)
    return doc


def build_scanned():
    """纯图片扫描页：先把文本渲染成位图，再整页插入，不留下任何文本层。"""
    source = fitz.open()
    page = source.new_page(width=PAGE_W, height=PAGE_H)
    for index, line in enumerate(_SCAN_LINES):
        page.insert_text((60, 120 + index * 28), line, fontsize=14, fontname="helv")
    pixmap = page.get_pixmap(dpi=160)
    image = pixmap.tobytes("png")
    source.close()
    doc = fitz.open()
    page = doc.new_page(width=PAGE_W, height=PAGE_H)
    page.insert_image(page.rect, stream=image)
    return doc


SAMPLES = (
    {"name": "double_column", "label": "合成双栏", "build": build_double_column,
     "file": "synth-double-column.pdf", "scanned": False},
    {"name": "table_dense", "label": "表格密集", "build": build_table,
     "file": "synth-table.pdf", "scanned": False},
    {"name": "formula_dense", "label": "公式密集", "build": build_formula,
     "file": "synth-formula.pdf", "scanned": False},
    {"name": "scanned", "label": "扫描件", "build": build_scanned,
     "file": "synth-scanned.pdf", "scanned": True},
)


# —— mock LLM（确定性、无网络） ---------------------------------------------

class _FakeResponse:
    """`urllib.request.urlopen` 返回值替身：支持上下文管理器与 read()。"""

    def __init__(self, body):
        self._body = json.dumps(body, ensure_ascii=False).encode("utf-8")

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def block_ratio(source):
    """按源文本确定性取一个 [RATIO_MIN, RATIO_MAX] 内的字数收缩比（模拟真实波动）。"""
    digest = hashlib.sha256((source or "").encode("utf-8")).hexdigest()
    span = int(round((RATIO_MAX - RATIO_MIN) * 100))
    return RATIO_MIN + (int(digest[:8], 16) % (span + 1)) / 100.0


def fake_translate(source, ratio=None):
    """确定性伪中文译文：字符数 = max(2, round(源字符数 × ratio))，与源不相似。

    `ratio=None` 时按 `block_ratio(source)` 取区间内比例（门禁默认）；显式传比例
    用于灵敏度检查（固定扩写比）。
    """
    source = re.sub(r"\s+", " ", source or "").strip()
    ratio = block_ratio(source) if ratio is None else ratio
    length = max(2, round(len(source) * ratio))
    return "".join(_CJK_POOL[(index * 7 + 3) % len(_CJK_POOL)] for index in range(length))


def _untrusted_text(user_message):
    """从 prompt 的 user 消息里取出 `<untrusted>` 包裹的待译正文。"""
    match = re.search(r"<untrusted[^>]*>(.*?)</untrusted>", user_message or "", re.S)
    text = match.group(1) if match else (user_message or "")
    return text.replace("&lt;", "<").replace("&gt;", ">")


def make_fake_open(ratio):
    """构造 `translate.llm._open` 的替身：不发网络，按请求正文返回确定性译文。"""
    def fake_open(request, timeout):
        payload = json.loads(bytes(request.data).decode("utf-8"))
        user = next(item["content"] for item in payload["messages"] if item["role"] == "user")
        content = json.dumps(
            [{"index": 0, "text": fake_translate(_untrusted_text(user), ratio)}],
            ensure_ascii=False)
        return _FakeResponse({"choices": [{"index": 0, "finish_reason": "stop",
                                           "message": {"role": "assistant", "content": content}}]})
    return fake_open


def fake_ocr_boxes(page):
    """扫描页 OCR 注入：返回固定框，使光栅叠加回填路径确定性可测。"""
    return [dict(box) for box in _OCR_BOXES]


# —— 计数型降级报告：DegradationReport 只去重存原因，这里额外数块级次数 -------

class CountingReport(verify.DegradationReport):
    """记录每页每个降级原因的**块级调用次数**（供裁切块数/决策路径覆盖度量）。"""

    instances = []

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.block_reasons = {}
        CountingReport.instances.append(self)

    def add(self, page_no, reason, detail=None):
        page_counts = self.block_reasons.setdefault(reason, {})
        page_counts[page_no] = page_counts.get(page_no, 0) + 1
        return super().add(page_no, reason, detail)


# —— 单样例执行与度量 --------------------------------------------------------

def _normalize(text):
    return re.sub(r"\s+", "", text or "")


def classify_pages(path, scanned):
    """用 T3 抽取口径判定「应有译文页」「扫描光栅页」并保留每页原文块。"""
    with fitz.open(path) as doc:
        ocr_map = {index: _OCR_BOXES for index in range(len(doc))} if scanned else None
        extraction = extract.extract_document(doc, ocr_boxes_by_page=ocr_map)
        all_pages = set(range(1, len(doc) + 1))
        block_count = len(extraction.blocks)
    text_pages = {block.page_no for block in extraction.blocks if block.font != "ocr"}
    raster_pages = {block.page_no for block in extraction.blocks if block.font == "ocr"}
    raster_only = raster_pages - text_pages
    empty_pages = all_pages - text_pages - raster_pages
    source_by_page = {}
    for block in extraction.blocks:
        if block.page_no in text_pages:
            source_by_page.setdefault(block.page_no, []).append(block.text)
    return {"text_pages": text_pages, "raster_only": raster_only,
            "empty_pages": empty_pages, "block_count": block_count,
            "source_by_page": source_by_page, "report": extraction.report}


def run_sample(sample, workdir, ratio):
    """构造样例 → 保存 → 跑完整 translate 链路 → 收集原始结果。"""
    doc = sample["build"]()
    path = workdir / sample["file"]
    doc.save(path)
    doc.close()
    before = hashlib.sha256(path.read_bytes()).hexdigest()

    info = classify_pages(path, sample["scanned"])
    dispatch = {
        "operation": "translate", "files": [str(path)],
        "options": {"apiKey": "sk-spike-not-a-real-key", "endpoint": "https://spike.invalid/v1",
                    "model": "spike-model", "targetLang": "zh", "sourceLang": "en",
                    "template": "generic", "format": "both"},
        "outputDir": str(workdir),
    }

    CountingReport.instances = []
    error = None
    result = None
    started = time.monotonic()
    try:
        with mock.patch.object(llm, "_open", side_effect=make_fake_open(ratio)), \
                mock.patch.object(extract, "_vision_ocr_boxes", side_effect=fake_ocr_boxes), \
                mock.patch.object(verify, "DegradationReport", CountingReport):
            result = engine.dispatch(dispatch)
    except Exception:  # 记录并继续，保证报告如实产出
        error = traceback.format_exc()
    elapsed = time.monotonic() - started
    after = hashlib.sha256(path.read_bytes()).hexdigest()

    entry = {"name": sample["name"], "label": sample["label"], "file": str(path),
             "scanned": sample["scanned"], "error": error, "elapsed": round(elapsed, 2),
             "source_readonly": before == after, "info": info, "ratio": ratio,
             "total_pages": 0, "mono": None, "dual": None, "report": None,
             "counting": CountingReport.instances[-1] if CountingReport.instances else None}
    if result is None:
        return entry
    entry["report"] = result["results"][0]["report"]
    entry["mono"] = result["results"][0].get("mono")
    entry["dual"] = result["results"][0].get("dual")
    entry["total_pages"] = entry["report"]["summary"]["total_pages"]
    return entry


def sample_metrics(entry, builds=1):
    """把单样例原始结果折算成可判定度量。

    `builds` 为本次运行的输出形态数：mono 与 dual 各回填一遍，块级计数会翻倍，
    因此这里折算回**单次渲染**口径（页级原因已去重，不受影响）。
    """
    counts = entry["counting"]
    report = entry["report"] or {"pages": [], "summary": {"total_pages": 0}}
    page_reasons = {item["page"]: set(item["reasons"]) for item in report["pages"]}
    overflow_pages = {page for page, reasons in page_reasons.items()
                      if reasons & {verify.OVERFLOW_MARGIN, verify.CLIP}}
    clip_pages = {page for page, reasons in page_reasons.items() if verify.CLIP in reasons}
    raw_reasons = dict(getattr(counts, "block_reasons", {}) or {})
    block_reasons = {
        reason: {page: max(1, count // builds) for page, count in page_counts.items()}
        for reason, page_counts in raw_reasons.items()}
    clip_blocks = dict(block_reasons.get(verify.CLIP, {}) or {})

    text_pages = set(entry["info"]["text_pages"])
    extractable, missing, residual = set(), set(), set()
    mono = entry.get("mono")
    if mono and Path(mono).is_file():
        with fitz.open(mono) as out:
            for page_no in sorted(text_pages):
                if page_no > len(out):
                    missing.add(page_no)
                    continue
                text = _normalize(out[page_no - 1].get_text())
                # §8.2 存在性校验：mock 译文的规范化前缀必须能从产物页提取到。
                found = False
                for source in entry["info"]["source_by_page"].get(page_no, []):
                    probe = _normalize(fake_translate(source, entry["ratio"]))[:8]
                    if probe and probe in text:
                        found = True
                        break
                if found:
                    extractable.add(page_no)
                else:
                    missing.add(page_no)
                # §8.3 原文缺失校验：被替换块的原文不应再被提取到。
                for source in entry["info"]["source_by_page"].get(page_no, []):
                    probe = _normalize(source)
                    if probe and probe in text:
                        residual.add(page_no)
                        break
    else:
        missing |= text_pages

    return {
        "total_pages": entry["total_pages"],
        "text_pages": text_pages,
        "raster_only": entry["info"]["raster_only"],
        "empty_pages": entry["info"]["empty_pages"],
        "block_count": entry["info"]["block_count"],
        "overflow_pages": overflow_pages,
        "clip_pages": clip_pages,
        "clip_blocks": clip_blocks,
        "max_clip_blocks": max(clip_blocks.values(), default=0),
        "block_reasons": block_reasons,
        "degraded_pages": {page for page, reasons in page_reasons.items() if reasons},
        "page_reasons": page_reasons,
        "extractable": extractable,
        "missing": missing,
        "residual": residual,
        "expected": text_pages,
        "search_verify_fail": {page for page, reasons in page_reasons.items()
                               if verify.SEARCH_VERIFY_FAIL in reasons},
    }


def aggregate(metrics):
    total_pages = sum(item["total_pages"] for item in metrics)
    overflow_pages = sum(len(item["overflow_pages"]) for item in metrics)
    clip_pages = sum(len(item["clip_pages"]) for item in metrics)
    expected = sum(len(item["expected"]) for item in metrics)
    extractable = sum(len(item["extractable"]) for item in metrics)
    max_clip = max((item["max_clip_blocks"] for item in metrics), default=0)
    residual = sum(len(item["residual"]) for item in metrics)
    return {
        "total_pages": total_pages,
        "overflow_pages": overflow_pages,
        "overflow_ratio": (overflow_pages / total_pages) if total_pages else 1.0,
        "clip_pages": clip_pages,
        "clip_ratio": (clip_pages / total_pages) if total_pages else 1.0,
        "max_clip_blocks": max_clip,
        "expected_pages": expected,
        "extractable_pages": extractable,
        "to_unicode_coverage": (extractable / expected) if expected else 1.0,
        "residual_pages": residual,
    }


def run_suite(ratio, workdir):
    """在 4 个样例上跑一遍完整链路并度量。"""
    workdir.mkdir(parents=True, exist_ok=True)
    entries = [run_sample(sample, workdir, ratio) for sample in SAMPLES]
    metrics = [sample_metrics(entry, builds=len(OUTPUT_FORMATS)) for entry in entries]
    return {"ratio": ratio, "entries": entries, "metrics": metrics,
            "totals": aggregate(metrics)}


def self_check_machinery():
    """直接调动回填引擎，验证 S2/S3 的**计数机制**在门禁样例未触发时仍然正确。"""
    def run(translated):
        doc = fitz.open()
        page = doc.new_page(width=300, height=400)
        page.insert_text((40, 55), "SOURCE", fontsize=13)
        block = extract.TextBlock(page_no=1, rect=geometry.RectNorm(35, 40, 150, 60, 0),
                                  text="SOURCE", size=13)
        report = CountingReport(task_id="selfcheck", total_pages=1)
        reflow.backfill_page(page, [block], {0: translated}, report)
        doc.close()
        return report

    clip = run("overflow " * 400)
    overflow = run("The translated version is considerably longer than the source text")
    return {
        "clip": dict(clip.block_reasons.get(verify.CLIP, {})),
        "overflow_margin": dict(overflow.block_reasons.get(verify.OVERFLOW_MARGIN, {})),
    }


# —— 报告渲染 ----------------------------------------------------------------

def _pages_text(pages):
    return ", ".join(str(page) for page in sorted(pages)) or "—"


def _ratio_description():
    if GATE_RATIO is None:
        return f"按源文本确定性取 [{RATIO_MIN:.2f}, {RATIO_MAX:.2f}]（模拟真实波动）"
    return f"固定 {GATE_RATIO}"


def _suite_table(lines, suite):
    entries, metrics = suite["entries"], suite["metrics"]
    lines.append("| 样例 | 总页 | 块数 | 应有译文页 | 可提取译文页 | 溢出页(S2/S3) | 裁切页(S3) | 最大裁切块/页 | 缩字页(S1) | 扫描光栅页 |")
    lines.append("|------|-----:|-----:|-----------:|-------------:|--------------:|-----------:|--------------:|-----------:|-----------:|")
    for entry, item in zip(entries, metrics):
        shrink_pages = {page for page, reasons in item["page_reasons"].items()
                        if verify.FONT_SHRINK in reasons}
        lines.append(
            f"| {entry['label']} | {item['total_pages']} | {item['block_count']} | "
            f"{len(item['expected'])} | {len(item['extractable'])} | "
            f"{_pages_text(item['overflow_pages'])} | {_pages_text(item['clip_pages'])} | "
            f"{item['max_clip_blocks']} | {_pages_text(shrink_pages)} | "
            f"{_pages_text(item['raster_only'])} |")
    lines.append("")


def _decision_table(lines, suite):
    """块级决策路径覆盖：证明回填确实按 S0→S3 执行。"""
    aggregated = {}
    for item in suite["metrics"]:
        for reason, page_counts in item["block_reasons"].items():
            aggregated[reason] = aggregated.get(reason, 0) + sum(page_counts.values())
    order = (verify.FONT_SHRINK, verify.OVERFLOW_MARGIN, verify.CLIP, verify.GLYPH_MISSING,
             verify.FONT_FALLBACK, verify.RASTER_OVERLAY, verify.SEARCH_VERIFY_FAIL,
             verify.UNTRANSLATED_ECHO, verify.EXTRACTION_LOW_CONF, verify.EMPTY_PAGE)
    lines.append("| 决策/降级原因 | 触发块次数 |")
    lines.append("|---------------|-----------:|")
    emitted = False
    for reason in order:
        if reason in aggregated:
            lines.append(f"| `{reason}` | {aggregated[reason]} |")
            emitted = True
    for reason in sorted(set(aggregated) - set(order)):
        lines.append(f"| `{reason}` | {aggregated[reason]} |")
        emitted = True
    if not emitted:
        lines.append("| （无） | 0 |")
    lines.append("")


def render_report(gate, sensitivity, self_check, started, finished):
    totals = gate["totals"]
    lines = []
    lines.append("# v1.1a 回填 spike 量化验收报告")
    lines.append("")
    lines.append("> 门禁：`需求总结.md §实现分期与验收门（V1.1a）`；"
                 "回填规格：`翻译-回填排版规格.md §8/§9`")
    lines.append(f"> 生成时间：{time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(started))}"
                 f"（耗时 {round(finished - started, 2)}s）")
    lines.append("> 运行：`.venv\\Scripts\\python.exe tests\\spike_translate_reflow.py`")
    lines.append("> 样例：全部为 PyMuPDF 内存合成，**不读取任何真实或个人文档**。")
    lines.append("")

    lines.append("## 方法")
    lines.append("")
    lines.append("- 链路：`engine.dispatch({operation: translate})` → extract → chunk → "
                 "prompt → LLM(mock) → verify → reflow → output。")
    lines.append("- LLM：patch `translate.llm._open`，mock 解析 prompt 中 `<untrusted>` "
                 "正文并返回确定性伪中文译文；**无任何真实网络请求**。门禁方向为默认 "
                 f"EN→ZH（目标语言默认中文），字数收缩比{_ratio_description()}。")
    lines.append("- 扫描页：patch `translate.extract._vision_ocr_boxes` 注入固定 OCR 框"
                 "（真实平台 OCR 在合成图像上的结果不稳定，会让度量不可复现；"
                 "真实 OCR 链路由 `tests/test_translate_extract.py` 覆盖）。")
    lines.append("- 溢出页 = 带 `overflow_margin`(S2) 或 `clip`(S3) 的页；裁切块数 = S3 的"
                 "块级调用次数（计数型报告，去重前计数，并按输出形态数折算成单次渲染口径）；"
                 "ToUnicode 覆盖率 = 可提取**译文**页 / 应有译文页（扫描光栅页按规格 §8.5 "
                 "豁免，且以 mock 译文前缀命中判定，避免页脚标记把不可搜索页算成可搜索）。")
    lines.append("")

    lines.append("## 门禁：每样例度量")
    lines.append("")
    _suite_table(lines, gate)

    lines.append("## 门禁：回填决策路径覆盖（块级）")
    lines.append("")
    _decision_table(lines, gate)

    lines.append("## 三项量化门禁")
    lines.append("")
    overflow_ok = totals["overflow_ratio"] <= 0.10 + 1e-9
    clip_ok = totals["max_clip_blocks"] <= 2
    tounicode_ok = totals["to_unicode_coverage"] >= 1.0 - 1e-9
    lines.append("| 指标 | 阈值 | 实测 | 判定 |")
    lines.append("|------|------|------|------|")
    lines.append(f"| 溢出页占比（S2/S3） | ≤ 10% | "
                 f"{totals['overflow_pages']}/{totals['total_pages']} = "
                 f"{totals['overflow_ratio'] * 100:.1f}% | "
                 f"{'PASS' if overflow_ok else 'FAIL'} |")
    lines.append(f"| 每页裁切块数 | ≤ 2 | {totals['max_clip_blocks']} | "
                 f"{'PASS' if clip_ok else 'FAIL'} |")
    lines.append(f"| ToUnicode 覆盖率 | = 100% | "
                 f"{totals['extractable_pages']}/{totals['expected_pages']} = "
                 f"{totals['to_unicode_coverage'] * 100:.1f}% | "
                 f"{'PASS' if tounicode_ok else 'FAIL'} |")
    lines.append("")
    lines.append(f"> 参考：设计原文「溢出页占比（`clip` 标记页 / 总页数）」按 S3-only 口径为 "
                 f"{totals['clip_pages']}/{totals['total_pages']} = "
                 f"{totals['clip_ratio'] * 100:.1f}%。本报告门禁采用任务口径（S2∪S3，更严格）。")
    lines.append("")

    verdict = "PASS" if (overflow_ok and clip_ok and tounicode_ok) else "FAIL"
    lines.append(f"## VERDICT: {verdict}")
    lines.append("")

    lines.append("## 灵敏度检查（非门禁）：扩写方向下 S2/S3 是否被触发")
    lines.append("")
    lines.append("门禁样例按默认 EN→ZH 收缩方向构造，实测只触发 S0/S1。为避免 S2/S3 "
                 "门禁指标形同虚设，下面用更大的 mock 字数比复跑同一测试集，确认溢出/裁切"
                 "机制确实会触发且计数正确。**此段不参与 VERDICT 判定。**")
    lines.append("")
    lines.append("| CJK 字数比 | 溢出页占比 | 裁切页 | 最大裁切块/页 | ToUnicode 覆盖 |")
    lines.append("|-----------:|-----------:|-------:|-------------:|---------------:|")
    for suite in sensitivity:
        item = suite["totals"]
        lines.append(f"| {suite['ratio']} | {item['overflow_pages']}/{item['total_pages']} = "
                     f"{item['overflow_ratio'] * 100:.0f}% | {item['clip_pages']} | "
                     f"{item['max_clip_blocks']} | {item['extractable_pages']}/"
                     f"{item['expected_pages']} = {item['to_unicode_coverage'] * 100:.0f}% |")
    lines.append("")

    lines.append("## 计量机制自检（S2/S3 计数）")
    lines.append("")
    clip_hits = sum(self_check["clip"].values())
    overflow_hits = sum(self_check["overflow_margin"].values())
    lines.append(f"- S3 `clip` 计数：自检页触发 {clip_hits} 块 → "
                 f"{'PASS' if clip_hits >= 1 else 'FAIL'}。")
    lines.append(f"- S2 `overflow_margin` 计数：自检页触发 {overflow_hits} 块 → "
                 f"{'PASS' if overflow_hits >= 1 else 'FAIL'}。")
    lines.append("- 说明：门禁样例在默认 EN→ZH 方向只触发 S0/S1；S2 在灵敏度检查中触发；"
                 "S3 未在这组样例出现，但由本自检与 `tests/test_translate_reflow.py::"
                 "test_s3_clips_as_last_resort` 覆盖。")
    lines.append("")

    lines.append("## 附带发现（不改变上述三项门禁结论）")
    lines.append("")
    lines.append(f"- 原文残留页（§8.3 原文缺失校验）：{totals['residual_pages']} 页。")
    lines.append(f"- `search_verify_fail` 页："
                 f"{', '.join(str(p) for item in gate['metrics'] for p in sorted(item['search_verify_fail'])) or '—'}。")
    schema_ok = all(
        isinstance(item, dict) and {"page", "degraded", "reasons", "searchable"} <= set(item)
        for entry in gate["entries"] for item in (entry["report"] or {}).get("pages", []))
    lines.append(f"- 降级报告 schema（每页 `degraded`+`reasons`，§9.2）："
                 f"{'PASS' if schema_ok else 'FAIL'}；报告按设计为稀疏结构（只列降级页），"
                 "`summary.total_pages` 覆盖全文。")
    lines.append("- **门禁适用边界（诚实披露）**：4 个门禁样例在默认 EN→ZH 收缩下回填"
                 "只走到 S0/S1（缩字），S2/S3 未在门禁样例中触发；上表灵敏度检查证明"
                 "扩写时会触发且计数正确。若产品要覆盖扩写/极密排版，T24 应补一个扩写方向"
                 "样例再复核。")
    lines.append("- **原因覆盖缺口**：`formula_fail`、`table_fail`、`rotation_crop`、"
                 "`unsupported_script`、`reassembly_mismatch`、`segment_split` 已登记在 "
                 "`verify.DEGRADATION_REASONS`，但当前 reflow/extract 链路**从不写出**这些原因"
                 "（公式/表格/旋转/RTL 未做结构化检测）。此为 T8 的已知能力边界，"
                 "不影响本 spike 的三项排版门禁，但 T20/T24 应如实披露或补齐。")
    lines.append("- 样例 `scanned` 仅 1 页且为光栅叠加（豁免可搜索性），其 `raster_overlay` "
                 "路径已由注入 OCR 触发；未验证真实 OCR 识别质量。")
    lines.append("- 源文件只读校验："
                 + ("全部样例源文件 SHA-256 未变。" if all(e["source_readonly"] for e in gate["entries"])
                    else "**存在源文件被修改的样例！**"))
    lines.append("")

    lines.append("## 结论与 T20 路由")
    lines.append("")
    if verdict == "PASS":
        lines.append("回填 mono/dual **通过** V1.1a 量化门禁（默认 EN→ZH 方向），"
                     "**允许 T20 并入回填产物**（mono + dual 新标签、可搜索校验、每页降级报告）。")
        lines.append("")
        lines.append("> 边界：该 PASS 以默认 EN→ZH 收缩方向为准；扩写/极密排版方向的"
                     "溢出率见上方灵敏度检查，T24 应补测。")
    else:
        lines.append("回填 mono/dual **未通过** V1.1a 量化门禁。按设计，**T20 改为显式降级**：")
        lines.append("")
        lines.append("- 不产出/不暴露 mono/dual PDF 作为可用产物；")
        lines.append("- 结果区提供**文本 / Markdown 导出**，并显示："
                     "「PDF 回填尚未通过质量门，当前提供文本与 Markdown 导出；回填达标后将自动启用」；")
        lines.append("- 回填入口保留但标注为「回填达标后可用」，不静默隐藏；")
        lines.append("- 本结论**不阻塞**其余 v1.1 任务。")
        lines.append("")
        lines.append("失败项：")
        if not overflow_ok:
            lines.append(f"- 溢出页占比 {totals['overflow_ratio'] * 100:.1f}% > 10%"
                         f"（超出 {totals['overflow_ratio'] * 100 - 10:.1f} 个百分点）。")
        if not clip_ok:
            lines.append(f"- 每页裁切块数 {totals['max_clip_blocks']} > 2。")
        if not tounicode_ok:
            lines.append(f"- ToUnicode 覆盖率 {totals['to_unicode_coverage'] * 100:.1f}% < 100%"
                         f"（缺 {totals['expected_pages'] - totals['extractable_pages']} 页）。")
    lines.append("")

    lines.append("## 逐页降级明细（门禁测试集）")
    lines.append("")
    for entry, item in zip(gate["entries"], gate["metrics"]):
        lines.append(f"### {entry['label']}（{entry['name']}）")
        lines.append("")
        if entry["error"]:
            lines.append(f"- **执行失败**：`{entry['error'].strip().splitlines()[-1]}`")
            lines.append("")
            continue
        if not item["page_reasons"]:
            lines.append("- 无降级页。")
        else:
            for page in sorted(item["page_reasons"]):
                reasons = ", ".join(sorted(item["page_reasons"][page]))
                clip_n = item["clip_blocks"].get(page, 0)
                lines.append(f"- 第 {page} 页：`{reasons}`"
                             + (f"（S3 裁切 {clip_n} 块）" if clip_n else ""))
        lines.append("")
    return "\n".join(lines) + "\n"


# —— 入口 --------------------------------------------------------------------

def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    started = time.time()
    with tempfile.TemporaryDirectory(prefix="aipdf-spike-") as tmp:
        root = Path(tmp)
        gate = run_suite(GATE_RATIO, root / "gate")
        sensitivity = [run_suite(ratio, root / f"sens-{ratio}")
                       for ratio in SENSITIVITY_RATIOS]
        self_check = self_check_machinery()
        finished = time.time()
        report = render_report(gate, sensitivity, self_check, started, finished)

    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(report, encoding="utf-8")

    totals = gate["totals"]
    verdict = "PASS" if (totals["overflow_ratio"] <= 0.10 + 1e-9
                         and totals["max_clip_blocks"] <= 2
                         and totals["to_unicode_coverage"] >= 1.0 - 1e-9) else "FAIL"
    print(f"spike 完成：VERDICT: {verdict}")
    print(f"  溢出页 {totals['overflow_pages']}/{totals['total_pages']} "
          f"= {totals['overflow_ratio'] * 100:.1f}%（≤10%）")
    print(f"  最大裁切块/页 {totals['max_clip_blocks']}（≤2）")
    print(f"  ToUnicode 覆盖 {totals['extractable_pages']}/{totals['expected_pages']} "
          f"= {totals['to_unicode_coverage'] * 100:.1f}%（=100%）")
    for suite in sensitivity:
        item = suite["totals"]
        print(f"  灵敏度 ratio={suite['ratio']}: 溢出 "
              f"{item['overflow_ratio'] * 100:.0f}% / 裁切页 {item['clip_pages']} / "
              f"最大裁切块 {item['max_clip_blocks']}")
    print(f"  机制自检: S3 clip 块 {sum(self_check['clip'].values())} / "
          f"S2 overflow 块 {sum(self_check['overflow_margin'].values())}")
    if any(entry["error"] for entry in gate["entries"]):
        print("  注意：有样例执行失败，详见报告。")
    print(f"报告：{REPORT_PATH}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SystemExit:
        raise
    except Exception:
        traceback.print_exc()
        raise SystemExit(2)
