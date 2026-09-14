# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译输出校验与每页降级报告测试；只用合成文本，不读写任何用户文档。

覆盖《需求总结.md》§安全边界 §2（长度比例、段落数、未译回 echo、元描述句）与
《翻译-回填排版规格.md》§9（降级原因枚举、报告 schema、聚合口径、可搜索性）。
"""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from translate import verify


class ReasonEnumTests(unittest.TestCase):
    def test_spec_reasons_are_all_present(self):
        expected = (
            "font_shrink", "overflow_margin", "clip", "font_fallback",
            "glyph_missing", "formula_fail", "table_fail", "reassembly_mismatch",
            "extraction_low_conf", "rotation_crop", "unsupported_script",
            "no_text_block", "skip_page", "page_failed", "raster_overlay",
            "segment_split", "search_verify_fail", "untranslated_echo",
        )
        for name in expected:
            self.assertIn(name, verify.DEGRADATION_REASONS)

    def test_empty_page_alias_is_registered(self):
        # extract.py（T3）对零文本块页使用 empty_page，报告必须能接收。
        self.assertEqual(verify.EMPTY_PAGE, "empty_page")
        self.assertIn(verify.EMPTY_PAGE, verify.DEGRADATION_REASONS)

    def test_reason_enum_has_no_duplicates(self):
        self.assertEqual(len(verify.DEGRADATION_REASONS),
                         len(set(verify.DEGRADATION_REASONS)))

    def test_every_reason_has_a_label(self):
        for reason in verify.DEGRADATION_REASONS:
            self.assertTrue(verify.REASON_LABELS.get(reason))

    def test_validation_reasons_are_constants(self):
        self.assertEqual(verify.VALIDATION_REASONS,
                         (verify.LENGTH_ANOMALY, verify.SEGMENT_MISMATCH,
                          verify.UNTRANSLATED_ECHO))

    def test_unsearchable_reasons_subset_of_enum(self):
        for reason in verify.UNSEARCHABLE_REASONS:
            self.assertIn(reason, verify.DEGRADATION_REASONS)

    def test_suspicious_instruction_is_registered_with_label(self):
        self.assertEqual(verify.SUSPICIOUS_INSTRUCTION, "suspicious_instruction")
        self.assertIn(verify.SUSPICIOUS_INSTRUCTION, verify.DEGRADATION_REASONS)
        self.assertIn("已保留原文", verify.REASON_LABELS[verify.SUSPICIOUS_INSTRUCTION])


class SplitSegmentsTests(unittest.TestCase):
    def test_empty_and_blank(self):
        self.assertEqual(verify.split_segments(""), [])
        self.assertEqual(verify.split_segments(None), [])
        self.assertEqual(verify.split_segments("  \n \n\t"), [])

    def test_splits_lines_and_drops_blank(self):
        self.assertEqual(verify.split_segments("a\nb\n\nc"), ["a", "b", "c"])

    def test_crlf_is_normalized(self):
        self.assertEqual(verify.split_segments("a\r\nb\rc"), ["a", "b", "c"])

    def test_single_line_is_one_segment(self):
        self.assertEqual(verify.split_segments("Hello world."), ["Hello world."])


class TextSimilarityTests(unittest.TestCase):
    def test_identical_is_one(self):
        self.assertEqual(verify.text_similarity("Hello World.", "hello world."), 1.0)

    def test_both_empty_is_one(self):
        self.assertEqual(verify.text_similarity("", ""), 1.0)
        self.assertEqual(verify.text_similarity(None, None), 1.0)

    def test_one_empty_is_zero(self):
        self.assertEqual(verify.text_similarity("abc", ""), 0.0)
        self.assertEqual(verify.text_similarity("", "abc"), 0.0)

    def test_different_languages_are_low(self):
        self.assertLess(verify.text_similarity("Good morning.", "早上好。"), 0.5)

    def test_near_identical_is_high(self):
        score = verify.text_similarity(
            "The quick brown fox jumps over the lazy dog.",
            "The quick brown fox jumps over the lazy dog!")
        self.assertGreater(score, verify.ECHO_SIMILARITY_THRESHOLD)


class DetectMetaTests(unittest.TestCase):
    def test_returns_none_for_normal_translation(self):
        self.assertIsNone(verify.detect_meta_response("早上好，祝你愉快。"))
        self.assertIsNone(verify.detect_meta_response("Good morning."))

    def test_english_leading_meta(self):
        self.assertIsNotNone(verify.detect_meta_response("Here is the translation: 你好。"))
        self.assertIsNotNone(verify.detect_meta_response("Sure, here is the translation:"))
        self.assertIsNotNone(verify.detect_meta_response("The translation: 你好。"))

    def test_refusal_like_meta(self):
        self.assertIsNotNone(verify.detect_meta_response("I cannot translate this text."))
        self.assertIsNotNone(verify.detect_meta_response("As an AI language model, I..."))

    def test_chinese_meta(self):
        self.assertIsNotNone(verify.detect_meta_response("以下是翻译：你好。"))
        self.assertIsNotNone(verify.detect_meta_response("译文如下："))
        self.assertIsNotNone(verify.detect_meta_response("抱歉，我无法翻译这段内容。"))

    def test_meta_phrase_not_at_start_is_ignored(self):
        # 正常译文里出现「以下是翻译」不应被误判（仅匹配开头）。
        self.assertIsNone(verify.detect_meta_response("报告提到「以下是翻译」这一段。"))
        self.assertIsNone(verify.detect_meta_response("The document repeats here is the translation section."))


class VerifyLengthRatioTests(unittest.TestCase):
    def test_normal_ratio_passes(self):
        result = verify.verify_translation("Good morning, have a nice day.",
                                           "早上好，祝你今天愉快。")
        self.assertFalse(result.has(verify.LENGTH_ANOMALY))
        self.assertTrue(result.ok)

    def test_too_short_flags_length_anomaly(self):
        result = verify.verify_translation("a" * 100, "b" * 5)
        self.assertTrue(result.has(verify.LENGTH_ANOMALY))
        self.assertFalse(result.ok)

    def test_too_long_flags_length_anomaly(self):
        result = verify.verify_translation("ab", "x" * 20)
        self.assertTrue(result.has(verify.LENGTH_ANOMALY))

    def test_boundary_values_are_inside(self):
        # 长度比正好 0.2 / 3.0 视为在范围内（闭区间）。
        inside_low = verify.verify_translation("a" * 10, "b" * 2)
        inside_high = verify.verify_translation("a" * 10, "b" * 30)
        self.assertFalse(inside_low.has(verify.LENGTH_ANOMALY))
        self.assertFalse(inside_high.has(verify.LENGTH_ANOMALY))

    def test_empty_source_and_translation_is_ok(self):
        result = verify.verify_translation("", "")
        self.assertTrue(result.ok)
        self.assertIsNone(result.length_ratio)
        self.assertEqual(result.reasons, [])

    def test_empty_source_with_translation_flags_anomaly(self):
        result = verify.verify_translation("", "hello")
        self.assertTrue(result.has(verify.LENGTH_ANOMALY))
        self.assertTrue(result.has(verify.SEGMENT_MISMATCH))

    def test_empty_translation_flags_anomaly(self):
        result = verify.verify_translation("Hello world.", "")
        self.assertTrue(result.has(verify.LENGTH_ANOMALY))
        self.assertTrue(result.has(verify.SEGMENT_MISMATCH))
        self.assertFalse(result.has(verify.UNTRANSLATED_ECHO))


class VerifySegmentTests(unittest.TestCase):
    def test_matching_segment_counts_pass(self):
        result = verify.verify_translation("First line.\nSecond line.",
                                           "第一行。\n第二行。")
        self.assertFalse(result.has(verify.SEGMENT_MISMATCH))
        self.assertEqual(result.source_segments, 2)
        self.assertEqual(result.translated_segments, 2)

    def test_mismatched_segment_counts_flagged(self):
        result = verify.verify_translation("First line.\nSecond line.",
                                           "第一行。第二行。")
        self.assertTrue(result.has(verify.SEGMENT_MISMATCH))
        self.assertEqual(result.source_segments, 2)
        self.assertEqual(result.translated_segments, 1)


class VerifyEchoTests(unittest.TestCase):
    def test_identical_text_is_echo(self):
        result = verify.verify_translation("This is a sentence.", "This is a sentence.")
        self.assertTrue(result.has(verify.UNTRANSLATED_ECHO))
        self.assertFalse(result.ok)
        self.assertEqual(result.details[verify.UNTRANSLATED_ECHO], "identical")

    def test_near_identical_is_echo(self):
        result = verify.verify_translation(
            "The quick brown fox jumps over the lazy dog.",
            "The quick brown fox jumps over the lazy dog!")
        self.assertTrue(result.has(verify.UNTRANSLATED_ECHO))

    def test_meta_response_is_echo(self):
        result = verify.verify_translation("Bonjour le monde.", "Here is the translation:")
        self.assertTrue(result.has(verify.UNTRANSLATED_ECHO))
        self.assertTrue(result.details[verify.UNTRANSLATED_ECHO].startswith("meta:"))

    def test_chinese_meta_response_is_echo(self):
        result = verify.verify_translation("Hello.", "以下是翻译：")
        self.assertTrue(result.has(verify.UNTRANSLATED_ECHO))

    def test_real_translation_is_not_echo(self):
        result = verify.verify_translation("Good morning.", "早上好。")
        self.assertFalse(result.has(verify.UNTRANSLATED_ECHO))
        self.assertTrue(result.ok)

    def test_language_pair_is_recorded(self):
        result = verify.verify_translation("Hello.", "你好。", source_lang="en", target_lang="zh")
        self.assertEqual(result.details["languages"], {"source": "en", "target": "zh"})


class ValidationResultTests(unittest.TestCase):
    def test_ok_is_derived_from_reasons(self):
        self.assertTrue(verify.ValidationResult().ok)
        self.assertFalse(verify.ValidationResult(reasons=[verify.LENGTH_ANOMALY]).ok)

    def test_to_dict(self):
        result = verify.verify_translation("a" * 100, "b" * 5)
        payload = result.to_dict()
        self.assertFalse(payload["ok"])
        self.assertIn(verify.LENGTH_ANOMALY, payload["reasons"])
        self.assertEqual(payload["source_segments"], 1)
        for key in ("ok", "reasons", "length_ratio", "source_segments",
                    "translated_segments", "similarity", "details"):
            self.assertIn(key, payload)


class DegradationReportTests(unittest.TestCase):
    def test_empty_report(self):
        report = verify.DegradationReport(task_id="t0")
        self.assertFalse(report.has_degradations())
        self.assertEqual(report.summary(),
                         {"degraded_pages": 0, "unsearchable_pages": 0, "total_pages": 0})
        self.assertEqual(report.to_dict()["pages"], [])

    def test_add_and_has_degradations(self):
        report = verify.DegradationReport(task_id="t1")
        report.add(3, verify.FONT_SHRINK)
        self.assertTrue(report.has_degradations())

    def test_duplicate_reason_recorded_once(self):
        report = verify.DegradationReport()
        report.add(3, verify.CLIP)
        report.add(3, verify.CLIP)
        page = report.to_dict()["pages"][0]
        self.assertEqual(page["reasons"], [verify.CLIP])

    def test_unknown_reason_raises(self):
        report = verify.DegradationReport()
        with self.assertRaises(ValueError):
            report.add(1, "not_a_real_reason")

    def test_to_dict_schema_and_aggregate(self):
        report = verify.DegradationReport(task_id="t1", total_pages=12)
        report.add(3, verify.FONT_SHRINK)
        report.add(3, verify.TABLE_FAIL)
        report.add(7, verify.RASTER_OVERLAY)
        report.add(7, verify.SEARCH_VERIFY_FAIL)
        payload = report.to_dict()
        self.assertEqual(payload["task_id"], "t1")
        self.assertEqual(payload["summary"],
                         {"degraded_pages": 2, "unsearchable_pages": 1, "total_pages": 12})
        self.assertEqual([page["page"] for page in payload["pages"]], [3, 7])
        page3, page7 = payload["pages"]
        self.assertTrue(page3["degraded"])
        self.assertTrue(page3["searchable"])
        self.assertEqual(page3["reasons"], [verify.FONT_SHRINK, verify.TABLE_FAIL])
        self.assertFalse(page7["searchable"])  # raster_overlay / search_verify_fail

    def test_searchable_override(self):
        report = verify.DegradationReport(task_id="t2", total_pages=5)
        report.add(1, verify.CLIP)
        report.set_searchable(1, False)
        self.assertFalse(report.to_dict()["pages"][0]["searchable"])
        self.assertEqual(report.summary()["unsearchable_pages"], 1)

    def test_auto_unsearchable_for_no_text_block(self):
        report = verify.DegradationReport(total_pages=3)
        report.add(2, verify.EMPTY_PAGE)
        self.assertFalse(report.to_dict()["pages"][0]["searchable"])

    def test_detail_is_recorded_and_serialized(self):
        report = verify.DegradationReport(task_id="t3", total_pages=4)
        report.add(2, verify.PAGE_FAILED, detail="timeout")
        page = report.to_dict()["pages"][0]
        self.assertEqual(page["detail"], {verify.PAGE_FAILED: "timeout"})

    def test_total_pages_falls_back_to_recorded_pages(self):
        report = verify.DegradationReport()
        report.add(1, verify.CLIP)
        self.assertEqual(report.summary()["total_pages"], 1)

    def test_set_total_pages(self):
        report = verify.DegradationReport()
        report.set_total_pages(10)
        report.add(1, verify.CLIP)
        self.assertEqual(report.summary()["total_pages"], 10)


if __name__ == "__main__":
    unittest.main()
