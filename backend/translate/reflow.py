# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""回填排版引擎（翻译引擎 T8）。

把 T5 产出的译文按《翻译-回填排版规格.md》写回原页面：先**真实移除**原文本
（PyMuPDF redaction，保留图像，不以黑框充当删除，§6.2），再按 §4 的强制顺序
S0→S3 排入译文：

- S0 原块框、原字号（scale 1.00）可容纳 → 直接排入；
- S1 字号自适应缩小 1.00→0.70（步进 0.05），行高在紧张时可由 1.0 压到 0.92；
- S2 溢出到页边留白（先下后右，遇到相邻块/图像即停）；
- S3 裁切（写入能容纳的部分，其余丢弃）。

字号候选遵循 §5：优先可再分发的内置字体（拉丁 base14 / CJK `china-s` 等，
均带 ToUnicode，可搜索），本机系统字体仅在内置字体缺字形时作为回退，且必须
通过 `OS/2 fsType` 检查（Restricted License 位禁止嵌入）。缺字形字符替换为可见
占位符并记 `glyph_missing`。译文通过 `fitz.TextWriter`（叠加在图像之上的 z-order）
写入，保证 `get_text()` 可提取。

坐标：本引擎在调用期间临时把页面旋转归零（等价于规格 §1.2 的规范化空间），
直接用 `RectNorm` 的未旋转、相对 CropBox 坐标绘制，结束后恢复 /Rotate，因此
抽取与回填共用同一坐标系。

