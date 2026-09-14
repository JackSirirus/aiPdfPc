# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译输出校验与每页降级报告（翻译引擎 T7）。

分两部分：

- `verify_translation()` 对一段源文本与其译文做输出校验，落实《需求总结.md》
  §安全边界 §2 的三条：**长度比例异常**（译/原长度比超出 `[0.2, 3.0]`）、
  **段落/段数不匹配**、**疑似未译回 echo**（译文与原文完全相同、相似度 > 95%，
  或模型返回「以下是翻译 / I cannot translate」这类元描述句）。命中项写入
  `ValidationResult`，调用方据此对该段逐段重译（限 1 次）；仍失败则保留原文并标
  `untranslated_echo` 降级。**校验失败绝不静默采用。**

- `DegradationReport` 按《翻译-回填排版规格.md》§9 汇总每页降级原因，产出与产物同
  目录保存的报告结构（§9.2）。报告只含页码、原因与可搜索性，**不含文档正文、密钥**。

所有降级原因都是模块常量，`DEGRADATION_REASONS` 是唯一枚举；`DegradationReport.add()`
拒绝未登记的原因，避免魔法字符串在各处漂移。仅依赖标准库（相似度用 `difflib`，
不引入分词器或第三方依赖）。
"""
from __future__ import annotations

from dataclasses import dataclass, field
import difflib
import re


# —— 校验阈值（《需求总结.md》§安全边界 §2） ---------------------------------
MIN_LENGTH_RATIO = 0.2            # 译/原长度比下界
MAX_LENGTH_RATIO = 3.0            # 译/原长度比上界
ECHO_SIMILARITY_THRESHOLD = 0.95  # 相似度高于此值判为疑似未译回 echo

# —— 校验标记（ValidationResult.reasons 的取值） -----------------------------
LENGTH_ANOMALY = "length_anomaly"    # 长度比例超出 [MIN_LENGTH_RATIO, MAX_LENGTH_RATIO]
SEGMENT_MISMATCH = "segment_mismatch"  # 源/译段落数不一致

# —— 降级原因常量（规格 §9.1，顺序与规格表一致） ----------------------------
FONT_SHRINK = "font_shrink"                  # 字号被缩小（S1）
OVERFLOW_MARGIN = "overflow_margin"          # 溢出到页边留白（S2）
CLIP = "clip"                                # 被裁切（S3）
FONT_FALLBACK = "font_fallback"              # 字体被替换
GLYPH_MISSING = "glyph_missing"              # 目标字形缺失
FORMULA_FAIL = "formula_fail"                # 公式回填失败
TABLE_FAIL = "table_fail"                    # 表格结构无法还原
REASSEMBLY_MISMATCH = "reassembly_mismatch"  # 译文段落与原文对不齐
EXTRACTION_LOW_CONF = "extraction_low_conf"  # 抽取置信度低（乱码/PUA）
ROTATION_CROP = "rotation_crop"              # 旋转/裁剪页回填受限
UNSUPPORTED_SCRIPT = "unsupported_script"    # 竖排/RTL 不支持，保留原文
NO_TEXT_BLOCK = "no_text_block"              # 无文本块（纯图/矢量）
EMPTY_PAGE = "empty_page"                    # extract.py 对零文本块页的同义记法
SKIP_PAGE = "skip_page"                      # 用户跳过
PAGE_FAILED = "page_failed"                  # 该页失败继续
RASTER_OVERLAY = "raster_overlay"            # 扫描页原文不可移除
SEGMENT_SPLIT = "segment_split"              # 超长段落被切分
SEARCH_VERIFY_FAIL = "search_verify_fail"    # 导出后校验未过
UNTRANSLATED_ECHO = "untranslated_echo"      # 译文与原文相同/高度相似或返回非译文内容

# 唯一枚举：规格 §9.1 的全部原因，外加抽取阶段（T3 `extract.py`）沿用、与
# `no_text_block` 同义的 `empty_page`，使抽取与回填共用同一份报告。
DEGRADATION_REASONS = (
    FONT_SHRINK, OVERFLOW_MARGIN, CLIP, FONT_FALLBACK, GLYPH_MISSING,
    FORMULA_FAIL, TABLE_FAIL, REASSEMBLY_MISMATCH, EXTRACTION_LOW_CONF,
    ROTATION_CROP, UNSUPPORTED_SCRIPT, NO_TEXT_BLOCK, EMPTY_PAGE, SKIP_PAGE,
    PAGE_FAILED, RASTER_OVERLAY, SEGMENT_SPLIT, SEARCH_VERIFY_FAIL,
    UNTRANSLATED_ECHO,
)

# 天然不可搜索的页（规格 §8.5 / §9.2）：扫描光栅叠加、导出校验未过、无文本块。
UNSEARCHABLE_REASONS = (
    RASTER_OVERLAY, SEARCH_VERIFY_FAIL, NO_TEXT_BLOCK, EMPTY_PAGE,
)

# 校验标记枚举（规格 §安全边界 §2 的检查项）。
VALIDATION_REASONS = (LENGTH_ANOMALY, SEGMENT_MISMATCH, UNTRANSLATED_ECHO)

_KNOWN_DEGRADATION = frozenset(DEGRADATION_REASONS)

# 规格 §9.1「用户可见文案」列；供结果区（F4/F6b）直接展示，不含文档正文。
REASON_LABELS = {
    FONT_SHRINK: "该页为适配译文缩小了字号",
    OVERFLOW_MARGIN: "该页译文超出原文块范围",
    CLIP: "该页存在被裁切的译文",
    FONT_FALLBACK: "该页使用了替代字体",
    GLYPH_MISSING: "该页存在无法显示的字符",
    FORMULA_FAIL: "该页公式未保留原样",
    TABLE_FAIL: "该页表格结构可能丢失",
    REASSEMBLY_MISMATCH: "该页部分内容未回填",
    EXTRACTION_LOW_CONF: "该页原文抽取可能不可靠",
    ROTATION_CROP: "该页因旋转/裁剪回填降级",
    UNSUPPORTED_SCRIPT: "该页文字方向不支持，保留原文",
    NO_TEXT_BLOCK: "该页无可用文本（纯图/矢量）",
    EMPTY_PAGE: "该页无可用文本（纯图/矢量）",
    SKIP_PAGE: "该页未翻译",
    PAGE_FAILED: "该页翻译失败",
    RASTER_OVERLAY: "该页原文为图像，未移除",
    SEGMENT_SPLIT: "该页有超长段落被切分",
    SEARCH_VERIFY_FAIL: "该页译文可能不可搜索",
    UNTRANSLATED_ECHO: "该段疑似未翻译/回显",
}

# 元描述/解释性语句：只在**开头**匹配，避免误伤正文中恰好出现这些词组的正常译文。
# 命中即判 `untranslated_echo`（非译文内容），不回填。
_META_PATTERNS = (
    ("here_is_translation", re.compile(
        r"^\s*(?:sure|of course|okay|ok)[,.!\s]+(?:here|below)?[^\n]{0,40}?"
        r"(?:is|are)\s+the\s+translat", re.I)),
    ("here_is_translation", re.compile(
        r"^\s*here\s+(?:is|are)\s+the\s+translat", re.I)),
    ("translation_label", re.compile(
        r"^\s*(?:the\s+)?translation\s*[:：]", re.I)),
    ("cannot_translate", re.compile(
        r"^\s*i\s+(?:cannot|can'?t|am unable to|am not able to)\s+translate", re.I)),
    ("as_an_ai", re.compile(r"^\s*as an ai\b", re.I)),
    ("zh_here_is_translation", re.compile(
        r"^\s*(?:以下|下面)?\s*(?:是|为)\s*(?:对应的)?\s*(?:翻译|译文|英文翻译|中文翻译)")),
    ("zh_translation_label", re.compile(
        r"^\s*(?:翻译|译文)\s*(?:如下|结果|是)?\s*[:：]")),
    ("zh_cannot_translate", re.compile(
        r"^\s*(?:抱歉|对不起)[，,。！! ]*(?:我)?\s*(?:无法|不能|没法)\s*翻译")),
)

_WHITESPACE_RE = re.compile(r"\s+")
_NEWLINE_RE = re.compile(r"\r\n|\r")


def split_segments(text):
    """把一段文本按行拆成段落/段，去掉空行与首尾空白。

    与 `chunk.chunk_blocks` 用 `\\n` 拼接逐段正文一致；单行文本即一个段。
    """
    if not text:
        return []
    normalized = _NEWLINE_RE.sub("\n", str(text))
    return [line.strip() for line in normalized.split("\n") if line.strip()]


def _normalize(text):
    """相似度比较前的规范化：去首尾空白、折叠内部空白、忽略大小写。"""
    return _WHITESPACE_RE.sub(" ", ("" if text is None else str(text)).strip()).casefold()


def text_similarity(source_text, translated_text):
    """规范化后的相似度（0–1），等价于「1 − 归一化编辑距离」。

    任一方为空时返回 0.0；双方都为空时返回 1.0。用 `difflib.SequenceMatcher`
    （`autojunk=False`，避免重复文本被自动忽略而低估相似度）。
    """
    left = _normalize(source_text)
    right = _normalize(translated_text)
    if not left and not right:
        return 1.0
    if not left or not right:
        return 0.0
    return difflib.SequenceMatcher(None, left, right, autojunk=False).ratio()


def detect_meta_response(text):
    """返回译文开头的元描述/解释性语句标签，没有则返回 `None`。"""
    if not text or not str(text).strip():
        return None
    for label, pattern in _META_PATTERNS:
        if pattern.match(str(text)):
            return label
    return None


@dataclass
class ValidationResult:
    """一段源文/译文的校验结果。

    `reasons` 为 `VALIDATION_REASONS` 中命中的标记；`ok` 为「无任何命中」。
    `length_ratio` 为译/原字符长度比（源为空时为 `None`）。`details` 记录命中细节
    （如相似度、命中的元描述标签、语言对），便于结果区展示与排查。
    """

    reasons: list = field(default_factory=list)
    length_ratio: float = None
    source_segments: int = 0
    translated_segments: int = 0
    similarity: float = 0.0
    details: dict = field(default_factory=dict)

    @property
    def ok(self):
        return not self.reasons

    def has(self, reason):
        return reason in self.reasons

    def to_dict(self):
        return {
            "ok": self.ok,
            "reasons": list(self.reasons),
            "length_ratio": self.length_ratio,
            "source_segments": self.source_segments,
            "translated_segments": self.translated_segments,
            "similarity": round(self.similarity, 4),
            "details": dict(self.details),
        }


def verify_translation(source_text, translated_text, source_lang=None, target_lang=None):
    """校验一段译文；返回 `ValidationResult`，任何异常都记入 `reasons`。

    检查项（《需求总结.md》§安全边界 §2）：
      1. 长度比例：译/原长度比必须落在 `[MIN_LENGTH_RATIO, MAX_LENGTH_RATIO]`，
         否则标 `length_anomaly`；
      2. 段落数：源/译按行拆段后的段数必须一致，否则标 `segment_mismatch`；
      3. 未译回 echo：译文与原文规范化后完全相同、相似度 > `ECHO_SIMILARITY_THRESHOLD`，
         或译文以元描述句开头，标 `untranslated_echo`。

    `source_lang` / `target_lang` 只用于记录语言对，不改变阈值（阈值对语言对统一）。
    """
    source = "" if source_text is None else str(source_text)
    translated = "" if translated_text is None else str(translated_text)
    reasons = []
    details = {}
    if source_lang is not None or target_lang is not None:
        details["languages"] = {"source": source_lang, "target": target_lang}

    # 1) 长度比例（按去首尾空白的字符数，避免空白造成的无意义偏差）。
    source_len = len(source.strip())
    translated_len = len(translated.strip())
    if source_len == 0:
        ratio = None
        length_ok = translated_len == 0
    else:
        ratio = translated_len / source_len
        length_ok = MIN_LENGTH_RATIO <= ratio <= MAX_LENGTH_RATIO
    if not length_ok:
        reasons.append(LENGTH_ANOMALY)
        details[LENGTH_ANOMALY] = {"ratio": ratio, "source_len": source_len,
                                   "translated_len": translated_len}

    # 2) 段落/段数。
    source_segments = split_segments(source)
    translated_segments = split_segments(translated)
    if len(source_segments) != len(translated_segments):
        reasons.append(SEGMENT_MISMATCH)
        details[SEGMENT_MISMATCH] = {"source": len(source_segments),
                                     "translated": len(translated_segments)}

    # 3) 未译回 echo。
    score = text_similarity(source, translated)
    if source.strip() and translated.strip():
        if _normalize(source) == _normalize(translated):
            reasons.append(UNTRANSLATED_ECHO)
            details[UNTRANSLATED_ECHO] = "identical"
        else:
            meta = detect_meta_response(translated)
            if meta is not None:
                reasons.append(UNTRANSLATED_ECHO)
                details[UNTRANSLATED_ECHO] = "meta:" + meta
            elif score > ECHO_SIMILARITY_THRESHOLD:
                reasons.append(UNTRANSLATED_ECHO)
                details[UNTRANSLATED_ECHO] = {"similarity": score}

    return ValidationResult(reasons=reasons, length_ratio=ratio,
                            source_segments=len(source_segments),
                            translated_segments=len(translated_segments),
                            similarity=score, details=details)


class DegradationReport:
    """每页降级报告（规格 §9.2）。

    `add(page_no, reason, detail)` 逐条记录；同一页同一原因只记一次（保持首次顺序）。
    未登记的 `reason` 抛 `ValueError`，防止魔法字符串漂移。`task_id` 与 `total_pages`
    用于报告头与聚合；`total_pages` 未显式给出时退化为「报告中出现过的页数」，
    整篇调用方应从抽取页数显式设置。
    """

    def __init__(self, task_id=None, total_pages=None):
        self.task_id = task_id
        self.total_pages = total_pages
        self._pages = {}

    def _entry(self, page_no):
        page = int(page_no)
        if page not in self._pages:
            self._pages[page] = {"reasons": [], "details": {}, "searchable": None}
        return self._pages[page]

    def add(self, page_no, reason, detail=None):
        """记录一页的一个降级原因；`detail` 可为任意可序列化的补充信息。"""
        if reason not in _KNOWN_DEGRADATION:
            raise ValueError(f"未登记的降级原因：{reason!r}。")
        entry = self._entry(page_no)
        if reason not in entry["reasons"]:
            entry["reasons"].append(reason)
        if detail is not None:
            entry["details"][reason] = detail
        return self

    def set_searchable(self, page_no, searchable=True):
        """显式覆盖某页可搜索性；未设置时按 `UNSEARCHABLE_REASONS` 自动推导。"""
        self._entry(page_no)["searchable"] = bool(searchable)
        return self

    def set_total_pages(self, total_pages):
        """设置文档总页数，供 `summary()` 的聚合口径使用。"""
        self.total_pages = int(total_pages)
        return self

    def has_degradations(self):
        return any(page["reasons"] for page in self._pages.values())

    def _is_searchable(self, entry):
        if entry["searchable"] is not None:
            return entry["searchable"]
        return not any(reason in UNSEARCHABLE_REASONS for reason in entry["reasons"])

    def summary(self):
        """聚合：`{degraded_pages, unsearchable_pages, total_pages}`（规格 §9.2）。"""
        total = len(self._pages) if self.total_pages is None else self.total_pages
        return {
            "degraded_pages": sum(1 for p in self._pages.values() if p["reasons"]),
            "unsearchable_pages": sum(1 for p in self._pages.values()
                                      if not self._is_searchable(p)),
            "total_pages": total,
        }

    def to_dict(self):
        """报告结构（规格 §9.2）；`detail` 仅在存在时附加，不含文档正文。"""
        pages = []
        for page_no in sorted(self._pages):
            entry = self._pages[page_no]
            item = {
                "page": page_no,
                "degraded": bool(entry["reasons"]),
                "reasons": list(entry["reasons"]),
                "searchable": self._is_searchable(entry),
            }
            if entry["details"]:
                item["detail"] = dict(entry["details"])
            pages.append(item)
        return {"task_id": self.task_id, "pages": pages, "summary": self.summary()}


__all__ = [
    # 阈值与校验标记
    "MIN_LENGTH_RATIO", "MAX_LENGTH_RATIO", "ECHO_SIMILARITY_THRESHOLD",
    "LENGTH_ANOMALY", "SEGMENT_MISMATCH", "VALIDATION_REASONS",
    # 降级原因枚举与文案
    "FONT_SHRINK", "OVERFLOW_MARGIN", "CLIP", "FONT_FALLBACK", "GLYPH_MISSING",
    "FORMULA_FAIL", "TABLE_FAIL", "REASSEMBLY_MISMATCH", "EXTRACTION_LOW_CONF",
    "ROTATION_CROP", "UNSUPPORTED_SCRIPT", "NO_TEXT_BLOCK", "EMPTY_PAGE",
    "SKIP_PAGE", "PAGE_FAILED", "RASTER_OVERLAY", "SEGMENT_SPLIT",
    "SEARCH_VERIFY_FAIL", "UNTRANSLATED_ECHO",
    "DEGRADATION_REASONS", "UNSEARCHABLE_REASONS", "REASON_LABELS",
    # 函数与类型
    "verify_translation", "split_segments", "text_similarity",
    "detect_meta_response", "ValidationResult", "DegradationReport",
]
