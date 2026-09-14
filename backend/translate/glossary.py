# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""CSV 术语表：解析、匹配与译后命中校验。

用户术语表优先级高于模型默认译法：命中条目作为强约束写入 system（见
`format_glossary_for_prompt`），并在回填前用 `check_adherence` 校验译文是否遵循。

CSV 格式：首行可选表头，按名字识别 `term,translation,source_lang,target_lang,
case_sensitive,note`；无表头时按 `term,translation[,source_lang[,target_lang]]` 定位。
`#` 开头的行与空行是注释。只读用户指定路径，不缓存正文、不写任何其它位置。
"""
from __future__ import annotations

import csv
import io
import re
import warnings
from dataclasses import dataclass
from pathlib import Path


class GlossaryError(ValueError):
    """术语表文件缺失、编码错误或行格式不合法。"""


@dataclass
class GlossaryEntry:
    term: str
    translation: str
    source_lang: str = ""
    target_lang: str = ""
    # 默认大小写敏感；可按条目关闭，或在匹配时统一覆盖。
    case_sensitive: bool = True
    note: str = ""


_HEADER_ALIASES = {
    "term": {"term", "source", "源词", "源词条", "术语", "原文"},
    "translation": {"translation", "target", "译法", "目标词", "译文"},
    "source_lang": {"source_lang", "sourcelang", "src", "src_lang", "源语言"},
    "target_lang": {"target_lang", "targetlang", "tgt", "tgt_lang", "目标语言"},
    "case_sensitive": {"case_sensitive", "casesensitive", "大小写敏感"},
    "note": {"note", "comment", "说明", "备注"},
}

_TRUE = {"true", "1", "yes", "y", "是"}
_FALSE = {"false", "0", "no", "n", "否", ""}

# CJK / 假名 / 谚文：这些文字没有大小写，按子串匹配。
_CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\u3040-\u30ff\uac00-\ud7af]")


def load_glossary(path):
    """读取用户指定的 CSV 文件，返回 `GlossaryEntry` 列表。不写任何位置。"""
    target = Path(path)
    try:
        text = target.read_text(encoding="utf-8-sig")
    except FileNotFoundError:
        raise GlossaryError(f"术语表文件不存在：{target.name}。")
    except OSError as exc:
        raise GlossaryError(f"无法读取术语表文件：{exc}")
    return parse_glossary(text, source=target.name)


def parse_glossary(text, source="<glossary>"):
    """解析 CSV 文本；校验每行格式，重复/冲突条目告警但不中断。"""
    rows = []
    for line_no, cells in enumerate(csv.reader(io.StringIO(text)), 1):
        values = [(cell or "").strip() for cell in cells]
        if not any(values):
            continue
        if values[0].startswith("#"):
            continue
        rows.append((line_no, values))
    if not rows:
        return []

    mapping = None
    if _looks_like_header(rows[0][1]):
        mapping = _header_mapping(rows[0][1], source, rows[0][0])
        data_rows = rows[1:]
    else:
        data_rows = rows

    entries = [_entry_from_row(cells, mapping, line_no, source) for line_no, cells in data_rows]
    _warn_duplicates(entries, source)
    return entries


def format_glossary_for_prompt(entries):
    """渲染为 `Term → Translation` 行列表，注入 system。"""
    lines = []
    for entry in entries or []:
        term = (entry.term or "").strip()
        translation = (entry.translation or "").strip()
        if term and translation:
            lines.append(f"{term} → {translation}")
    return "\n".join(lines)


def match_glossary(text, entries, case_sensitive=None):
    """返回在 `text` 中命中的条目；拉丁词按全词边界，CJK 按子串。"""
    matched = []
    for entry in entries or []:
        sensitive = entry.case_sensitive if case_sensitive is None else case_sensitive
        if term_in_text(entry.term, text, sensitive):
            matched.append(entry)
    return matched


def check_adherence(source_text, translated_text, entries, case_sensitive=None):
    """译后校验：命中的术语其指定译法必须出现，否则记「术语未遵循」。"""
    violations = []
    for entry in match_glossary(source_text, entries, case_sensitive):
        sensitive = entry.case_sensitive if case_sensitive is None else case_sensitive
        if not _contains(entry.translation, translated_text, sensitive):
            violations.append({"term": entry.term, "translation": entry.translation})
    return violations


def find_duplicates(entries):
    """返回重复/冲突条目的 key 列表（按 term 忽略大小写）。"""
    seen = {}
    for entry in entries or []:
        seen.setdefault((entry.term or "").casefold(), []).append(entry)
    return sorted(key for key, group in seen.items() if len(group) > 1)


def term_in_text(term, text, case_sensitive=True):
    """判断术语是否作为独立出现于文本中。空术语返回 False。"""
    term = (term or "").strip()
    text = text or ""
    if not term or not text:
        return False
    if _CJK_RE.search(term):
        return _contains(term, text, case_sensitive)
    pattern = r"(?<![0-9A-Za-z_])" + re.escape(term) + r"(?![0-9A-Za-z_])"
    return re.search(pattern, text, 0 if case_sensitive else re.IGNORECASE) is not None


def _contains(needle, haystack, case_sensitive):
    needle = (needle or "").strip()
    if not needle:
        return True
    haystack = haystack or ""
    if case_sensitive:
        return needle in haystack
    return needle.casefold() in haystack.casefold()


def _looks_like_header(cells):
    if len(cells) < 2:
        return False
    first = _normalize_header(cells[0])
    second = _normalize_header(cells[1])
    return first in _HEADER_ALIASES["term"] and second in _HEADER_ALIASES["translation"]


def _normalize_header(cell):
    return re.sub(r"[\s\-]+", "_", (cell or "").strip().lower())


def _header_mapping(cells, source, line_no):
    mapping = {}
    for index, cell in enumerate(cells):
        name = _normalize_header(cell)
        for field, aliases in _HEADER_ALIASES.items():
            if name in aliases and field not in mapping:
                mapping[field] = index
    for required in ("term", "translation"):
        if required not in mapping:
            raise GlossaryError(f"{source} 第 {line_no} 行表头缺少 {required} 列。")
    return mapping


def _entry_from_row(cells, mapping, line_no, source):
    def take(field):
        if mapping is not None:
            index = mapping.get(field)
            return cells[index] if index is not None and index < len(cells) else ""
        fallback = {"term": 0, "translation": 1, "source_lang": 2, "target_lang": 3}
        index = fallback.get(field)
        return cells[index] if index is not None and index < len(cells) else ""

    term, translation = take("term"), take("translation")
    if not term:
        raise GlossaryError(f"{source} 第 {line_no} 行缺少术语（term）。")
    if not translation:
        raise GlossaryError(f"{source} 第 {line_no} 行缺少译法（translation）。")
    # 只有显式提供 case_sensitive 列才解析，否则保持默认（大小写敏感）。
    if mapping is not None and "case_sensitive" in mapping:
        case_sensitive = _parse_bool(take("case_sensitive"), source, line_no)
    else:
        case_sensitive = True
    return GlossaryEntry(term=term, translation=translation,
                         source_lang=take("source_lang"), target_lang=take("target_lang"),
                         case_sensitive=case_sensitive, note=take("note"))


def _parse_bool(cell, source, line_no):
    value = (cell or "").strip().lower()
    if value in _TRUE:
        return True
    if value in _FALSE:
        return False
    raise GlossaryError(f"{source} 第 {line_no} 行的 case_sensitive 应为 true/false。")


def _warn_duplicates(entries, source):
    duplicates = find_duplicates(entries)
    if not duplicates:
        return
    counts = {}
    for entry in entries:
        key = (entry.term or "").casefold()
        counts[key] = counts.get(key, 0) + 1
    detail = "; ".join(f"{key}(×{counts[key]})" for key in duplicates)
    warnings.warn(f"{source} 术语表存在重复或冲突条目：{detail}。", UserWarning, stacklevel=3)
