# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""译文 PDF 输出：mono 纯译文 / dual 原文对照（翻译引擎 T9）。

把 T8 回填后的页面组装成两份产物，并落实《翻译-回填排版规格.md》：

- **mono（纯译文）**：逐页复制原文页 → 原地回填译文（原文被真实移除），页数恒等于
  原文页数；某页没有译文时保留原文，绝不扩页、不重排分页。
- **dual（对照）**：按「原页 N → 译页 N」交错排布（原 1, 译 1, 原 2, 译 2, …），
  默认**不自动左右并列**。被跳过 / 失败 / 无文本块的页，在译文序列插入**等量空白页**
  以维持 `原 N ↔ 译 N`（§7），并已在报告中标记原因。

文件名与落盘
------------
- `{basename}-纯译文.pdf` / `{basename}-对照.pdf`；目标已存在时在词干后加序号
  （`报告 (2)-纯译文.pdf`），不静默覆盖。
- 产物一律经同目录临时文件 + `os.replace` 原子落盘（复用 `bridge.atomic_replace`），
  崩溃在替换前不会留下半成品；输出显式 `PDF_ENCRYPT_NONE`，默认**不加密**。
- 输出进入任务目录 `AIPDF-<YYYYmmdd-HHMMSS>-<hex6>/`（与 `engine.dispatch` 命名一致）。

诚实标注（§9.3）
----------------
- 每页页脚加极小灰色（6pt）标记 `机器翻译 · 请核对`，默认开启、不遮挡正文；一般文档
  可关闭，**法律 / 医学高风险模板强制显示、不可关闭**（`marker=False, high_risk=True`
  仍会写出标记）。
- 受限文档（空用户密码 + owner 权限、`/P` 禁止抽取）不被静默处理：输出不继承原限制，
  且把披露信息写入 `report.source_protection`（`describe_source_protection`）。

