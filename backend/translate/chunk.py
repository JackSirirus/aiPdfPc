# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""token 预算分块与译后重组（翻译引擎 T4）。

按《翻译-回填排版规格.md》§3，把阅读序段落（T3 产出的 `TextBlock` 列表）切成
可发送给 LLM 的请求块，并把逐段译文按源块下标重组回填。

分块按 token 预算，不按页：一页可含多块，一块可跨页。相邻块携带上一块的**最后
一句**作为上下文（`overlap=1`），仅在请求中提示、不参与回填。任一块超过硬上限时
按句子边界切分，仍切不开的极端长串按字符强制切分并标 `segment_split` 降级。

估算不引入分词器依赖，用保守上界
`max(cjk_chars + ceil(other_chars / 4), len(text) // 2)`：
CJK 记 1 token/字、拉丁约 4 字符/token，混合文本取更保守的 `len // 2`，
估算值只用于切块，实际用量以服务商返回为准。本模块只依赖标准库，不导入
PyMuPDF，`blocks` 只要求每项带 `text` 属性。
"""
from __future__ import annotations

import math
import re
from dataclasses import dataclass, field

# —— 规格 §3.1 预算参数 ------------------------------------------------------
TARGET_TOKENS = 1200        # 单次请求目标 token（软上限，触发切块）
HARD_MAX_TOKENS = 3000      # 单次请求硬上限（固定，超过一律切分）
OVERLAP_SENTENCES = 1       # 上下文重叠：仅取上一块最后 1 句，不要求重译
LATIN_CHARS_PER_TOKEN = 4   # 拉丁约 4 字符/token（CJK 记 1 token/字）

# CJK / 假名 / 谚文按字计 1 token（与 extract 的脚本判定保持一致）。
_CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\u3040-\u30ff\uac00-\ud7af]")
# 句末标点：拉丁标点须后接空白/换行才算句界；CJK 标点直接成句。
_CJK_END = "。！？"
_LATIN_END = ".!?"
_CLOSERS = "\"'”’」』）)]】》〕"
# 超长单句的兜底断点（逗号/顿号/分号）。
_FALLBACK_BREAK = ",，、;；"


@dataclass
class Chunk:
    """一个发给 LLM 的请求块。

    `chunks` 为按请求顺序排列的逐段文本：先重叠上下文段（`overlap_count` 个），
    再待译核心段；`source_indices` 只列核心段的源块下标，与 `chunks` 的核心部分
    一一对应（同一源块被切分时会重复出现）。`text` 为拼接后的请求正文。
    `overlap_indices` 记录仅作上下文、不参与回填的源块下标。`chunk_id` 供
    `bridge` 按块拼装响应（规格 §3.2.3）。

    `split_indices` 列出本块核心段里因超长被切分的源块下标（去重升序），供编排层把
    `segment_split` 降级原因按页记入报告（F2）。
    """

    chunks: list = field(default_factory=list)
    text: str = ""
    source_indices: list = field(default_factory=list)
    token_estimate: int = 0
    chunk_id: int = 0
    overlap_indices: list = field(default_factory=list)
    degraded: bool = False
    reasons: list = field(default_factory=list)
    split_indices: list = field(default_factory=list)

    @property
    def overlap_count(self):
        """仅作上下文的前导段数（0 或 1），不参与回填。"""
        return max(0, len(self.chunks) - len(self.source_indices))

    @property
    def core_segments(self):
        """待译核心段文本，与 `source_indices` 一一对应。"""
        return self.chunks[self.overlap_count:]


@dataclass
class BlockTranslation:
    """重组结果：一段译文对应一个源块下标。

    `degraded=True` 表示该下标没有拿到对应译文（译文缺失/数量不足），调用方应
    保留原文并标降级，不得用相邻段的译文硬补。
    """

    index: int
    text: str
    degraded: bool = False


class _Atom:
    """分块的最小单位：一段 ≤ 硬上限的文本及其源块下标。"""

    __slots__ = ("text", "index", "tokens", "over_limit", "forced")

    def __init__(self, text, index, over_limit=False, forced=False):
        self.text = text
        self.index = index
        self.tokens = estimate_tokens(text)
        self.over_limit = over_limit      # 来自被切分的超长段落 → segment_split
        self.forced = forced              # 极端长串按字符强制切分


def estimate_tokens(text):
    """保守 token 估算，见模块 docstring；空文本记 0。"""
    if not text:
        return 0
    cjk = 0
    for ch in text:
        if _CJK_RE.match(ch):
            cjk += 1
    other = len(text) - cjk
    by_script = cjk + math.ceil(other / LATIN_CHARS_PER_TOKEN)
    return max(by_script, len(text) // 2)


def split_sentences(text):
    """按句末标点切句，保留原文空白。

    拉丁标点（`.!?`）后须接空白/换行或到达结尾才算句界，因此 `3.14` 不会被切开；
    CJK 标点（`。！？`）直接成句。返回片段按原文顺序拼接即等于输入。
    """
    text = text or ""
    if not text:
        return []
    sentences = []
    start = 0
    index = 0
    length = len(text)
    while index < length:
        char = text[index]
        if char in _CJK_END or char in _LATIN_END:
            end = index + 1
            while end < length and text[end] in _CLOSERS:
                end += 1
            boundary = char in _CJK_END or end >= length or text[end].isspace()
            if boundary:
                while end < length and text[end].isspace():
                    end += 1
                sentences.append(text[start:end])
                start = end
                index = end
                continue
        index += 1
    tail = text[start:]
    if tail:
        if tail.strip() or not sentences:
            sentences.append(tail)
        else:
            sentences[-1] += tail
    return sentences


def last_sentence(text):
    """最后一句（去首尾空白）；无句界时返回整段，空文本返回空串。"""
    parts = [part for part in split_sentences(text) if part.strip()]
    if not parts:
        return (text or "").strip()
    return parts[-1].strip()


def chunk_blocks(blocks, *, target_tokens=TARGET_TOKENS,
                 hard_max=HARD_MAX_TOKENS, overlap=OVERLAP_SENTENCES):
    """把阅读序 `blocks`（带 `text` 属性）切成 `Chunk` 列表。

    - 逐块累加估算 token，加入下一块会超过目标 `target_tokens` 时收束当前块。
    - 单块超过 `hard_max` 时先按句切分，仍超长的句子按逗号/分号兜底，最后按字符
      强制切分并记 `segment_split`。
    - 每个新块的正文前附上一块的最后一句作为上下文（`overlap=1`），仅作提示。
    """
    atoms = []
    for index, block in enumerate(blocks or []):
        text = (getattr(block, "text", "") or "").strip()
        if not text:
            continue
        for piece, over_limit, forced in _split_oversized(text, hard_max):
            if piece.strip():
                atoms.append(_Atom(piece, index, over_limit, forced))

    chunks = []
    current = []
    current_tokens = 0
    overlap_atoms = []
    overlap_indices = []

    def context_tokens():
        return sum(atom.tokens for atom in overlap_atoms)

    def flush():
        nonlocal current, current_tokens, overlap_atoms, overlap_indices
        if not current:
            return
        chunks.append(_build_chunk(len(chunks), overlap_atoms, overlap_indices, current))
        if overlap > 0:
            previous = current[-1]
            context = last_sentence(previous.text)
            if context and estimate_tokens(context) <= hard_max:
                overlap_atoms = [_Atom(context, previous.index)]
                overlap_indices = [previous.index]
            else:
                overlap_atoms = []
                overlap_indices = []
        current = []
        current_tokens = 0

    for atom in atoms:
        if current and (current_tokens + atom.tokens > target_tokens
                        or context_tokens() + current_tokens + atom.tokens > hard_max):
            flush()
        if not current and overlap_atoms and context_tokens() + atom.tokens > hard_max:
            overlap_atoms = []  # 上下文本身过大：宁丢上下文，不撑破硬上限
            overlap_indices = []
        current.append(atom)
        current_tokens += atom.tokens

    flush()
    return chunks


def reassemble_chunks(chunks, translations):
    """把逐段译文按源块下标重组为 `BlockTranslation` 列表。

    `translations` 是模型返回的逐段译文（规格 §3.3.1 的 `{index, text}` 展平）。
    接受两种对位，按长度自动判定：

    - **请求对齐（首选）**：`len(translations) == sum(len(chunk.chunks))`，即每个请求段
      （含重叠上下文段）一个译文。重叠段只作上下文、**不参与回填**，其位置可放任意占位。
      `engine._translate_blocks` 即按此契约产出（用空串占位重叠段）。
    - **核心对齐（兼容）**：`len(translations) == sum(len(chunk.source_indices))` 且与请求段数
      不同时，按核心段对位。仅当存在重叠段（请求段数 ≠ 核心段数）时才可能与请求对齐区分；
      无重叠时二者等价。

    译文多于预期时忽略多余项，少于预期时缺失下标以 `degraded=True` 标记，交由调用方保留
    原文。调用方应优先产出请求对齐输入，避免长度巧合带来的歧义。
    """
    chunks = list(chunks or [])
    translations = list(translations or [])
    request_total = sum(len(chunk.chunks) for chunk in chunks)
    core_total = sum(len(chunk.source_indices) for chunk in chunks)
    # 模型只返回核心段（不含重叠）时，len == core_total 且与请求段数不同。
    core_aligned = len(translations) == core_total and core_total != request_total

    results = []
    cursor = 0
    for chunk in chunks:
        slots = len(chunk.source_indices) if core_aligned else len(chunk.chunks)
        available = translations[cursor:cursor + slots]
        cursor += slots
        core = available if core_aligned else available[chunk.overlap_count:]
        for offset, index in enumerate(chunk.source_indices):
            if offset < len(core):
                results.append(BlockTranslation(index=index, text=core[offset]))
            else:
                results.append(BlockTranslation(index=index, text="", degraded=True))
    return results


# —— 超长段落切分（§3.2.2） --------------------------------------------------

def _split_oversized(text, limit):
    """把超过 `limit` token 的段落切到 ≤ limit，返回 (片段, over_limit, forced)。"""
    if estimate_tokens(text) <= limit:
        return [(text, False, False)]
    pieces = []
    for sentence in split_sentences(text):
        if not sentence:
            continue
        if estimate_tokens(sentence) <= limit:
            pieces.append((sentence, True, False))
        else:
            pieces.extend(_split_clause(sentence, limit))
    return pieces or [(text, True, True)]


def _split_clause(text, limit):
    """单句仍超限：按逗号/分号打包，极端长串按字符强制切分。"""
    pieces = []
    buffer = ""
    for clause in _clauses(text):
        if buffer and estimate_tokens(buffer + clause) > limit:
            pieces.append((buffer, True, False))
            buffer = ""
        if estimate_tokens(clause) > limit:
            pieces.extend((piece, True, True) for piece in _hard_slice(clause, limit))
        else:
            buffer += clause
    if buffer:
        pieces.append((buffer, True, False))
    return pieces or [(text, True, True)]


def _clauses(text):
    """按兜底断点切分，保留断点字符。"""
    clauses = []
    start = 0
    for index, char in enumerate(text):
        if char in _FALLBACK_BREAK:
            clauses.append(text[start:index + 1])
            start = index + 1
    if start < len(text):
        clauses.append(text[start:])
    return clauses


def _hard_slice(text, limit):
    """按字符强制切片；每片长度 = limit，估算 token 必 ≤ limit。"""
    limit = max(1, limit)
    return [text[i:i + limit] for i in range(0, len(text), limit)]


def _build_chunk(chunk_id, overlap_atoms, overlap_indices, core_atoms):
    segments = [atom.text for atom in overlap_atoms] + [atom.text for atom in core_atoms]
    source_indices = [atom.index for atom in core_atoms]
    text = "\n".join(segment for segment in segments if segment)
    reasons = []
    if any(atom.over_limit for atom in core_atoms):
        reasons.append("segment_split")
    # F2：被切分的源块下标（同一块可能切成多片），供编排层按页记 segment_split。
    split_indices = sorted({atom.index for atom in core_atoms if atom.over_limit})
    return Chunk(chunks=segments, text=text, source_indices=source_indices,
                 token_estimate=estimate_tokens(text), chunk_id=chunk_id,
                 overlap_indices=list(overlap_indices),
                 degraded=bool(reasons), reasons=reasons,
                 split_indices=split_indices)


__all__ = [
    "TARGET_TOKENS", "HARD_MAX_TOKENS", "OVERLAP_SENTENCES",
    "Chunk", "BlockTranslation", "estimate_tokens", "split_sentences",
    "last_sentence", "chunk_blocks", "reassemble_chunks",
]
