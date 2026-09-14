# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""结构化降级检测启发式（翻译引擎 T8 辅助）。

《翻译-回填排版规格.md》§1/§2.4/§4.4 要求对四类难以忠实回填的内容**显式降级标注**，
而不是静默损坏：

- `rotation_crop`：页面带 /Rotate 或 CropBox ≠ MediaBox，回填需坐标归一化（§1）。
- `unsupported_script`：RTL（阿拉伯语/希伯来语等，占比 > 30%）或竖排文本，保留原文（§2.4）。
- `formula_fail`：数学字形占比高的公式块，缩放至下限仍溢出（§4.4）。
- `table_fail`：短块按行/列规则对齐形成的表格，合并/不规则或单元格溢出（§4.4）。

本模块只做**保守的启发式检测**：仅依赖标准库；涉及页面矢量线段的判定直接读取传入的
PyMuPDF 页面对象，不引入模型或第三方依赖。这是设计里的「尽力」层，宁可多标注、不可
静默损坏；阈值取偏严一侧，降低正常文本的误报。检测本身不修改任何文件，也不读写用户文档。
"""
from __future__ import annotations

import unicodedata


# —— RTL / 竖排（§2.4） -------------------------------------------------------
RTL_RATIO_THRESHOLD = 0.30   # RTL 字符占非空白字符比例超过此值 → 不支持
VERTICAL_DIR_RATIO = 0.50    # 行方向中竖排（|dy| > |dx|）占比

# —— 公式（§4.4） -------------------------------------------------------------
MATH_RATIO_THRESHOLD = 0.30  # 数学字形占非空白字符比例
MATH_MIN_COUNT = 3           # 至少出现这么多个数学字形才可能是公式

# —— 表格（§4.4） -------------------------------------------------------------
TABLE_MIN_ROWS = 3           # 至少这么多行
TABLE_MIN_COLS = 2           # 至少这么多列
TABLE_MIN_CELLS = 6          # 至少这么多短块
TABLE_MAX_CELL_CHARS = 40    # 「单元格」文本长度上限，超过视为普通段落
TABLE_MIN_FILL = 0.50        # 命中单元格 / (行×列) 的下限

# —— 几何（§1） ---------------------------------------------------------------
GEOMETRY_EPS = 0.5           # CropBox 与 MediaBox 逐坐标比较容差（pt）

# ASCII 运算符 + 常见数学/下标上标符号；不含括号（正文常见，易误报）。
_MATH_ASCII = frozenset("+-=*/^_<>")
_MATH_SYMBOLS = frozenset(
    "±×÷∓∑∏∫∮√∞≈≠≤≥≡∂∇∈∉⊂⊃∪∩∀∃∅∆⋅∘∙⊕⊗⊥∥∠⊤⊢⊨∝∵∴"
)
_SUB_SUP = frozenset("₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿ")


# —— RTL / 竖排 ---------------------------------------------------------------

def _is_rtl_char(char):
    """字符是否属于 RTL 脚本（Unicode 双向类 R/AL，覆盖阿拉伯、希伯来等）。"""
    return unicodedata.bidirectional(char) in ("R", "AL")


def rtl_ratio(text):
    """RTL 字符占非空白字符的比例（0–1）；空文本返回 0.0。"""
    chars = [char for char in (text or "") if not char.isspace()]
    if not chars:
        return 0.0
    return sum(1 for char in chars if _is_rtl_char(char)) / len(chars)


def is_rtl_text(text, threshold=RTL_RATIO_THRESHOLD):
    """是否以 RTL 为主（§2.4：占比 > `threshold`）。"""
    return rtl_ratio(text) > threshold


def is_vertical_lines(dirs, ratio=VERTICAL_DIR_RATIO, rotation=0):
    """行方向多为竖排时返回 True（仅在 axis-preserving 旋转下判定）。

    `dirs` 为 PyMuPDF `line["dir"]` 的 (dx, dy) 序列；横排约 (1,0)/(-1,0)，
    竖排约 (0,±1)。以 |dy| > |dx| 判定单行竖排，再要求占比达 `ratio`。

    **/Rotate 90/270 会把内容坐标系的 x/y 轴对调**：此时未旋转 `dir` 的「竖」在可见
    页面可能是「横」，反之亦然（扫描件侧放、靠 /Rotate 摆正即属此类）。这类页面的
    竖排判定不可靠，一律不判竖排——宁可翻译一张旋转页，也不误判整块跳过（不翻译）。
    仅当 `rotation` 为 0/180（轴不交换）时才用 `dir` 判竖排。`geometry.page_rotation`
    已保证 rotation ∈ {0,90,180,270}。
    """
    if int(rotation or 0) % 180 != 0:
        return False
    normalized = [tuple(direction) for direction in (dirs or []) if direction]
    if not normalized:
        return False
    vertical = sum(1 for dx, dy in normalized if abs(dy) > abs(dx) + 1e-6)
    return vertical / len(normalized) >= ratio


def classify_unsupported(text, dirs=None, *, rtl_threshold=RTL_RATIO_THRESHOLD,
                         vertical_ratio=VERTICAL_DIR_RATIO, rotation=0):
    """返回块的不支持脚本类型：`"rtl"` / `"vertical"` / `""`（正常）。

    先判 RTL（文本可得），再判竖排（需行方向 `dirs` 与页面 `rotation`）。二者都命中
    时以 RTL 优先，因为 RTL 文本无论如何都不能按 LTR 回填。旋转 90/270 的页面不做
    竖排判定（轴交换导致 `dir` 不可靠，见 `is_vertical_lines`），避免把正常文本整块
    误判为竖排而不翻译。
    """
    if is_rtl_text(text, rtl_threshold):
        return "rtl"
    if is_vertical_lines(dirs, vertical_ratio, rotation):
        return "vertical"
    return ""


# —— 公式 ---------------------------------------------------------------------

def is_math_char(char):
    """是否为数学/运算符/上下标字形。"""
    if char in _MATH_ASCII or char in _MATH_SYMBOLS or char in _SUB_SUP:
        return True
    return unicodedata.category(char) == "Sm"


def math_ratio(text):
    """数学字形占非空白字符的比例（0–1）；空文本返回 0.0。"""
    chars = [char for char in (text or "") if not char.isspace()]
    if not chars:
        return 0.0
    return sum(1 for char in chars if is_math_char(char)) / len(chars)


def is_math_dense(text, threshold=MATH_RATIO_THRESHOLD, min_count=MATH_MIN_COUNT):
    """是否为公式候选块（§4.4）：数学字形占比高且数量足够。"""
    chars = [char for char in (text or "") if not char.isspace()]
    if not chars:
        return False
    count = sum(1 for char in chars if is_math_char(char))
    if count < min_count:
        return False
    return count / len(chars) > threshold


# —— 表格 ---------------------------------------------------------------------

def _median(values):
    if not values:
        return 0.0
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return float(ordered[middle])
    return (ordered[middle - 1] + ordered[middle]) / 2.0


def _cluster(values, tolerance):
    """把一维坐标按容差聚成簇，返回各簇均值（升序）。"""
    groups = []
    for value in sorted(values):
        if groups and value - groups[-1][-1] <= tolerance:
            groups[-1].append(value)
        else:
            groups.append([value])
    return [sum(group) / len(group) for group in groups]


def _nearest(value, centers, tolerance):
    """返回最接近 `value` 的簇下标；超出容差返回 None。"""
    if not centers:
        return None
    best = min(range(len(centers)), key=lambda index: abs(centers[index] - value))
    return best if abs(centers[best] - value) <= tolerance else None


def table_grid(blocks, *, min_rows=TABLE_MIN_ROWS, min_cols=TABLE_MIN_COLS,
               min_cells=TABLE_MIN_CELLS, max_cell_chars=TABLE_MAX_CELL_CHARS,
               min_fill=TABLE_MIN_FILL):
    """检测规则表格结构（§4.4）。

    只把**短、单行文本块**当作单元格候选；按块左上角 y0 聚行、x0 聚列。命中
    ≥`min_rows` 行、≥`min_cols` 列、≥`min_cells` 个单元格，且填充率达 `min_fill`
    时返回结构字典：`{rows, cols, cells, fill, irregular}`；否则返回 None。

    `irregular=True` 表示存在缺失列位（跨行/跨列合并）或填充不足，行内回填无法还原，
    调用方应记 `table_fail`。`cells` 为命中块的原始下标集合。
    """
    cells = []
    for index, block in enumerate(blocks or []):
        text = (getattr(block, "text", "") or "").strip()
        rect = getattr(block, "rect", None)
        if not text or rect is None or "\n" in text or len(text) > max_cell_chars:
            continue
        cells.append((index, rect))
    if len(cells) < min_cells:
        return None

    row_tol = max(3.0, _median([rect.height for _, rect in cells]) * 0.5)
    col_tol = max(3.0, _median([rect.width for _, rect in cells]) * 0.5)
    row_centers = _cluster([rect.y0 for _, rect in cells], row_tol)
    col_centers = _cluster([rect.x0 for _, rect in cells], col_tol)
    if len(row_centers) < min_rows or len(col_centers) < min_cols:
        return None

    row_counts = [0] * len(row_centers)
    cell_indices = set()
    for index, rect in cells:
        row = _nearest(rect.y0, row_centers, row_tol)
        col = _nearest(rect.x0, col_centers, col_tol)
        if row is None or col is None:
            continue
        row_counts[row] += 1
        cell_indices.add(index)
    if len(cell_indices) < min_cells:
        return None

    fill = len(cell_indices) / (len(row_centers) * len(col_centers))
    if fill < min_fill:
        return None
    irregular = any(count < len(col_centers) for count in row_counts)
    return {
        "rows": len(row_centers),
        "cols": len(col_centers),
        "cells": cell_indices,
        "fill": round(fill, 4),
        "irregular": bool(irregular),
    }


def has_grid_lines(page, *, min_vertical=2, min_horizontal=2, tolerance=0.5):
    """页面是否含矢量线段网格（表格线的辅助信号，§4.4）。"""
    vertical, horizontal = set(), set()
    for drawing in page.get_drawings():
        for item in drawing.get("items", []) or []:
            if not item or item[0] != "l":
                continue
            start, end = item[1], item[2]
            if abs(start.x - end.x) <= tolerance:
                vertical.add(round(start.x, 1))
            elif abs(start.y - end.y) <= tolerance:
                horizontal.add(round(start.y, 1))
    return len(vertical) >= min_vertical and len(horizontal) >= min_horizontal


def _is_short_single_line(block):
    text = (getattr(block, "text", "") or "").strip()
    return bool(text) and "\n" not in text and len(text) <= TABLE_MAX_CELL_CHARS


def table_structure(page, blocks):
    """页面级表格结构检测：块级网格优先，矢量网格线兜底（§4.4）。

    PyMuPDF 常把同一行的多个单元格并进一个文本块，块级 `table_grid` 未必命中；
    此时若页面存在矢量线段网格、且有 ≥`TABLE_MIN_ROWS` 个短单行块，仍判为表格。
    返回结构字典（含 `source` 字段：`"blocks"` / `"lines"`）；无表格返回 None。
    """
    grid = table_grid(blocks)
    if grid is not None:
        grid["source"] = "blocks"
        return grid
    short = {index for index, block in enumerate(blocks or [])
             if _is_short_single_line(block)}
    if len(short) < TABLE_MIN_ROWS:
        return None
    if not has_grid_lines(page):
        return None
    return {"rows": len(short), "cols": 0, "cells": short, "fill": 1.0,
            "irregular": False, "source": "lines"}


# —— 页面几何（§1） -----------------------------------------------------------

def page_geometry_degraded(page):
    """/Rotate 非零或 CropBox ≠ MediaBox 时返回 True（回填需坐标归一化）。

    `page.rotation` 需在临时归零旋转**之前**读取；`cropbox` / `mediabox` 不受
    旋转影响，随时可读。
    """
    rotation = int(getattr(page, "rotation", 0) or 0) % 360
    if rotation != 0:
        return True
    crop = page.cropbox
    media = page.mediabox
    return any(abs(left - right) > GEOMETRY_EPS
               for left, right in ((crop.x0, media.x0), (crop.y0, media.y0),
                                   (crop.x1, media.x1), (crop.y1, media.y1)))


__all__ = [
    "RTL_RATIO_THRESHOLD", "VERTICAL_DIR_RATIO", "MATH_RATIO_THRESHOLD",
    "MATH_MIN_COUNT", "TABLE_MIN_ROWS", "TABLE_MIN_COLS", "TABLE_MIN_CELLS",
    "TABLE_MAX_CELL_CHARS", "TABLE_MIN_FILL", "GEOMETRY_EPS",
    "rtl_ratio", "is_rtl_text", "is_vertical_lines", "classify_unsupported",
    "is_math_char", "math_ratio", "is_math_dense",
    "table_grid", "table_structure", "has_grid_lines", "page_geometry_degraded",
]
