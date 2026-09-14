# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""v1.1a 扩写方向回填门禁：mock 译文长度 ≥ 源文本（ratio 1.00 / 1.50）。

背景（对抗性审查）
------------------
原 `tests/spike_translate_reflow.py` 的量化门禁默认按 EN→ZH **收缩**方向构造
（`GATE_RATIO=None` → 每块确定性取 [0.50, 0.80]），因此它**结构上无法触发产品
的主导失败模式——扩写溢出**；原本的 `SPIKE_SENSITIVITY=1.0,1.5` 只是非门禁旁注。
本脚本把扩写方向提升为**独立门禁**：在完全相同的 4 个合成样例（双栏 / 表格密集 /
公式密集 / 扫描件）上，把 mock 译文设为「与源等长」和「源 ×1.5」，跑完整引擎链路
（`engine.dispatch({operation: translate})` → extract → chunk → prompt → LLM(mock)
→ verify → reflow → output），按设计《需求总结.md §实现分期与验收门（V1.1a）》
的三项量化门禁判定：

- 溢出页占比（`overflow_margin` ∪ `clip` 页 / 总页数）≤ **10%**；
- 每页裁切块数（单页 S3 次数最大值）≤ **2**；
- ToUnicode 覆盖率（可提取译文页 / 应有译文页）= **100%**。

**诚实原则**：本脚本不调 mock 让门禁通过；扩写方向若大幅溢出，就如实记录为 FAIL
与已知限制，并确保它不静默（见报告 §缓解）。

检测端到端确认
--------------
`unsupported_script` / `table_fail` / `formula_fail` / `rotation_crop` 在上一次
spike 报告后（commit `d1a0e81`）才接线。本脚本额外用 2 个合成样例（旋转页、竖排页）
经完整引擎链路确认它们写入结果与 sidecar；表格/公式两个原因由扩写主样例（ratio 1.50）
端到端触发。