降级报告与原因枚举复用 `verify.py`（翻译引擎 T7）的**唯一枚举**
`DEGRADATION_REASONS` 与 `DegradationReport`，不在此另立一套，避免魔法字符串漂移。
"""
from __future__ import annotations

import os
import re
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

import pymupdf as fitz

try:  # 作为包导入（tests 走 backend 顶层 → translate.reflow）
    from . import geometry
    from .verify import (
        CLIP, DEGRADATION_REASONS, FONT_FALLBACK, FONT_SHRINK, GLYPH_MISSING,
        OVERFLOW_MARGIN, RASTER_OVERLAY, SEARCH_VERIFY_FAIL, UNSEARCHABLE_REASONS,
        DegradationReport,
    )
except ImportError:  # 直接以 backend 为 sys.path 顶层加载时
    import geometry
    from verify import (
        CLIP, DEGRADATION_REASONS, FONT_FALLBACK, FONT_SHRINK, GLYPH_MISSING,
        OVERFLOW_MARGIN, RASTER_OVERLAY, SEARCH_VERIFY_FAIL, UNSEARCHABLE_REASONS,
        DegradationReport,
    )


# —— 规格 §4.2 / §4.3 溢出参数 -------------------------------------------------
SHRINK_MIN = 0.70        # 字号缩放下限
SHRINK_STEP = 0.05       # 字号缩放步进
LINEHEIGHT_MIN = 0.92    # 行高压缩下限（× 字号）
MARGIN = 24.0            # 页边留白边界（pt）
BLOCK_GAP = 4.0          # 下扩时与下一块保留的间距（pt）
DEFAULT_SIZE = 11.0      # 块缺少字号时（如 OCR）的兜底字号
_EPS = 0.5

# —— 规格 §5 字体参数 ----------------------------------------------------------
_HELV = "helv"
_CJK_BUILTINS = ("china-s", "china-t", "japan", "korea")
# OS/2 fsType 的 Restricted License 位（规格 §5.3：置位禁止嵌入）。
_FSTYPE_RESTRICTED = 0x0002
_SYSTEM_SCAN_LIMIT = 240
_PREFERRED_FONT_HINTS = (
    "noto", "sourcehan", "source han", "dejavu", "liberation", "opensans",
    "roboto", "wqy", "droid", "fandol",
)
# 缺字形占位符优先顺序，实际取第一个被当前字体覆盖的字符（规格 §5.2）。
_PLACEHOLDER_PREFERRED = ("▯", "□", "?")

_CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]")
_KANA_RE = re.compile(r"[\u3040-\u30ff]")
_HANGUL_RE = re.compile(r"[\uac00-\ud7af]")

# 源文档字体名 → 内置 base14 别名（规格 §5.1 第 2 条：可复用源字体时复用）。
_SOURCE_FONT_ALIASES = (
    ("helvetica", "helv"), ("arial", "helv"), ("times", "tiro"),
    ("courier", "cour"),
)


@dataclass(frozen=True)
class FontChoice:
    """一次回填选定的字体及其降级信息。

    `fallback=True` 表示没有使用首选（拉丁 base14 / 目标脚本内置字体），需记
    `font_fallback`；`missing` 为字体未覆盖的字符元组，非空需记 `glyph_missing`。
    """

    font: "fitz.Font"
    name: str
    fontfile: str = ""
    fallback: bool = False
    missing: tuple = ()


@dataclass
class _Placement:
    """成功写入的一段译文；`clipped=True` 表示只写入了能容纳的部分。"""

    text: str
    clipped: bool = False


# —— 规格 §5：字体候选、fsType 与缺字形 ---------------------------------------

def font_fs_type(data):
    """从 TrueType/OpenType 二进制解析 OS/2 表的 fsType；无法解析返回 None。

    只做表目录定位，不支持 TrueType Collection（`ttcf`）——系统扫描会跳过 .ttc。
    """
    if not data or len(data) < 12 or data[:4] == b"ttcf":
        return None
    try:
        num_tables = struct.unpack(">H", data[4:6])[0]
    except struct.error:
        return None
    if num_tables <= 0 or 12 + num_tables * 16 > len(data):
        return None
    for index in range(num_tables):
        offset = 12 + index * 16
        if data[offset:offset + 4] != b"OS/2":
            continue
        try:
            table = struct.unpack(">I", data[offset + 8:offset + 12])[0]
            return struct.unpack(">H", data[table + 8:table + 10])[0]
        except struct.error:
            return None
    return None


def is_embeddable(fs_type):
    """fsType 是否允许嵌入（规格 §5.3）。None（未有 OS/2）按无限制处理。"""
    return fs_type is None or not (fs_type & _FSTYPE_RESTRICTED)


def _system_font_dirs():
    if sys.platform == "win32":
        windir = os.environ.get("WINDIR") or r"C:\Windows"
        local = os.environ.get("LOCALAPPDATA") or ""
        dirs = [Path(windir) / "Fonts"]
        if local:
            dirs.append(Path(local) / "Microsoft" / "Windows" / "Fonts")
        return dirs
    if sys.platform == "darwin":
        return [Path("/System/Library/Fonts"), Path("/Library/Fonts"),
                Path.home() / "Library" / "Fonts"]
    return [Path("/usr/share/fonts"), Path("/usr/local/share/fonts"),
            Path.home() / ".fonts"]


def _system_font_files(limit=_SYSTEM_SCAN_LIMIT):
    """扫描本机可嵌入字体文件；可再分发字体名优先（规格 §5.1）。"""
    found = []
    for folder in _system_font_dirs():
        if not folder.is_dir():
            continue
        for path in folder.rglob("*"):
            if path.suffix.lower() in (".ttf", ".otf"):
                found.append(path)
    found.sort(key=lambda path: (
        0 if any(hint in path.name.lower() for hint in _PREFERRED_FONT_HINTS) else 1,
        path.name.lower()))
    return found[:limit]


def _candidate_names(text):
    """按目标脚本给出内置字体候选顺序（去重保序）。"""
    names = []
    has_cjk = bool(_CJK_RE.search(text))
    has_kana = bool(_KANA_RE.search(text))
    has_hangul = bool(_HANGUL_RE.search(text))
    if has_hangul:
        names.append("korea")
    if has_kana:
        names.append("japan")
    if has_cjk:
        names += ["china-s", "china-t"]
    if has_cjk or has_kana or has_hangul:
        names += list(_CJK_BUILTINS)
    names.append(_HELV)
    seen = set()
    ordered = []
    for name in names:
        if name not in seen:
            seen.add(name)
            ordered.append(name)
    return ordered


def _source_font_alias(font_name):
    name = (font_name or "").strip().lower()
    for prefix, alias in _SOURCE_FONT_ALIASES:
        if name.startswith(prefix):
            return alias
    return ""


def _load_builtin(name):
    try:
        return FontChoice(font=fitz.Font(name), name=name)
    except Exception:
        return None


def _missing_glyphs(font, needed):
    return {char for char in needed if not font.has_glyph(ord(char))}


def _scan_system_font(needed, system_files=None):
    """在内置字体缺字形时扫描本机字体；只采用可解析且非受限许可的字体。"""
    paths = list(system_files) if system_files is not None else _system_font_files()
    best = None
    best_missing = None
    for path in paths:
        try:
            data = Path(path).read_bytes()
        except OSError:
            continue
        fs_type = font_fs_type(data)
        if fs_type is None or not is_embeddable(fs_type):
            continue  # 未知许可或受限许可：不嵌入（规格 §5.3）
        try:
            font = fitz.Font(fontbuffer=data)
        except Exception:
            continue
        missing = _missing_glyphs(font, needed)
        if best_missing is None or len(missing) < len(best_missing):
            best_missing = missing
            best = FontChoice(font=font, name=Path(path).stem, fontfile=str(path),
                              fallback=True, missing=tuple(sorted(missing)))
            if not missing:
                break
    return best


def resolve_font(text, base_font="", *, provider=None, system_files=None):
    """为一段译文选择回填字体（规格 §5）。

    顺序：① 源字体别名（base14）覆盖完整则复用，不算降级；② 目标脚本内置字体
    （首个为自然选择，其后为降级）；③ 内置都缺字形时回退扫描本机可嵌入字体；
    ④ 仍缺字形则取缺得最少的内置字体并标 `missing`。不下载任何字体。
    """
    if provider is not None:
        return provider(text, base_font)
    needed = tuple(sorted({char for char in (text or "") if not char.isspace()}))
    natural_names = _candidate_names(text)
    if not natural_names:
        natural_names = [_HELV]

    # ① 源字体可复用：覆盖完整才算复用，不记 font_fallback。
    alias = _source_font_alias(base_font)
    if alias:
        builtin = _load_builtin(alias)
        if builtin is not None and not _missing_glyphs(builtin.font, needed):
            return builtin

    # ② 目标脚本内置字体；首个覆盖完整者为自然选择，后续为降级。
    fallbacks = []
    for index, name in enumerate(natural_names):
        builtin = _load_builtin(name)
        if builtin is None:
            continue
        missing = _missing_glyphs(builtin.font, needed)
        if not missing:
            return FontChoice(font=builtin.font, name=name, fallback=index > 0)
        fallbacks.append((len(missing), index, builtin, missing))
    if not fallbacks:
        builtin = _load_builtin(_HELV)
        fallbacks = [(len(needed), 0, builtin, set(needed))]

    best_missing_count, _best_index, best, best_missing = min(
        fallbacks, key=lambda item: (item[0], item[1]))

    # ③ 本机系统字体（须非受限许可）；只在缺字形更少时采用。
    system_choice = _scan_system_font(needed, system_files)
    if system_choice is not None and len(system_choice.missing) < best_missing_count:
        return system_choice

    # ④ 都缺字形：返回缺得最少的内置字体，交由调用方标 glyph_missing。
    return FontChoice(font=best.font, name=best.name, fallback=True,
                      missing=tuple(sorted(best_missing)))


def _placeholder(font):
    for char in _PLACEHOLDER_PREFERRED:
        if font.has_glyph(ord(char)):
            return char
    return _PLACEHOLDER_PREFERRED[-1]


# —— 排版原语：测量即落笔，放不下则整段丢弃 ------------------------------------

def _attempt(page, rect, text, font, size, lineheight=None):
    """尝试把 `text` 排入 `rect`；返回 (是否完全容纳, 已填充的 TextWriter)。

    用 `TextWriter.fill_textbox` 测量：返回空（或无内容）剩余即视为完全容纳；
    放不下时 writer 已包含能容纳的部分行，交由 S3 写出来实现裁切。
    """
    writer = fitz.TextWriter(page.rect)
    try:
        leftover = writer.fill_textbox(rect, text, font=font, fontsize=size,
                                       lineheight=lineheight)
    except Exception:
        return False, writer
    fits = not leftover or all(not (item[0] or "").strip() for item in leftover)
    return fits, writer


def _write(writer, page):
    if getattr(writer, "text_rect", None):
        writer.write_text(page)


def _try_write(page, rect, text, font, size, lineheight=None):
    fits, writer = _attempt(page, rect, text, font, size, lineheight)
    if not fits:
        return None
    _write(writer, page)
    return _Placement(text=text, clipped=False)


def _shrink_scales():
    steps = int(round((1.0 - SHRINK_MIN) / SHRINK_STEP))
    return [round(1.0 - index * SHRINK_STEP, 2) for index in range(1, steps + 1)]


def _base_size(block):
    size = float(getattr(block, "size", 0.0) or 0.0)
    if size > 0:
        return size
    height = float(getattr(block.rect, "height", 0.0) or 0.0)
    return max(height * 0.8, DEFAULT_SIZE)


def _down_limit(original, occupied, crop):
    """下扩边界：页底留白与相邻块上缘 − 4pt 取小，且不小于原块下缘（§4.3）。"""
    limit = crop.y1 - MARGIN
    for rect in occupied:
        if (rect.x0 < original.x1 - _EPS and rect.x1 > original.x0 + _EPS
                and rect.y0 >= original.y0 - _EPS):
            limit = min(limit, rect.y0 - BLOCK_GAP)
    return max(limit, original.y1)


def _right_limit(original, occupied, crop):
    """右扩边界：页右留白与相邻块左缘 − 4pt 取小，且不小于原块右缘（§4.3）。"""
    limit = crop.x1 - MARGIN
    for rect in occupied:
        if (rect.y0 < original.y1 - _EPS and rect.y1 > original.y0 + _EPS
                and rect.x0 >= original.x0 - _EPS):
            limit = min(limit, rect.x0 - BLOCK_GAP)
    return max(limit, original.x1)


def _place_block(page, block, text, occupied, report, page_no, *,
                 font_provider=None, system_files=None):
    """对单个块执行 S0→S3 溢出决策；命中即停，并记录退化原因。"""
    base_size = _base_size(block)
    choice = resolve_font(text, base_font=getattr(block, "font", ""),
                          provider=font_provider, system_files=system_files)
    if choice.fallback:
        report.add(page_no, FONT_FALLBACK)
    # 字形覆盖检查由引擎自身完成（规格 §5.2），不依赖 provider 是否申报。
    needed = {char for char in text if not char.isspace()}
    missing = set(choice.missing) | _missing_glyphs(choice.font, needed)
    if missing:
        report.add(page_no, GLYPH_MISSING)
        placeholder = _placeholder(choice.font)
        text = "".join(placeholder if char in missing else char for char in text)

    original = block.rect.rect
    # S0：原块框 + 原字号。
    placement = _try_write(page, original, text, choice.font, base_size)
    if placement is not None:
        return placement

    # S1：字号自适应缩小（必要时压行高）。
    for scale in _shrink_scales():
        size = base_size * scale
        for lineheight in (None, LINEHEIGHT_MIN):
            placement = _try_write(page, original, text, choice.font, size,
                                   lineheight=lineheight)
            if placement is not None:
                report.add(page_no, FONT_SHRINK)
                return placement

    # S2：先下后右溢出到页边留白；与相邻块/图像相交则在该边界停住。
    min_size = base_size * SHRINK_MIN
    crop = page.rect
    down = _down_limit(original, occupied, crop)
    right = _right_limit(original, occupied, crop)
    expanded_down = fitz.Rect(original.x0, original.y0, original.x1,
                              max(original.y1, down))
    expanded = fitz.Rect(original.x0, original.y0, max(original.x1, right),
                         max(original.y1, down))
    for rect in (expanded_down, expanded):
        if rect.height <= original.height + _EPS and rect.width <= original.width + _EPS:
            continue
        for lineheight in (None, LINEHEIGHT_MIN):
            placement = _try_write(page, rect, text, choice.font, min_size,
                                   lineheight=lineheight)
            if placement is not None:
                report.add(page_no, OVERFLOW_MARGIN)
                return placement

    # S3：裁切——写入能容纳的部分，超出丢弃。
    _fits, writer = _attempt(page, expanded, text, choice.font, min_size)
    _write(writer, page)
    report.add(page_no, CLIP)
    return _Placement(text=text, clipped=True)


# —— 页面处理 -----------------------------------------------------------------

def _translation_map(blocks, translations):
    """把多种形态的译文输入归一为 {块下标: 译文}。

    支持：`{下标: 文本}`；`BlockTranslation` 列表（用 `.index` 定位，degraded 跳过）；
    与 `blocks` 等长的平行列表（单元可为 str / None / (文本, …) / BlockTranslation）。
    缺失或空的译文不进入映射，调用方据此保留原文。
    """
    result = {}
    count = len(blocks)
    if translations is None:
        return result
    if isinstance(translations, dict):
        for key, value in translations.items():
            if isinstance(key, int) and 0 <= key < count:
                _store_translation(result, key, value)
        return result
    items = list(translations)
    if items and all(hasattr(item, "index") and hasattr(item, "text") for item in items):
        for item in items:
            if 0 <= item.index < count and not getattr(item, "degraded", False):
                _store_translation(result, item.index, item.text)
        return result
    for index, value in enumerate(items[:count]):
        _store_translation(result, index, value)
    return result


def _store_translation(result, index, value):
    if value is None:
        return
    if hasattr(value, "text"):
        if getattr(value, "degraded", False):
            return
        text = value.text
    elif isinstance(value, (list, tuple)):
        text = value[0] if value else ""
    else:
        text = value
    text = "" if text is None else str(text)
    if text.strip():
        result[index] = text


def _is_raster(block):
    """扫描件块：无文本层、原文为光栅图，redaction 无法移除（规格 §6.3）。"""
    font = (getattr(block, "font", "") or "").strip().lower()
    return font in ("ocr", "raster", "scan")


def _occupied_rects(page, blocks):
    """块框（前 len(blocks) 项与 blocks 对齐）+ 图像块框，作为溢出相交判定区。"""
    rects = [block.rect.rect for block in blocks]
    for raw in page.get_text("dict").get("blocks", []) or []:
        if raw.get("type", 0) == 1:
            rects.append(fitz.Rect(raw["bbox"]))
    return rects


def _normalize(text):
    return re.sub(r"\s+", "", text or "").lower()


def _verify_searchable(page, placements, report, page_no):
    """ToUnicode 抽检：写入的译文应能被 get_text() 提取（规格 §8）。"""
    if not placements:
        return
    extracted = _normalize(page.get_text())
    if not extracted:
        report.add(page_no, SEARCH_VERIFY_FAIL)
        return
    for placement in placements:
        probe = _normalize(placement.text)
        if not probe:
            continue
        if placement.clipped:
            probe = probe[:8]  # 裁切后只写入前缀，抽检开头字符即可确认 ToUnicode
        if probe and probe not in extracted:
            report.add(page_no, SEARCH_VERIFY_FAIL)
            return


def backfill_page(page, blocks, translations, report, *, font_provider=None,
                  system_files=None):
    """把一页的译文回填进 `page`（原地修改），并写入 `report`。

    - `blocks`：该页阅读序 `TextBlock`（含 `RectNorm`、字号、字体名）。
    - `translations`：译文，见 `_translation_map` 的三形态。
    - `report`：`DegradationReport`；每个未达成效果都留痕。
    - `font_provider` / `system_files`：注入字体选择或系统字体清单（测试用）。

    先在同一个处理批次内对所有可编辑块做真实移除（redaction，保留图像），再依次
    S0→S3 写入译文，避免出现「原文已删、译文未写」的中间态。扫描块不做移除，
    仅叠加译文并记 `raster_overlay`。
    """
    page_no = page.number + 1
    blocks = list(blocks or [])
    mapping = _translation_map(blocks, translations)
    saved_rotation = page.rotation
    page.set_rotation(0)  # 规格 §1.2：在未旋转 + CropBox 相对坐标系内回填
    try:
        editable = []
        raster = []
        for index, block in enumerate(blocks):
            text = mapping.get(index)
            if not text or not str(text).strip():
                continue
            if _is_raster(block):
                raster.append((index, block, str(text)))
            else:
                editable.append((index, block, str(text)))
        for _index, _block, _text in raster:
            report.add(page_no, RASTER_OVERLAY)

        # §6.2 真实移除：集中 redaction 后一次性 apply，保留图像/矢量图。
        if editable:
            for _index, block, _text in editable:
                page.add_redact_annot(block.rect.rect)  # fill=None：不铺黑框
            page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_NONE,
                                  graphics=fitz.PDF_REDACT_LINE_ART_NONE,
                                  text=fitz.PDF_REDACT_TEXT_REMOVE)

        occupied = _occupied_rects(page, blocks)
        placements = []
        for index, block, text in raster + editable:
            others = [rect for other, rect in enumerate(occupied) if other != index]
            placement = _place_block(page, block, text, others, report, page_no,
                                     font_provider=font_provider,
                                     system_files=system_files)
            if placement is not None:
                placements.append(placement)
        _verify_searchable(page, placements, report, page_no)
    finally:
        page.set_rotation(saved_rotation)


__all__ = [
    "DEGRADATION_REASONS", "UNSEARCHABLE_REASONS", "SHRINK_MIN", "SHRINK_STEP",
    "DEFAULT_SIZE", "FontChoice", "DegradationReport", "font_fs_type",
    "is_embeddable", "resolve_font", "backfill_page",
]
