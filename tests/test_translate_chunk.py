# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""token 预算分块与译后重组测试；只用合成文本，不读写任何用户文档。

覆盖规格 §3：保守 token 估算、句边界切分、目标/硬上限预算、超长段落
（句子 → 逗号/分号 → 强制字符切分）与 `segment_split` 降级、上下文重叠
（仅上一块最后一句、不回填）以及逐段译文的按块重组与数量不匹配降级。
"""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from translate import chunk, extract


def block(text, page=1):
    """构造一个带 text 的 TextBlock；chunk 模块只读取 text 属性。"""
    return extract.TextBlock(
        page_no=page, rect=extract.geometry.RectNorm(0.0, 0.0, 1.0, 1.0, 0), text=text)


SENTENCE_ENDS = ".!?。！？"


class TokenEstimateTests(unittest.TestCase):
    def test_empty_text_is_zero(self):
        self.assertEqual(chunk.estimate_tokens(""), 0)
        self.assertEqual(chunk.estimate_tokens(None), 0)

    def test_cjk_counts_one_token_per_char(self):
        self.assertEqual(chunk.estimate_tokens("中文测试"), 4)
        self.assertEqual(chunk.estimate_tokens("日本語のテキスト"), 8)

    def test_latin_uses_conservative_bound(self):
        # 8 字符：4 字符/token 得 2，保守取下界 len // 2 = 4。
        self.assertEqual(chunk.estimate_tokens("abcdefgh"), 4)

    def test_mixed_uses_conservative_bound(self):
        # cjk=2 + ceil(2/4)=1 → 3；len // 2 = 2；取较大者 3。
        self.assertEqual(chunk.estimate_tokens("中文ab"), 3)

    def test_estimate_is_monotonic(self):
        short = chunk.estimate_tokens("hello")
        long = chunk.estimate_tokens("hello " * 50)
        self.assertLess(short, long)


class SentenceSplitTests(unittest.TestCase):
    def test_splits_latin_on_punctuation_with_space(self):
        self.assertEqual(chunk.split_sentences("One. Two! Three?"),
                         ["One. ", "Two! ", "Three?"])

    def test_decimal_point_is_not_a_boundary(self):
        self.assertEqual(chunk.split_sentences("Version 3.14 is pi."),
                         ["Version 3.14 is pi."])

    def test_splits_cjk_without_trailing_space(self):
        self.assertEqual(chunk.split_sentences("中文。第二句！第三"),
                         ["中文。", "第二句！", "第三"])

    def test_split_preserves_original_text(self):
        for text in ("One. Two! Three?", "中文。第二句！", "a.b.c", "no ends here"):
            self.assertEqual("".join(chunk.split_sentences(text)), text)

    def test_last_sentence(self):
        self.assertEqual(chunk.last_sentence("One. Two. Three."), "Three.")
        self.assertEqual(chunk.last_sentence("中文。最后一句"), "最后一句")
        self.assertEqual(chunk.last_sentence(""), "")


class ChunkBlocksTests(unittest.TestCase):
    def test_empty_or_blank_blocks_produce_no_chunks(self):
        self.assertEqual(chunk.chunk_blocks([]), [])
        self.assertEqual(chunk.chunk_blocks([block("   "), block("")]), [])

    def test_single_small_block_becomes_one_chunk(self):
        chunks = chunk.chunk_blocks([block("Hello world.")])
        self.assertEqual(len(chunks), 1)
        self.assertEqual(chunks[0].source_indices, [0])
        self.assertEqual(chunks[0].chunks, ["Hello world."])
        self.assertEqual(chunks[0].text, "Hello world.")
        self.assertEqual(chunks[0].overlap_count, 0)
        self.assertEqual(chunks[0].token_estimate, chunk.estimate_tokens("Hello world."))
        self.assertFalse(chunks[0].degraded)

    def test_target_budget_splits_across_blocks(self):
        blocks = [block("a" * 10), block("b" * 10), block("c" * 10)]
        chunks = chunk.chunk_blocks(blocks, target_tokens=12)
        self.assertEqual(len(chunks), 2)
        self.assertEqual(chunks[0].source_indices, [0, 1])
        self.assertEqual(chunks[1].source_indices, [2])
        for item in chunks:
            self.assertLessEqual(item.token_estimate, 12)

    def test_chunk_id_is_sequential(self):
        blocks = [block("a" * 10), block("b" * 10), block("c" * 10)]
        chunks = chunk.chunk_blocks(blocks, target_tokens=12)
        self.assertEqual([c.chunk_id for c in chunks], [0, 1])

    def test_hard_max_is_never_exceeded(self):
        blocks = [block("x" * 2000) for _ in range(10)]
        chunks = chunk.chunk_blocks(blocks)
        self.assertTrue(chunks)
        for item in chunks:
            self.assertLessEqual(item.token_estimate, chunk.HARD_MAX_TOKENS)

    def test_oversized_block_splits_at_sentence_boundaries(self):
        text = "First sentence. Second sentence. Third sentence."
        chunks = chunk.chunk_blocks([block(text)], target_tokens=10, hard_max=20)
        self.assertGreater(len(chunks), 1)
        for item in chunks:
            self.assertLessEqual(item.token_estimate, 20)
            self.assertIn("segment_split", item.reasons)
            self.assertTrue(item.degraded)
            # 每个核心段都停在句末，不在句子中间断开。
            for segment in item.core_segments:
                self.assertIn(segment.rstrip()[-1], SENTENCE_ENDS)
        self.assertEqual({i for item in chunks for i in item.source_indices}, {0})

    def test_punctuation_free_long_string_is_force_split(self):
        chunks = chunk.chunk_blocks([block("x" * 100)], target_tokens=10, hard_max=20)
        self.assertGreater(len(chunks), 1)
        for item in chunks:
            self.assertLessEqual(item.token_estimate, 20)
            self.assertIn("segment_split", item.reasons)
            for segment in item.core_segments:
                self.assertEqual(len(segment), 20)

    def test_overlap_carries_last_sentence_only(self):
        blocks = [block("Alpha one. Alpha two."), block("Beta one.")]
        chunks = chunk.chunk_blocks(blocks, target_tokens=10)
        self.assertEqual(len(chunks), 2)
        second = chunks[1]
        self.assertEqual(second.source_indices, [1])
        self.assertEqual(second.overlap_count, 1)
        self.assertEqual(second.overlap_indices, [0])
        self.assertEqual(second.chunks[0], "Alpha two.")
        self.assertEqual(second.core_segments, ["Beta one."])

    def test_first_chunk_has_no_overlap(self):
        chunks = chunk.chunk_blocks([block("Alpha one."), block("Beta one.")],
                                    target_tokens=1)
        self.assertGreater(len(chunks), 1)
        self.assertEqual(chunks[0].overlap_count, 0)
        self.assertEqual(chunks[0].overlap_indices, [])

    def test_overlap_can_be_disabled(self):
        blocks = [block("Alpha one. Alpha two."), block("Beta one.")]
        chunks = chunk.chunk_blocks(blocks, target_tokens=10, overlap=0)
        self.assertEqual(len(chunks), 2)
        self.assertEqual(chunks[1].overlap_count, 0)
        self.assertEqual(chunks[1].source_indices, [1])

    def test_all_source_indices_preserved_in_order(self):
        blocks = [block("word " * 200) for _ in range(6)]
        chunks = chunk.chunk_blocks(blocks)
        collected = [index for item in chunks for index in item.source_indices]
        self.assertEqual(collected, list(range(6)))


class ReassembleTests(unittest.TestCase):
    def setUp(self):
        # 目标 12：块 0、1 在 chunk 0；块 2 在 chunk 1，带块 1 的上下文重叠。
        self.blocks = [block("a" * 10), block("b" * 10), block("c" * 10)]
        self.chunks = chunk.chunk_blocks(self.blocks, target_tokens=12)

    def test_request_aligned_translations_skip_overlap(self):
        translations = ["t0", "t1", "context", "t2"]
        results = chunk.reassemble_chunks(self.chunks, translations)
        self.assertEqual([(r.index, r.text) for r in results],
                         [(0, "t0"), (1, "t1"), (2, "t2")])
        self.assertFalse(any(r.degraded for r in results))

    def test_core_aligned_translations_are_accepted(self):
        translations = ["t0", "t1", "t2"]
        results = chunk.reassemble_chunks(self.chunks, translations)
        self.assertEqual([(r.index, r.text) for r in results],
                         [(0, "t0"), (1, "t1"), (2, "t2")])
        self.assertFalse(any(r.degraded for r in results))

    def test_request_aligned_with_overlap_placeholders_tracks_request_order(self):
        # F1 引擎契约：重叠段用空串占位，整体长度 == 请求段数，逐段对位不得错位。
        translations = ["t0", "t1", "", "t2"]  # chunk0 无重叠；chunk1 前导重叠段占位
        results = chunk.reassemble_chunks(self.chunks, translations)
        self.assertEqual([(r.index, r.text) for r in results],
                         [(0, "t0"), (1, "t1"), (2, "t2")])
        self.assertFalse(any(r.degraded for r in results))

    def test_fewer_translations_mark_missing_as_degraded(self):
        results = chunk.reassemble_chunks(self.chunks, ["t0"])
        self.assertEqual([r.index for r in results], [0, 1, 2])
        self.assertEqual(results[0].text, "t0")
        self.assertFalse(results[0].degraded)
        self.assertTrue(results[1].degraded and results[1].text == "")
        self.assertTrue(results[2].degraded and results[2].text == "")

    def test_more_translations_ignore_extras(self):
        translations = ["t0", "t1", "context", "t2", "EXTRA"]
        results = chunk.reassemble_chunks(self.chunks, translations)
        self.assertEqual([(r.index, r.text) for r in results],
                         [(0, "t0"), (1, "t1"), (2, "t2")])

    def test_empty_inputs(self):
        self.assertEqual(chunk.reassemble_chunks([], []), [])
        self.assertEqual(chunk.reassemble_chunks([], ["junk"]), [])


if __name__ == "__main__":
    unittest.main()