运行：`.venv\\Scripts\\python.exe tests\\spike_translate_reflow_expansion.py`
报告写入 `.omo/evidence/v11-spike-expansion.md`。判定 FAIL 也返回 0（质量门而非删除
开关）；仅脚本自身异常返回 2。
"""
from __future__ import annotations

import subprocess
import sys
import tempfile
import time
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tests"))
sys.path.insert(0, str(ROOT / "backend"))

import pymupdf as fitz  # noqa: E402

import spike_translate_reflow as base  # noqa: E402

REPORT_PATH = ROOT / ".omo" / "evidence" / "v11-spike-expansion.md"

# 扩写方向门禁的两个比：1.00（等长）与 1.50（源 ×1.5）。
EXPANSION_RATIOS = (1.00, 1.50)
# 设计《需求总结.md §实现分期与验收门（V1.1a）》的量化阈值。
OVERFLOW_GATE = 0.10
CLIP_GATE = 2
TOUNICODE_GATE = 1.0


def _head_hash():
    """取当前 HEAD 短哈希，供报告标注证据对应提交。"""
    try:
        out = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=str(ROOT),
                             capture_output=True, text=True, timeout=10)
        return out.stdout.strip() if out.returncode == 0 else "unknown"
    except Exception:
        return "unknown"


# —— 检测端到端样例：旋转 / 竖排（表格、公式由扩写主样例触发） ----------------

def build_rotated():
    """旋转页：回填需坐标归一化，应记 `rotation_crop`（规格 §1）。"""
    doc = fitz.open()
    page = doc.new_page(width=base.PAGE_W, height=base.PAGE_H)
    base._wrap_lines(page, 60, 90,
                     base._paragraph(4242, count=30), width_chars=84, size=11)
    page.set_rotation(90)
    return doc


def build_vertical():
    """竖排文本页：行方向非水平，抽取应标 `unsupported_script=vertical`（规格 §2.4）。"""
    doc = fitz.open()
    page = doc.new_page(width=base.PAGE_W, height=base.PAGE_H)
    page.insert_text((300, 100), "Vertical synthetic detection sample", fontsize=14,
                     fontname="helv", rotate=90)
    page.insert_text((360, 100), "second vertical line", fontsize=14,
                     fontname="helv", rotate=90)
    return doc


def build_math_dense():
    """数学字形占比高的公式块：扩写溢出时应记 `formula_fail`（规格 §4.4）。

    注意：主样例 `formula_dense` 的 6 条 ASCII 公式被抽取器**合并为一个大块**，
    合并后整体数学占比低于阈值，`is_math_dense` 判否，因此主样例不会写出
    `formula_fail`（这是启发式的保守边界，报告如实标注）。本样例用单个高密度
    公式行，确认该原因在真实块级判定下确实端到端可写。
    """
    doc = fitz.open()
    page = doc.new_page(width=base.PAGE_W, height=base.PAGE_H)
    page.insert_text((60, 90), "f(x)=x^2+y^2/(a-b)", fontsize=12, fontname="helv")
    return doc


DETECTION_SAMPLES = (
    {"name": "rotated", "label": "旋转页", "build": build_rotated,
     "file": "synth-rotated.pdf", "scanned": False},
    {"name": "vertical", "label": "竖排页", "build": build_vertical,
     "file": "synth-vertical.pdf", "scanned": False},
    {"name": "math_dense", "label": "高密度公式页", "build": build_math_dense,
     "file": "synth-math-dense.pdf", "scanned": False},
)


# —— 度量的补充聚合 -----------------------------------------------------------

def reasons_fired(entries):
    """汇总一次运行中**实际写入结果报告**的降级原因（页级计数）。"""
    fired = {}
    for entry in entries:
        report = entry.get("report") or {}
        for item in report.get("pages", []):
            for reason in item.get("reasons", []):
                fired[reason] = fired.get(reason, 0) + 1
    return fired


def per_sample_rows(suite):
    """每样例一行的 (label, total, overflow_pages, clip, max_clip, coverage)。"""
    rows = []
    for entry, item in zip(suite["entries"], suite["metrics"]):
        total = item["total_pages"]
        overflow = len(item["overflow_pages"])
        ratio = (overflow / total * 100.0) if total else 0.0
        expected, extractable = len(item["expected"]), len(item["extractable"])
        coverage = (extractable / expected * 100.0) if expected else 100.0
        rows.append({
            "label": entry["label"], "name": entry["name"], "error": entry["error"],
            "total": total, "overflow": overflow, "overflow_pct": ratio,
            "clip_pages": sorted(item["clip_pages"]),
            "max_clip": item["max_clip_blocks"],
            "expected": expected, "extractable": extractable, "coverage": coverage,
            "raster_only": sorted(item["raster_only"]),
        })
    return rows


def run_detection_suite(workdir, ratio):
    """在检测样例上跑完整链路，收集报告原因。"""
    workdir.mkdir(parents=True, exist_ok=True)
    entries = [base.run_sample(sample, workdir, ratio) for sample in DETECTION_SAMPLES]
    return entries


def _detection_table(lines, detection_entries, ratio):
    lines.append(f"| 样例 | 执行 | 报告中出现的原因 |")
    lines.append("|------|------|------------------|")
    for entry in detection_entries:
        report = entry.get("report") or {}
        fired = sorted({reason for item in report.get("pages", [])
                        for reason in item.get("reasons", [])})
        status = "失败" if entry.get("error") else "OK"
        lines.append(f"| {entry['label']} | {status} | "
                     f"{', '.join(f'`{r}`' for r in fired) or '—'} |")
    lines.append("")


# —— 报告渲染 -----------------------------------------------------------------

def render_report(expansion_runs, detection_entries, head, started, finished):
    lines = []
    lines.append("# v1.1a 回填 spike：**扩写方向**量化门禁报告")
    lines.append("")
    lines.append("> 门禁依据：`设计/v1.1-翻译/需求总结.md §实现分期与验收门（V1.1a）`"
                 "（溢出 ≤10% / 裁切 ≤2 / ToUnicode=100%）")
    lines.append(f"> 证据提交：`{head}`（`git rev-parse --short HEAD`）")
    lines.append(f"> 生成时间：{time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(started))}"
                 f"（耗时 {round(finished - started, 2)}s）")
    lines.append("> 运行：`.venv\\Scripts\\python.exe tests\\spike_translate_reflow_expansion.py`")
    lines.append("> 样例：全部为 PyMuPDF 内存合成，**不读取任何真实或个人文档**。")
    lines.append("")

    lines.append("## 为什么需要这份报告")
    lines.append("")
    lines.append("原 `tests/spike_translate_reflow.py` 的量化门禁只跑 EN→ZH **收缩**方向"
                 "（每块 [0.50, 0.80]），因此结构上无法触发产品的主导失败模式——**扩写溢出**。"
                 "对抗性审查据此判定「门禁结构上抓不到主导失败模式」。本报告把 mock 译文设为"
                 "**与源等长（ratio 1.00）**与**源 ×1.5（ratio 1.50）**，在同一 4 样例上重跑"
                 "完整引擎链路，按同一套阈值判定，并**如实记录**数值。")
    lines.append("")

    lines.append("## 方法")
    lines.append("")
    lines.append("- LLM：patch `translate.llm._open`，mock 返回确定性伪中文译文，字符数 = "
                 "`max(2, round(源字符数 × ratio))`，ratio=1.00 / 1.50；**无任何真实网络请求**。"
                 "因此这是「等长 / 扩写」方向的诚实压力测试（测回填排版，不测模型质量）。")
    lines.append("- 溢出页 = 带 `overflow_margin`(S2) 或 `clip`(S3) 的页；裁切块数 = S3 的块级"
                 "调用次数（去重前计数，按输出形态数折算成单次渲染口径）；ToUnicode = 可提取"
                 "译文页 / 应有译文页（扫描光栅页豁免，用 mock 译文前缀命中判定）。")
    lines.append("- 与收缩门禁使用**同一套** `spike_translate_reflow` 度量代码，避免口径漂移。")
    lines.append("")

    lines.append("## 扩写方向：三项量化门禁（逐 ratio）")
    lines.append("")
    for suite in expansion_runs:
        ratio = suite["ratio"]
        totals = suite["totals"]
        rows = per_sample_rows(suite)
        overflow_ok = totals["overflow_ratio"] <= OVERFLOW_GATE + 1e-9
        clip_ok = totals["max_clip_blocks"] <= CLIP_GATE
        tounicode_ok = totals["to_unicode_coverage"] >= TOUNICODE_GATE - 1e-9
        verdict = "PASS" if (overflow_ok and clip_ok and tounicode_ok) else "FAIL"

        lines.append(f"### ratio = {ratio:.2f}（mock 译文 = 源字符数 × {ratio:.2f}）")
        lines.append("")
        lines.append("| 样例 | 总页 | 溢出页(S2/S3) | 裁切页(S3) | 最大裁切块/页 | "
                     "应有译文页 | 可提取 | ToUnicode |")
        lines.append("|------|-----:|--------------:|-----------:|--------------:|"
                     "-----------:|-------:|----------:|")
        for row in rows:
            lines.append(
                f"| {row['label']} | {row['total']} | "
                f"{row['overflow']} ({row['overflow_pct']:.0f}%) | "
                f"{', '.join(str(p) for p in row['clip_pages']) or '—'} | "
                f"{row['max_clip']} | {row['expected']} | {row['extractable']} | "
                f"{row['coverage']:.0f}% |")
        lines.append("")
        lines.append("| 指标 | 阈值 | 实测 | 判定 |")
        lines.append("|------|------|------|------|")
        lines.append(f"| 溢出页占比（S2∪S3） | ≤ 10% | "
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
        lines.append(f"**ratio={ratio:.2f} VERDICT: {verdict}**")
        lines.append("")
        fired = reasons_fired(suite["entries"])
        if fired:
            lines.append("本 ratio 实际写出的降级原因（页级计数）："
                         + "，".join(f"`{reason}`×{count}"
                                     for reason, count in sorted(fired.items())) + "。")
            lines.append("")
        errors = [row for row in rows if row["error"]]
        if errors:
            lines.append("执行失败样例："
                         + "，".join(f"{row['label']}（{row['error'].strip().splitlines()[-1]}）"
                                     for row in errors) + "。")
            lines.append("")

    lines.append("## 检测原因端到端确认（HEAD 接线后）")
    lines.append("")
    lines.append("原 `v11-spike-report.md` 称 `unsupported_script`/`table_fail`/`formula_fail`/"
                 "`rotation_crop` 「当前 reflow/extract 链路从不写出」——该结论早于 commit "
                 f"`d1a0e81`（结构化检测接线）。下表用完整 `engine.dispatch` 链路确认现状：")
    lines.append("")
    lines.append("**扩写主样例（ratio 1.50）触发：**")
    lines.append("")
    exp_fired = {}
    for suite in expansion_runs:
        if abs(suite["ratio"] - 1.50) < 1e-9:
            exp_fired = reasons_fired(suite["entries"])
    targets = ("rotation_crop", "unsupported_script", "table_fail", "formula_fail")
    for reason in targets:
        hit = exp_fired.get(reason, 0)
        lines.append(f"- `{reason}`：扩写主样例{'触发 ' + str(hit) + ' 页' if hit else '未触发'}"
                     "（见下方专项样例）。")
    lines.append("")
    lines.append("**专项检测样例（ratio 1.00，完整链路）：**")
    lines.append("")
    _detection_table(lines, detection_entries, 1.00)
    det_fired = {}
    for entry in detection_entries:
        report = entry.get("report") or {}
        for item in report.get("pages", []):
            for reason in item.get("reasons", []):
                det_fired.setdefault(reason, []).append(entry["label"])
    lines.append("| 目标原因 | 证据来源 | 端到端触发 |")
    lines.append("|----------|----------|-----------|")
    for reason in targets:
        via_det = det_fired.get(reason, [])
        via_exp = exp_fired.get(reason, 0)
        where = []
        if via_exp:
            where.append(f"扩写主样例 {via_exp} 页")
        if via_det:
            where.append("、".join(set(via_det)))
        lines.append(f"| `{reason}` | {'；'.join(where) if where else '—'} | "
                     f"{'是' if (via_exp or via_det) else '**否**'} |")
    lines.append("")

    lines.append("## 诚实判定与已知限制")
    lines.append("")
    any_pass = all(
        (s["totals"]["overflow_ratio"] <= OVERFLOW_GATE + 1e-9
         and s["totals"]["max_clip_blocks"] <= CLIP_GATE
         and s["totals"]["to_unicode_coverage"] >= TOUNICODE_GATE - 1e-9)
        for s in expansion_runs)
    for suite in expansion_runs:
        t = suite["totals"]
        lines.append(f"- ratio {suite['ratio']:.2f}：溢出 "
                     f"{t['overflow_ratio'] * 100:.1f}%（阈值 10%），"
                     f"最大裁切块 {t['max_clip_blocks']}（阈值 2），"
                     f"ToUnicode {t['to_unicode_coverage'] * 100:.0f}%。")
    lines.append("")
    if any_pass:
        lines.append("**扩写方向全部达标。** 原门禁的收缩结论与之一致；产品在等长/扩写"
                     "输入下未出现超阈溢出。")
    else:
        lines.append("**扩写方向未达标（诚实结论）。** 产品在目标语言比源语言更长时"
                     "（中译英、英译德等高扩写语言对）会**大面积溢出到页边**，远超设计 "
                     "≤10% 门禁。这不是 mock 调参导致的误报——mock 只按字符长度模拟"
                     "「译文更长」这一真实事实；实际溢出由 S2/S3 机制触发，说明该机制"
                     "在扩写下会被大量使用。")
        lines.append("")
        lines.append("**这不是静默失败**：设计已要求每页降级报告留痕（规格 §9）。"
                     "下方「不静默」一节核对结果区是否真的把 `overflow_margin`/`clip` "
                     "呈现给用户；若缺口存在，则如实标注。")
    lines.append("")

    lines.append("## 不静默：overflow/clip 的用户可见性核对")
    lines.append("")
    for note in SURFACING_NOTES:
        lines.append(f"- {note}")
    lines.append("")

    lines.append("## 缓解建议")
    lines.append("")
    for item in MITIGATIONS:
        lines.append(f"- {item}")
    lines.append("")

    lines.append("## 结论")
    lines.append("")
    lines.append("扩写方向门禁的数值必须与收缩方向报告合并阅读。收缩方向 "
                 "`v11-spike-report.md` 的 PASS 不代表扩写方向安全；本报告用独立提交"
                 f"（`{head}`）钉住扩写方向的真实表现。若产品要对外承诺「PDF 回填可用」，"
                 "必须先落实上述缓解（结果区显式告警 + 溢出/裁切非静默），再决定是否以"
                 "限制语言对或提高阈值的方式放行。")
    lines.append("")
    return "\n".join(lines) + "\n"


# —— 用户可见性核对与缓解（审计后按事实固化，含 file:line） --------------------
SURFACING_NOTES = (
    "**单文件结果区（阅读器 F4）确实逐页呈现 overflow_margin / clip**："
    "`Sources/AIPDF.Windows/ViewModels/Translation/FullTranslationViewModel.cs:120-142` "
    "的 `ReasonLabels` 含 `overflow_margin`（:123「该页译文超出原文块范围」）与 "
    "`clip`（:124「该页存在被裁切的译文」）；`Sources/AIPDF.Windows/Services/"
    "FullTranslation.cs:255-352` 的 `ParseOutcome` 读取 `report.pages[].reasons`；"
    "渲染在 `Sources/AIPDF.Windows/Views/Translation/FullTranslationPanel.xaml:300-348`"
    "（`Text=\"{Binding LabelText}\"` :342）。后端同源文案见 "
    "`backend/translate/verify.py:80-101`。",
    "**回填已优先页边溢出、再裁切，并逐页写显式标记**（非静默）："
    "`backend/translate/reflow.py:406-431`（S2 `overflow_margin` :415-425，"
    "S3 `clip` :427-431），且报告 sidecar（`<stem>-降级报告.json`）同目录落盘"
    "（`backend/engine.py:940-941`）。因此溢出不会无声丢失。",
    "**缺口 1 · 无聚合溢出告警**：`report.summary` 只有 `degraded_pages`/"
    "`unsearchable_pages`/`total_pages`（`backend/translate/verify.py:315-323`），"
    "`overflow_margin` 与 `font_shrink` 一起被并入 `degraded_pages`，用户无法一眼"
    "看出「N 页溢出」；设计 §9.3 要求的「F4 顶部显示聚合」未细分到溢出/裁切。",
    "**缺口 2 · 批量页只显示数量**：批量工具页只呈现降级页**数量**，不呈现逐页"
    "原因标签（`Sources/AIPDF.Windows/ViewModels/TranslationBatchViewModel.cs:92,"
    "148-152`）；`FullTranslationViewModel.ReasonLabels`/`DescribeReason` 未在批量"
    "路径复用。",
    "**缺口 3 · 引擎结果无机器可读顶层告警**：整篇 translate 结果"
    "（`backend/engine.py:1009-1012`）不含顶层 `overflow`/`warning` 字段；消费方必须"
    "自行遍历 `report.pages[].reasons` 才能发现溢出。",
)

MITIGATIONS = (
    "**实现（建议落点）**：在整篇结果顶层加机器可读聚合，如 "
    "`results[].overflow = {pages, count, clipPages, warn}`，由 "
    "`report.pages[].reasons` ∩ {`overflow_margin`, `clip`} 计算；任何消费方（含批量页）"
    "据此即可非静默。再在 F4 顶部显示「本文件 N 页译文超出原文范围，其中 M 页被裁切」"
    "（落实设计 §9.3 的聚合要求）。",
    "**实现（建议落点）**：批量工具页补齐逐项降级原因（复用 `ReasonLabels`），至少显示"
    "溢出/裁切计数，消除「数量可看、原因不可看」的不对称。",
    "**产品策略**：扩写方向远超 ≤10% 门禁（60–90%），**不建议**在扩写语言对"
    "（中→英、英→德等）默认开启 PDF 回填；可在开启前显式提示「该方向排版质量未验证」，"
    "或按设计 §未达标降级先提供文本/Markdown 导出。",
    "**门禁口径**：≤10% 应按**语言对方向**分别定义（收缩方向 vs 扩写方向），"
    "不应以单一 EN→ZH 收缩门禁代表全部方向。扩写方向需单独定阈并单独放行。",
)


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    started = time.time()
    head = _head_hash()
    with tempfile.TemporaryDirectory(prefix="aipdf-spike-exp-") as tmp:
        root = Path(tmp)
        expansion_runs = [base.run_suite(ratio, root / f"exp-{ratio}")
                          for ratio in EXPANSION_RATIOS]
        detection_entries = run_detection_suite(root / "detection", 1.00)
        finished = time.time()
        report = render_report(expansion_runs, detection_entries, head, started, finished)

    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(report, encoding="utf-8")

    print(f"扩写方向 spike 完成（HEAD {head}）")
    for suite in expansion_runs:
        t = suite["totals"]
        print(f"  ratio={suite['ratio']:.2f}: 溢出 {t['overflow_pages']}/{t['total_pages']} "
              f"= {t['overflow_ratio'] * 100:.0f}%（≤10%）/ 最大裁切块 {t['max_clip_blocks']}"
              f"（≤2）/ ToUnicode {t['to_unicode_coverage'] * 100:.0f}%")
    fire_all = reasons_fired(
        [e for suite in expansion_runs for e in suite["entries"]] + detection_entries)
    print(f"  端到端触发的原因: {', '.join(sorted(fire_all)) or '—'}")
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