坐标：页脚标记使用 PyMuPDF 的「未旋转 + CropBox 相对」坐标（与抽取/回填同一空间），
因此旋转页也能落在正确页脚，不依赖可见页 `page.rect`。
"""
from __future__ import annotations

from collections.abc import Mapping
from pathlib import Path
import time
import uuid

import pymupdf as fitz

try:  # 作为包导入（tests 走 backend 顶层 → translate.output）
    from . import bridge, extract, reflow
    from .verify import EMPTY_PAGE, NO_TEXT_BLOCK, PAGE_FAILED, SKIP_PAGE
except ImportError:  # 直接以 backend 为 sys.path 顶层加载时
    import bridge
    import extract
    import reflow
    from verify import EMPTY_PAGE, NO_TEXT_BLOCK, PAGE_FAILED, SKIP_PAGE


# —— 规格 §9.3 页脚标记 ------------------------------------------------------
MARKER_TEXT = "机器翻译 · 请核对"
MARKER_SIZE = 6.0                         # 极小字号（pt）
MARKER_COLOR = (0.5, 0.5, 0.5)            # 浅灰，避免干扰阅读
MARKER_FONT = "china-s"                   # 内置 CJK 字体，带 ToUnicode
MARKER_MARGIN = 6.0                       # 页脚左右留白（pt）
MARKER_BAND = 12.0                        # 页脚标记带高度（pt）

# —— 产物命名 ----------------------------------------------------------------
TASK_PREFIX = "AIPDF-"
MONO_SUFFIX = "-纯译文"
DUAL_SUFFIX = "-对照"
_SUFFIXES = {"mono": MONO_SUFFIX, "dual": DUAL_SUFFIX}

# —— 受限文档披露（《需求总结.md》§受限文档策略） ---------------------------
_RESTRICTED_DISCLOSURE = "已绕过源文档的抽取限制（经用户确认）"
# PyMuPDF 的标准权限位；缺任意一位即视为受限（权限加密）。
_PERMISSION_BITS = (fitz.PDF_PERM_PRINT | fitz.PDF_PERM_MODIFY | fitz.PDF_PERM_COPY
                    | fitz.PDF_PERM_ANNOTATE | fitz.PDF_PERM_FORM
                    | fitz.PDF_PERM_ACCESSIBILITY | fitz.PDF_PERM_ASSEMBLE
                    | fitz.PDF_PERM_PRINT_HQ)

# 译文序列需要补空白页的降级原因（§2.4 / §7.2 / §7.3）。
_BLANK_REASONS = (SKIP_PAGE, PAGE_FAILED, NO_TEXT_BLOCK, EMPTY_PAGE)


def _user_error(message):
    """构造 engine.UserError（延迟导入避免 engine ↔ translate 循环依赖）。"""
    try:
        from engine import UserError
    except ImportError:
        return RuntimeError(message)
    return UserError(message)


# —— 页脚标记 ----------------------------------------------------------------

def add_marker(page, *, text=MARKER_TEXT, size=MARKER_SIZE, color=MARKER_COLOR,
               font=MARKER_FONT):
    """在 `page` 页脚写入极小灰色「机器翻译 · 请核对」标记，返回是否写入成功。

    使用未旋转 + CropBox 相对坐标定位页脚，旋转页也能落在正确位置；不写入正文区、
    不改动正文内容。CJK 字体不可用时退回内置拉丁字体（宁缺清晰，不抛异常）。
    """
    crop = page.cropbox
    rect = fitz.Rect(MARKER_MARGIN, crop.height - MARKER_BAND,
                     crop.width - MARKER_MARGIN, crop.height - 2.0)
    if rect.width <= 0 or rect.height <= 0:
        return False
    for name in (font, "helv"):
        try:
            page.insert_textbox(rect, text, fontsize=size, fontname=name,
                                color=color, align=fitz.TEXT_ALIGN_CENTER)
            return True
        except Exception:
            continue
    return False


def _marker_enabled(marker, high_risk):
    """高风险（法律/医学）模板强制开启页脚标记，设置无法关闭（§9.3）。"""
    return bool(marker) or bool(high_risk)


# —— 受限文档披露 ------------------------------------------------------------

def describe_source_protection(source_doc):
    """返回源文档的权限披露信息（不读取正文、不含密码）。

    `restricted=True` 表示「权限加密」：可打开但 `/P` 限制了抽取/复制等权限位。
    输出为新文档、不继承该限制，调用方应据此向用户明示（《需求总结.md》§受限文档策略）。
    """
    permissions = int(getattr(source_doc, "permissions", _PERMISSION_BITS))
    restricted = (permissions & _PERMISSION_BITS) != _PERMISSION_BITS
    return {
        "encrypted": bool(getattr(source_doc, "is_encrypted", False)),
        "restricted": restricted,
        "permissions": permissions,
        "disclosure": _RESTRICTED_DISCLOSURE if restricted else "",
    }


def _disclose_restrictions(source_doc, report):
    """把源文档权限披露挂到报告对象（`report.source_protection`）。

    `DegradationReport` 的 `to_dict()` 是固定 schema（页码 + 原因），不含策略类字段，
    因此这里以属性形式附带；报告 sidecar 写出时由调用方合并该属性。
    """
    info = describe_source_protection(source_doc)
    try:
        report.source_protection = info
    except Exception:
        pass
    return info


# —— 译文归一：把多种输入形态整理成 {页号: {块下标: 译文}} --------------------

def _extract_blocks(source_doc):
    """逐页抽取阅读序块（与 T3 抽取口径一致），供回填复用。"""
    return [extract.extract_page(source_doc[index], index + 1)
            for index in range(len(source_doc))]


def _block_offsets(pages):
    """每页首块的文档级全局下标（用于把扁平译文按页定位）。"""
    offsets = []
    cursor = 0
    for blocks in pages:
        offsets.append(cursor)
        cursor += len(blocks)
    return offsets


def _is_translation_item(item):
    """是否为 `BlockTranslation` 形态（同时带 `index` 与 `text`）。

    必须同时判两个属性：str/bytes/list/tuple 也带 `.index` 方法，只判 `index` 会把
    平行列表误当逐段译文对象。
    """
    return hasattr(item, "index") and hasattr(item, "text")


def _text_of(value):
    """从 str / None / (文本, …) / BlockTranslation 提取译文文本。"""
    if value is None:
        return ""
    if hasattr(value, "text"):
        return str(value.text or "")
    if isinstance(value, (list, tuple)):
        return "" if not value or value[0] is None else str(value[0])
    return str(value)


def _localize(value, blocks, offset):
    """把一页的译文输入归一为 `{局部块下标: 译文}`。

    支持：`{下标: 文本}`；`BlockTranslation` 列表（下标按页内局部或全局自动识别）；
    与块等长的平行列表（str / None / (文本, …)）。空译文与 degraded 段不进入映射，
    调用方据此保留原文。超过 1 个页内块但下标明显是全局的，按 `offset` 回退到局部。
    """
    if value is None:
        return {}
    if isinstance(value, Mapping):
        keys = [int(key) for key in value]
        global_form = bool(keys) and any(key >= len(blocks) for key in keys)
        result = {}
        for key, item in value.items():
            index = int(key) - (offset if global_form else 0)
            text = _text_of(item)
            if 0 <= index < len(blocks) and text.strip():
                result[index] = text
        return result
    items = list(value)
    if items and all(_is_translation_item(item) for item in items):
        global_form = any(int(item.index) >= len(blocks) for item in items)
        result = {}
        for item in items:
            if getattr(item, "degraded", False):
                continue
            index = int(item.index) - (offset if global_form else 0)
            text = _text_of(item)
            if 0 <= index < len(blocks) and text.strip():
                result[index] = text
        return result
    result = {}
    for index, item in enumerate(items):
        if index >= len(blocks):
            break
        text = _text_of(item)
        if text.strip():
            result[index] = text
    return result


def _locate(offsets, pages, global_index):
    """全局块下标 → (页号, 页内下标)；越界返回 None。"""
    if global_index < 0:
        return None
    for page_index in range(len(pages) - 1, -1, -1):
        if global_index >= offsets[page_index]:
            return page_index + 1, global_index - offsets[page_index]
    return None


def page_translations(source_doc, translations, *, blocks_by_page=None):
    """把翻译输入归一为 `{页号(1 基): {页内块下标: 译文}}`。

    接受两种形态：
    1. **映射**：`{页号: 该页译文}`，值为 `backfill_page` 支持的任一形态；
    2. **扁平序列**：整篇 `BlockTranslation`（`.index` 为文档级全局下标，直接来自
       `chunk.reassemble_chunks`），或与文档级块序列等长的平行列表（str / None）。
    """
    pages = blocks_by_page if blocks_by_page is not None else _extract_blocks(source_doc)
    if translations is None:
        return {}
    if isinstance(translations, Mapping):
        offsets = _block_offsets(pages)
        result = {}
        for key, value in translations.items():
            page_index = int(key) - 1
            if not 0 <= page_index < len(pages):
                continue
            entry = _localize(value, pages[page_index], offsets[page_index])
            if entry:
                result[page_index + 1] = entry
        return result
    return _flatten(pages, list(translations))


def _flatten(pages, items):
    """扁平序列形态：BlockTranslation 按全局下标归页，平行列表按顺序归页。"""
    offsets = _block_offsets(pages)
    result = {}
    if items and all(_is_translation_item(item) for item in items):
        for item in items:
            if getattr(item, "degraded", False):
                continue
            located = _locate(offsets, pages, int(item.index))
            if located is None:
                continue
            page_no, index = located
            text = _text_of(item)
            if text.strip():
                result.setdefault(page_no, {})[index] = text
        return result
    cursor = 0
    for page_index, blocks in enumerate(pages):
        for index in range(len(blocks)):
            if cursor >= len(items):
                return result
            text = _text_of(items[cursor])
            cursor += 1
            if text.strip():
                result.setdefault(page_index + 1, {})[index] = text
    return result


# —— mono / dual 组装 --------------------------------------------------------

def _blank_pages(report, total_pages, explicit):
    """需要补空白页的页号集合：显式指定 + 报告中跳过/失败/无文本块的页（§7）。"""
    blanks = set()
    for page_no in explicit or ():
        page_no = int(page_no)
        if 1 <= page_no <= total_pages:
            blanks.add(page_no)
    if report is not None:
        for item in report.to_dict().get("pages", []):
            if set(item.get("reasons", [])) & set(_BLANK_REASONS):
                blanks.add(int(item["page"]))
    return blanks


def _build_translated(source_doc, pages, page_map, report, *, marker, high_risk,
                      blank_pages):
    """构建「译文序列」：逐页复制并回填；`blank_pages` 内的页写为等量空白页。

    页序与原文严格一一对应（页 N ↔ 页 N）；在此文档内 `page.number + 1` 即原文页号，
    因此 `backfill_page` 写入报告的页码是正确的（dual 交错后不会翻倍）。
    """
    output = fitz.open()
    marker_on = _marker_enabled(marker, high_risk)
    try:
        for index in range(len(source_doc)):
            page_no = index + 1
            source = source_doc[index]
            if page_no in blank_pages:
                blank = output.new_page(width=source.cropbox.width,
                                        height=source.cropbox.height)
                blank.set_rotation(source.rotation)
                if marker_on:
                    add_marker(blank)
                continue
            output.insert_pdf(source_doc, from_page=index, to_page=index)
            target = output[-1]
            reflow.backfill_page(target, pages[index], page_map.get(page_no, {}), report)
            if marker_on:
                add_marker(target)
        return output
    except Exception:
        output.close()
        raise


def build_mono(source_doc, translations, report, *, marker=True, high_risk=False,
               blocks_by_page=None):
    """构建 mono（纯译文）文档：页数 = 原文页数，译文原地替换原文。

    - 没有译文的页保留原文（不补空白、不扩页）；
    - `marker=False` 时不写页脚标记，但 `high_risk=True` 强制写出（§9.3）；
    - `blocks_by_page` 可注入 T3 抽取结果，避免重复抽取（默认内部抽取）。
    """
    pages = blocks_by_page if blocks_by_page is not None else _extract_blocks(source_doc)
    page_map = page_translations(source_doc, translations, blocks_by_page=pages)
    _disclose_restrictions(source_doc, report)
    return _build_translated(source_doc, pages, page_map, report, marker=marker,
                             high_risk=high_risk, blank_pages=frozenset())


def build_dual(source_doc, translations, report, *, marker=True, high_risk=False,
               blank_pages=None, blocks_by_page=None):
    """构建 dual（对照）文档：原页 N、译页 N 交错，默认不左右并列。

    被跳过 / 失败 / 无文本块的页（由 `report` 或 `blank_pages` 指定）在译文序列插入
    等量空白页，保持 `原 N ↔ 译 N`（§7）；不重排、不扩页、不自动切横向。
    """
    pages = blocks_by_page if blocks_by_page is not None else _extract_blocks(source_doc)
    page_map = page_translations(source_doc, translations, blocks_by_page=pages)
    _disclose_restrictions(source_doc, report)
    blanks = _blank_pages(report, len(source_doc), blank_pages)
    translated = _build_translated(source_doc, pages, page_map, report, marker=marker,
                                   high_risk=high_risk, blank_pages=blanks)
    marker_on = _marker_enabled(marker, high_risk)
    output = fitz.open()
    try:
        for index in range(len(source_doc)):
            output.insert_pdf(source_doc, from_page=index, to_page=index)
            if marker_on:
                add_marker(output[-1])
            output.insert_pdf(translated, from_page=index, to_page=index)
        return output
    except Exception:
        output.close()
        raise
    finally:
        translated.close()


# —— 落盘：文件名、原子保存、任务目录 -----------------------------------------

def output_filename(source_path, kind, directory=None):
    """产物文件名：`{basename}{后缀}.pdf`；已存在时加序号 `{basename} (2){后缀}.pdf`。

    `directory` 为 None 时只返回首选名（不检查冲突）；给出目录时自动去重，不覆盖。
    """
    if kind not in _SUFFIXES:
        raise ValueError(f"未知的输出类型：{kind!r}。")
    suffix = _SUFFIXES[kind]
    stem = Path(source_path).stem or "document"
    name = f"{stem}{suffix}.pdf"
    if directory is None:
        return name
    folder = Path(directory)
    if not (folder / name).exists():
        return name
    sequence = 2
    while True:
        name = f"{stem} ({sequence}){suffix}.pdf"
        if not (folder / name).exists():
            return name
        sequence += 1


def save_document(doc, path):
    """把 `doc` 原子保存到 `path`（同目录 temp → 校验 → `os.replace`）。

    显式 `PDF_ENCRYPT_NONE`：不继承源文档限制、默认不加密；保存后重开校验页数，
    失败则删除临时文件并保留原目标。返回最终路径。
    """
    if not len(doc):
        raise _user_error("操作后没有剩余页面。")
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.parent / (bridge.TEMP_PREFIX + uuid.uuid4().hex)
    try:
        doc.save(str(temporary), garbage=4, deflate=True,
                 encryption=fitz.PDF_ENCRYPT_NONE)
        with fitz.open(str(temporary)) as check:
            if check.page_count != len(doc):
                raise _user_error("输出文件验证失败。")
    except Exception:
        try:
            temporary.unlink()
        except OSError:
            pass
        raise
    return bridge.atomic_replace(temporary, target)


def new_task_dir(parent, *, token=None, when=None):
    """在 `parent` 下创建任务目录 `AIPDF-<YYYYmmdd-HHMMSS>-<hex6>/` 并返回。"""
    parent = Path(parent)
    stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime() if when is None else when)
    folder = parent / f"{TASK_PREFIX}{stamp}-{token or uuid.uuid4().hex[:6]}"
    folder.mkdir(mode=0o700)
    return folder


def write_outputs(source_doc, source_path, translations, report, output_dir, *,
                  marker=True, high_risk=False, formats=("mono", "dual"),
                  blocks_by_page=None, task_dir=None):
    """构建并原子保存 mono/dual，落入任务目录；返回各产物路径。

    返回 `{"taskDir": str, "mono": str?, "dual": str?}`；受限文档额外带
    `"sourceProtection"`。单份失败时已保存的产物保留、临时文件被清理。
    """
    parent = Path(output_dir)
    if not parent.is_dir():
        raise _user_error("请选择有效的输出文件夹。")
    folder = Path(task_dir) if task_dir else new_task_dir(parent)
    pages = blocks_by_page if blocks_by_page is not None else _extract_blocks(source_doc)
    builders = {"mono": build_mono, "dual": build_dual}
    result = {"taskDir": str(folder)}
    for kind in formats:
        builder = builders.get(kind)
        if builder is None:
            raise _user_error(f"不支持的输出格式：{kind}")
        doc = builder(source_doc, translations, report, marker=marker,
                      high_risk=high_risk, blocks_by_page=pages)
        try:
            saved = save_document(doc, folder / output_filename(source_path, kind, folder))
        finally:
            doc.close()
        result[kind] = str(saved)
    info = getattr(report, "source_protection", None)
    if info and info.get("restricted"):
        result["sourceProtection"] = info
    return result


__all__ = [
    "MARKER_TEXT", "MARKER_SIZE", "MARKER_COLOR", "MARKER_FONT", "TASK_PREFIX",
    "MONO_SUFFIX", "DUAL_SUFFIX", "add_marker", "describe_source_protection",
    "page_translations", "build_mono", "build_dual", "output_filename",
    "save_document", "new_task_dir", "write_outputs",
]
