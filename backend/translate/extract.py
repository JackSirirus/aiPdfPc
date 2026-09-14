# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""文本抽取与阅读序重建（翻译引擎 T3）。

把一页 PDF 的文本抽取成规范坐标系（`geometry.RectNorm`）内的 `TextBlock` 列表，
并按《翻译-回填排版规格.md》§2 重建阅读序：去连字、多栏检测与列内排序、段落重建。
扫描页（无文本层）走既有 OCR 路径（`vision_path` + `run_process`，与
`engine.ocr_doc()` 同源），识别结果同样归一化后进入同一套排序与段落流程。
抽取阶段同时对每个块做**不支持脚本**标注（§2.4）：RTL 文本或竖排行方向记入
`TextBlock.unsupported_script`，供翻译阶段跳过、回填阶段保留原文并记降级。

坐标事实：PyMuPDF `page.get_text("dict")` 的 bbox 本就是「未旋转 + 相对 CropBox」
的规范坐标（已在 PyMuPDF 1.28 实测：`bbox == bbox * rotation_matrix * derotation_matrix`），
因此文本块直接封装为 RectNorm，不再乘 derotation_matrix；只有来自可见页面图像
（OCR 结果）的坐标才经 `geometry.rect_normalize` 转换。该假设在 `_bbox_needs_normalization`
里做运行时校验（F5）：回环不一致时改写 bbox 为显式归一化结果，绝不把错坐标静默当成
规范坐标。本模块不修改任何输入文件，文档正文按不可信输入处理，仅返回内存结构。
"""
from __future__ import annotations

import json
import re
import tempfile
from dataclasses import dataclass
from pathlib import Path

import pymupdf as fitz

try:  # 作为包导入（tests 走 backend 顶层 → translate.extract）
    from . import detect, geometry
except ImportError:  # 直接以 backend 为 sys.path 顶层加载时
    import detect
    import geometry


# —— 规格 §2 参数 -----------------------------------------------------------
COLUMN_GAP_MIN = 24.0        # §2.2 列间空白带最小宽度（pt）
COLUMN_HEIGHT_RATIO = 0.55   # §2.2 空白带须跨越的页面高度比例
FULL_WIDTH_RATIO = 0.75      # 占内容宽度 ≥ 此比例视为通栏候选，不参与列缝判定
PARAGRAPH_GAP_FACTOR = 1.6   # §2.3 段内垂直间距上限（× line_height）
PARAGRAPH_SIZE_TOLERANCE = 1.5
LINE_BUCKET = 3.0            # §2.2 y0 近似相等阈值 / 排序分桶
INDENT_CHAR_RATIO = 0.5      # 单字符宽 ≈ 字号 × 该比例
INDENT_FACTOR = 1.5          # 首行缩进：≥ 1.5 字符宽 → 新段落
# 私有区 / CID 占比超过该阈值视为抽取低置信（规格 §9.1 extraction_low_conf）。
LOW_CONF_RATIO = 0.3
OCR_DPI = 216                # 与 engine.ocr_doc 一致
_EPS = 0.5

_PUA_RE = re.compile(r"[\ue000-\uf8ff\U000f0000-\U000ffffd\U00100000-\U0010fffd]")
_CID_RE = re.compile(r"\(cid:\d+\)")
# CJK / 假名 / 谚文没有词间空格，拼接时不插空格。
_CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\u3040-\u30ff\uac00-\ud7af]")
# bbox 回环一致性容差（pt）：矩阵乘积的浮点噪声远小于此值，语义不符则远大于。
_BBOX_EPS = 0.01


@dataclass
class TextBlock:
    """抽取出的一个段落块。

    `confidence` 为 0–1：1 表示文本干净，越低表示 PUA/CID 乱码占比越高；
    `low_confidence` 对应规格 §9.1 的 `extraction_low_conf`。`size` / `font` 供
    回填排版（T8）复用；OCR 块没有字体名时 `font` 为空串或 "ocr"。
    `unsupported_script` 为 `""` / `"rtl"` / `"vertical"`（§2.4）：非空表示该块
    文字方向不支持，翻译阶段应跳过、回填阶段保留原文并记降级。
    """

    page_no: int
    rect: geometry.RectNorm
    text: str
    confidence: float = 1.0
    size: float = 0.0
    font: str = ""
    unsupported_script: str = ""

    @property
    def low_confidence(self):
        return is_low_confidence(self.text)


@dataclass
class ExtractionResult:
    """整份文档的抽取结果与每页覆盖报告。"""

    blocks: list
    report: dict


# —— 内部分块（完成排序/段落重建前的中间结构） -------------------------------

@dataclass
class _Block:
    rect: geometry.RectNorm
    text: str
    size: float = 0.0
    font: str = ""
    bold: bool = False
    italic: bool = False
    confidence: float = 1.0
    column: int = 0
    region: bool = False
    band: int = 0
    script: str = ""


def suspicious_ratio(text):
    """PUA 字形与 `(cid:NNN)` 占位符在全文字符中的占比（0–1）。"""
    text = text or ""
    if not text:
        return 0.0
    suspicious = sum(len(m.group(0)) for m in _CID_RE.finditer(text))
    cleaned = _CID_RE.sub("", text)
    suspicious += sum(1 for ch in cleaned if _PUA_RE.match(ch))
    return suspicious / len(text)


def is_low_confidence(text, threshold=LOW_CONF_RATIO):
    """乱码占比是否超过阈值；对应规格 §9.1 `extraction_low_conf`。"""
    return suspicious_ratio(text) > threshold


# —— 文本拼接与去连字（§2.1） ----------------------------------------------

def _line_text(line):
    return "".join(span.get("text", "") for span in line.get("spans", []) or [])


def join_lines(lines, dehyphenate=True):
    """按阅读序拼接行文本，CJK 之间不插空格，并按 §2.1 规则去连字。"""
    result = ""
    for line in lines or []:
        result = _join_pair(result, line or "", dehyphenate)
    return result


def _join_pair(left, right, dehyphenate):
    if not left:
        return right
    if not right:
        return left
    if (dehyphenate and len(left) >= 2 and left.endswith("-")
            and left[-2].isalpha() and right[0].islower()):
        # 行尾 "-" 前是字母、下一行以小写字母开头 → 拼接（不做词典校验）。
        return left[:-1] + right
    if _CJK_RE.match(left[-1]) or _CJK_RE.match(right[0]):
        return left + right
    return left + " " + right


# —— 从文本层抽取（§2） ------------------------------------------------------

def _style_flags(spans):
    """span flags：bit1 斜体、bit4 粗体（PyMuPDF）。"""
    bold = any(int(span.get("flags", 0)) & 16 for span in spans)
    italic = any(int(span.get("flags", 0)) & 2 for span in spans)
    return bold, italic


def _bbox_needs_normalization(page, bbox):
    """`get_text("dict")` 的 bbox 是否**不在**规范坐标系（F5 运行时守卫）。

    本模块依赖「dict bbox 已是未旋转 + 相对 CropBox」这一事实；实现上用
    `bbox * rotation_matrix * derotation_matrix` 是否回到原值来验证。两个矩阵互逆，
    正常构建下回环恒等（仅浮点噪声）；一旦某个 PyMuPDF 版本/构建返回的是可见页坐标，
    回环将明显偏离，调用方据此改用 `geometry.rect_normalize(page, bbox * rotation_matrix)`
    显式归一化，而不是把错的坐标当规范坐标使用。

    矩阵不可用或形状异常时返回 False（保持既有行为，不因守卫本身失败而中断抽取）。
    """
    try:
        roundtrip = bbox * page.rotation_matrix * page.derotation_matrix
    except Exception:
        return False
    try:
        return any(abs(getattr(roundtrip, name) - getattr(bbox, name)) > _BBOX_EPS
                   for name in ("x0", "y0", "x1", "y1"))
    except Exception:
        return False


def _text_blocks_from_dict(page, dehyphenate):
    """由 `page.get_text("dict")` 构造内部块；bbox 已在规范坐标系内。"""
    rotation = geometry.page_rotation(page)
    blocks = []
    for raw in page.get_text("dict").get("blocks", []):
        if raw.get("type", 0) != 0:  # 图像块（type==1）不参与翻译
            continue
        lines = raw.get("lines", []) or []
        text = join_lines((_line_text(line) for line in lines), dehyphenate)
        if not text.strip():
            continue
        bbox = fitz.Rect(raw["bbox"])
        if _bbox_needs_normalization(page, bbox):
            # F5：dict bbox 不是规范坐标（回环不一致）→ 当作可见页坐标显式归一化。
            bbox = geometry.rect_normalize(page, bbox * page.rotation_matrix).rect
        spans = [span for line in lines for span in line.get("spans", []) or []]
        size = max((float(span.get("size", 0.0)) for span in spans), default=0.0)
        font = ""
        if spans:
            font = max(spans, key=lambda span: len(span.get("text", ""))).get("font", "")
        bold, italic = _style_flags(spans)
        dirs = [line.get("dir") for line in lines]
        blocks.append(_Block(
            rect=geometry.RectNorm(bbox.x0, bbox.y0, bbox.x1, bbox.y1, rotation),
            text=text, size=size, font=font, bold=bold, italic=italic,
            confidence=1.0 - suspicious_ratio(text),
            # dir 与 bbox 同在未旋转坐标；传入页面 rotation 后按可见方向判竖排（§2.4）。
            script=detect.classify_unsupported(text, dirs, rotation=rotation)))
    return blocks


# —— 扫描页 OCR 分支（复用 engine.ocr_doc 的调用方式） -----------------------

def _engine_module():
    """延迟导入 engine，避免与未来的 engine→translate 引用形成循环。"""
    try:
        import engine
    except ImportError:
        return None
    return engine


def _vision_ocr_boxes(page):
    """渲染可见页并调用系统 OCR helper，返回归一化 (x,y,w,h) 框列表。

    未安装 helper 时返回空列表，让该页按「无文本」降级，而不是让整篇失败。
    """
    engine = _engine_module()
    if engine is None:
        return []
    helper = engine.vision_path()
    if not helper:
        return []
    with tempfile.TemporaryDirectory(prefix="aipdf-translate-ocr-") as tmp:
        image = Path(tmp) / "page.png"
        page.get_pixmap(dpi=OCR_DPI).save(image)
        raw = engine.run_process([helper, "ocr", image], timeout=180)
        return json.loads(raw)


def _ocr_blocks(page, ocr_boxes):
    """无文本层页面的 OCR 块；`ocr_boxes` 可为注入的框列表或 `page -> boxes`。"""
    boxes = ocr_boxes(page) if callable(ocr_boxes) else ocr_boxes
    if boxes is None:
        boxes = _vision_ocr_boxes(page)
    if not boxes:
        return []
    visible_w, visible_h = page.rect.width, page.rect.height
    blocks = []
    for item in boxes:
        text = (item.get("text") or "").strip()
        if not text:
            continue
        x, y, w, h = (float(item[key]) for key in ("x", "y", "w", "h"))
        visible = fitz.Rect(x * visible_w, y * visible_h,
                            (x + w) * visible_w, (y + h) * visible_h)
        rect = geometry.rect_normalize(page, visible)
        blocks.append(_Block(rect=rect, text=text, size=max(rect.height * 0.8, 0.0),
                             font="ocr", confidence=1.0 - suspicious_ratio(text),
                             script=detect.classify_unsupported(text)))
    return blocks


# —— 多栏检测与阅读序（§2.2） -----------------------------------------------

def _merge_intervals(intervals):
    merged = []
    for start, end in sorted(intervals):
        if merged and start <= merged[-1][1] + _EPS:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    return merged


def _detect_gutters(blocks, page_height):
    """按 x 投影找列间空白带：无正文块横跨、宽度 ≥24pt、竖向跨度 ≥55% 页高。"""
    if len(blocks) < 2 or page_height <= 0:
        return []
    boundaries = sorted({round(blk.rect.x0, 3) for blk in blocks}
                        | {round(blk.rect.x1, 3) for blk in blocks})
    if len(boundaries) < 2:
        return []
    content_w = boundaries[-1] - boundaries[0]
    if content_w <= 0:
        return []
    body = [blk for blk in blocks if blk.rect.width < FULL_WIDTH_RATIO * content_w]
    if len(body) < 2:
        return []
    gutters = []
    for left, right in zip(boundaries, boundaries[1:]):
        if right - left < COLUMN_GAP_MIN:
            continue
        if any(blk.rect.x0 <= left + _EPS and blk.rect.x1 >= right - _EPS for blk in body):
            continue  # 有正文块横跨该区间 → 不是列缝
        neighbors = [blk for blk in body
                     if blk.rect.x1 <= left + _EPS or blk.rect.x0 >= right - _EPS]
        if not neighbors:
            continue
        span = max(blk.rect.y1 for blk in neighbors) - min(blk.rect.y0 for blk in neighbors)
        if span >= COLUMN_HEIGHT_RATIO * page_height:
            gutters.append((left, right))
    return _merge_intervals(gutters)


def _assign_columns(blocks, gutters):
    """列缝切出列；横跨列缝的通栏块标记为 region（§2.2 规则 7）。"""
    for blk in blocks:
        if any(blk.rect.x0 <= left + _EPS and blk.rect.x1 >= right - _EPS
               for left, right in gutters):
            blk.region = True
        center = (blk.rect.x0 + blk.rect.x1) / 2
        blk.column = sum(1 for left, right in gutters if center > (left + right) / 2)


def _order_blocks(blocks):
    """阅读序：通栏块按 y0 切分横带，带内先通栏后列，列内按 (y0,x0)（§2.2）。"""
    regions = sorted((blk for blk in blocks if blk.region),
                     key=lambda blk: (blk.rect.y0, blk.rect.x0))
    for blk in blocks:
        blk.band = sum(1 for region in regions if region.rect.y0 <= blk.rect.y0 + _EPS)
    return sorted(blocks, key=lambda blk: (
        blk.band, 0 if blk.region else 1, blk.column,
        round(blk.rect.y0 / LINE_BUCKET), blk.rect.x0))


# —— 段落重建（§2.3） --------------------------------------------------------

def _line_height(block):
    return max(block.size, 1.0) * 1.2


def _same_paragraph(prev, current):
    if prev.region or current.region:  # 通栏块（标题/表格）独立成段
        return False
    if prev.column != current.column:
        return False
    gap = current.rect.y0 - prev.rect.y1
    if gap < -LINE_BUCKET or gap >= PARAGRAPH_GAP_FACTOR * _line_height(prev):
        return False
    if abs(prev.size - current.size) > PARAGRAPH_SIZE_TOLERANCE:
        return False
    if (prev.bold, prev.italic) != (current.bold, current.italic):
        return False
    char_width = max(prev.size, current.size, 1.0) * INDENT_CHAR_RATIO
    if current.rect.x0 - prev.rect.x0 >= INDENT_FACTOR * char_width:
        return False  # 首行缩进明显 → 新段落
    return True


def _absorb(prev, current, dehyphenate):
    prev.text = _join_pair(prev.text, current.text, dehyphenate)
    prev.rect = geometry.RectNorm(
        min(prev.rect.x0, current.rect.x0), min(prev.rect.y0, current.rect.y0),
        max(prev.rect.x1, current.rect.x1), max(prev.rect.y1, current.rect.y1),
        prev.rect.rotation_deg)
    prev.confidence = min(prev.confidence, current.confidence)
    if not prev.script and current.script:
        prev.script = current.script


def _merge_paragraphs(blocks, dehyphenate):
    paragraphs = []
    for block in blocks:
        if paragraphs and _same_paragraph(paragraphs[-1], block):
            _absorb(paragraphs[-1], block, dehyphenate)
        else:
            paragraphs.append(block)
    return paragraphs


# —— 对外接口 ----------------------------------------------------------------

def extract_page(page, page_no=None, *, dehyphenate=True, ocr_boxes=None):
    """抽取并重建一页的阅读序，返回按阅读序排列的 `TextBlock` 列表。

    `page_no` 默认取 `page.number + 1`（1 基）。`ocr_boxes` 仅用于无文本层的
    扫描页；文本层存在时不会触发 OCR（不得重复识别已有文本层）。
    """
    if page_no is None:
        page_no = page.number + 1
    if page.get_text().strip():
        raw = _text_blocks_from_dict(page, dehyphenate)
    else:
        raw = _ocr_blocks(page, ocr_boxes)
    if not raw:
        return []
    gutters = _detect_gutters(raw, page.cropbox.height)
    _assign_columns(raw, gutters)
    paragraphs = _merge_paragraphs(_order_blocks(raw), dehyphenate)
    return [TextBlock(page_no=page_no, rect=blk.rect, text=blk.text,
                      confidence=round(blk.confidence, 4), size=blk.size, font=blk.font,
                      unsupported_script=blk.script)
            for blk in paragraphs if blk.text.strip()]


def extract_document(doc, *, pages=None, dehyphenate=True, ocr_boxes_by_page=None):
    """抽取整份文档并产出每页覆盖报告（规格 §9.2 结构）。

    `pages` 为 0 基页索引序列，默认全部；`ocr_boxes_by_page` 以页索引为键注入
    扫描页 OCR 框（测试/调试用）。零块的页在报告中记 `empty_page`，含低置信
    块的页记 `extraction_low_conf`。报告只含页码与原因，不含正文。
    """
    indices = range(len(doc)) if pages is None else pages
    blocks = []
    page_reports = []
    for index in indices:
        page = doc[index]
        injected = None if ocr_boxes_by_page is None else ocr_boxes_by_page.get(index)
        page_blocks = extract_page(page, page.number + 1,
                                   dehyphenate=dehyphenate, ocr_boxes=injected)
        blocks.extend(page_blocks)
        reasons = []
        if not page_blocks:
            reasons.append("empty_page")
        elif any(block.low_confidence for block in page_blocks):
            reasons.append("extraction_low_conf")
        page_reports.append({"page": page.number + 1, "degraded": bool(reasons),
                             "reasons": reasons})
    report = {
        "pages": page_reports,
        "summary": {
            "degraded_pages": sum(1 for item in page_reports if item["degraded"]),
            "total_pages": len(page_reports),
        },
    }
    return ExtractionResult(blocks=blocks, report=report)
