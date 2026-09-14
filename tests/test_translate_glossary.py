# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""术语表：CSV 解析、格式校验、匹配规则与译后命中校验。仅使用合成数据。"""
import sys
import tempfile
import unittest
import warnings
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
from translate import glossary


class GlossaryTestCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="aipdf-glossary-", dir=str(ROOT / "tmp"))
        cls.root = Path(cls.temp.name)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def write(self, name, content, encoding="utf-8"):
        path = self.root / name
        path.write_text(content, encoding=encoding)
        return path


class ParseTests(GlossaryTestCase):
    def test_positional_columns(self):
        path = self.write("positional.csv", "cat,猫\nbank,银行,en,zh\n")
        entries = glossary.load_glossary(path)
        self.assertEqual([(e.term, e.translation) for e in entries], [("cat", "猫"), ("bank", "银行")])
        self.assertEqual((entries[1].source_lang, entries[1].target_lang), ("en", "zh"))
        self.assertTrue(entries[0].case_sensitive)

    def test_header_columns(self):
        header = "term,translation,source_lang,target_lang,case_sensitive,note\n"
        path = self.write("header.csv", header + "cat,猫,en,zh,false,动物\n")
        entry = glossary.load_glossary(path)[0]
        self.assertEqual(entry.term, "cat")
        self.assertEqual(entry.translation, "猫")
        self.assertFalse(entry.case_sensitive)
        self.assertEqual(entry.note, "动物")

    def test_chinese_header_aliases(self):
        path = self.write("zh-header.csv", "源词,译法,源语言,目标语言,大小写敏感,说明\nGPU,图形处理器,en,zh,true,硬件\n")
        entry = glossary.load_glossary(path)[0]
        self.assertEqual((entry.term, entry.translation), ("GPU", "图形处理器"))
        self.assertTrue(entry.case_sensitive)
        self.assertEqual(entry.note, "硬件")

    def test_comments_and_blank_lines_skipped(self):
        path = self.write("comments.csv", "# 注释行\ncat,猫\n\n  \nbank,银行\n")
        entries = glossary.load_glossary(path)
        self.assertEqual(len(entries), 2)

    def test_bom_is_tolerated(self):
        path = self.write("bom.csv", "cat,猫\n", encoding="utf-8-sig")
        self.assertEqual(glossary.load_glossary(path)[0].term, "cat")

    def test_empty_file_returns_empty_list(self):
        path = self.write("empty.csv", "# only a comment\n\n")
        self.assertEqual(glossary.load_glossary(path), [])

    def test_missing_translation_raises(self):
        path = self.write("bad.csv", "cat\n")
        with self.assertRaises(glossary.GlossaryError):
            glossary.load_glossary(path)

    def test_empty_term_raises(self):
        path = self.write("bad-term.csv", ",猫\n")
        with self.assertRaises(glossary.GlossaryError):
            glossary.load_glossary(path)

    def test_invalid_case_sensitive_raises(self):
        path = self.write("bad-bool.csv", "term,translation,case_sensitive\ncat,猫,maybe\n")
        with self.assertRaises(glossary.GlossaryError):
            glossary.load_glossary(path)

    def test_missing_file_raises(self):
        with self.assertRaises(glossary.GlossaryError):
            glossary.load_glossary(self.root / "nope.csv")

    def test_load_does_not_write_side_files(self):
        path = self.write("side.csv", "cat,猫\n")
        before = sorted(p.name for p in self.root.iterdir())
        glossary.load_glossary(path)
        self.assertEqual(sorted(p.name for p in self.root.iterdir()), before)


class DuplicateTests(GlossaryTestCase):
    def test_duplicate_warns_but_returns_entries(self):
        path = self.write("dup.csv", "cat,猫\nCat,小猫\n")
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            entries = glossary.load_glossary(path)
        self.assertEqual(len(entries), 2)
        self.assertTrue(any("重复" in str(item.message) for item in caught))

    def test_find_duplicates_reports_keys(self):
        entries = [glossary.GlossaryEntry("GPU", "图形处理器"),
                   glossary.GlossaryEntry("gpu", "显卡"),
                   glossary.GlossaryEntry("CPU", "处理器")]
        self.assertEqual(glossary.find_duplicates(entries), ["gpu"])

    def test_no_duplicate_no_warning(self):
        path = self.write("unique.csv", "cat,猫\ndog,狗\n")
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            glossary.load_glossary(path)
        self.assertEqual(caught, [])


class FormatTests(GlossaryTestCase):
    def test_format_lines(self):
        entries = [glossary.GlossaryEntry("cat", "猫"), glossary.GlossaryEntry("bank", "银行")]
        self.assertEqual(glossary.format_glossary_for_prompt(entries), "cat → 猫\nbank → 银行")

    def test_format_skips_incomplete_entries(self):
        entries = [glossary.GlossaryEntry("cat", "猫"), glossary.GlossaryEntry("empty", "")]
        self.assertEqual(glossary.format_glossary_for_prompt(entries), "cat → 猫")

    def test_format_empty(self):
        self.assertEqual(glossary.format_glossary_for_prompt([]), "")


class MatchingTests(unittest.TestCase):
    def test_case_sensitive_by_default(self):
        entries = [glossary.GlossaryEntry("GPU", "图形处理器")]
        self.assertEqual(len(glossary.match_glossary("a GPU here", entries)), 1)
        self.assertEqual(glossary.match_glossary("a gpu here", entries), [])

    def test_case_insensitive_configurable(self):
        entries = [glossary.GlossaryEntry("GPU", "图形处理器")]
        self.assertEqual(len(glossary.match_glossary("a gpu here", entries, case_sensitive=False)), 1)

    def test_per_entry_case_insensitive_flag(self):
        entries = [glossary.GlossaryEntry("gpu", "图形处理器", case_sensitive=False)]
        self.assertEqual(len(glossary.match_glossary("A GPU shipped", entries)), 1)

    def test_latin_whole_word_boundary(self):
        entries = [glossary.GlossaryEntry("bank", "银行")]
        self.assertEqual(len(glossary.match_glossary("the bank loan", entries)), 1)
        self.assertEqual(glossary.match_glossary("bankrupt company", entries), [])

    def test_cjk_substring_matching(self):
        entries = [glossary.GlossaryEntry("银行", "bank")]
        self.assertEqual(len(glossary.match_glossary("银行存款", entries)), 1)

    def test_term_in_text_empty_inputs(self):
        self.assertFalse(glossary.term_in_text("", "text"))
        self.assertFalse(glossary.term_in_text("term", ""))


class AdherenceTests(unittest.TestCase):
    def setUp(self):
        self.entries = [glossary.GlossaryEntry("bank", "银行"),
                        glossary.GlossaryEntry("loan", "贷款")]

    def test_violation_when_glossary_translation_missing(self):
        violations = glossary.check_adherence("The bank approves the loan.", "银行批准了该申请。", self.entries)
        self.assertEqual(violations, [{"term": "loan", "translation": "贷款"}])

    def test_no_violation_when_both_present(self):
        violations = glossary.check_adherence("The bank approves the loan.", "银行批准了贷款。", self.entries)
        self.assertEqual(violations, [])

    def test_terms_not_in_source_are_not_checked(self):
        violations = glossary.check_adherence("A simple sentence.", "一句简单的话。", self.entries)
        self.assertEqual(violations, [])


if __name__ == "__main__":
    unittest.main()
