# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""mono/dual 译文输出测试；只用内存合成 PDF，不读写任何用户文档。

覆盖《翻译-回填排版规格.md》§7 dual 对齐不变量（页 N ↔ 页 N、跳过/失败补空白、
不自动横向）与 §9.3 页脚标记（默认开启、高风险强制）、《需求总结.md》受限文档策略
（输出不加密/不继承限制并披露）、产物命名去重、原子落盘与任务目录。
"""
import re
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

import pymupdf as fitz
from translate import bridge, output
from translate.chunk import BlockTranslation
from translate.verify import PAGE_FAILED, SKIP_PAGE, DegradationReport


class OutputTestCase(unittest.TestCase):
    def setUp(self):
        (ROOT / "tmp").mkdir(exist_ok=True)
        self._tmp = tempfile.TemporaryDirectory(prefix="aipdf-output-",
                                                dir=str(ROOT / "tmp"))
        self.addCleanup(self._tmp.cleanup)
        self.tmp = Path(self._tmp.name)

    def source(self, name, texts, width=400, height=500):
        """把若干文本页写成一份源 PDF，返回路径（合成文件，不含个人信息）。"""
        path = self.tmp / name
        doc = fitz.open()
        for text in texts:
            page = doc.new_page(width=width, height=height)
            page.insert_text((72, 90), text, fontsize=12)
        doc.save(str(path))
        doc.close()
        return path

    def restricted_source(self, name, texts):
        """权限加密源：空用户密码 + 仅允许打印（/P 禁止抽取/复制）。"""
        path = self.tmp / name
        doc = fitz.open()
        for text in texts:
            page = doc.new_page(width=400, height=500)
            page.insert_text((72, 90), text, fontsize=12)
        doc.save(str(path), encryption=fitz.PDF_ENCRYPT_AES_256, user_pw="",
                 owner_pw="owner-secret", permissions=int(fitz.PDF_PERM_PRINT))
        doc.close()
        return path

    def open(self, path):
        doc = fitz.open(str(path))
        self.addCleanup(doc.close)
        return doc

    def leftovers(self):
        return sorted(p.name for p in self.tmp.glob(bridge.TEMP_PREFIX + "*"))


class MonoOutputTests(OutputTestCase):
    def test_mono_replaces_translated_text_and_keeps_page_count(self):
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(
                src, {1: {0: "第一页译文"}, 2: {0: "第二页译文"}}, report, marker=False)
        self.addCleanup(mono.close)
        self.assertEqual(len(mono), 2)                 # 不扩页
        self.assertIn("第一页译文", mono[0].get_text())
        self.assertNotIn("Page one", mono[0].get_text())
        self.assertIn("第二页译文", mono[1].get_text())
        self.assertNotIn("Page two", mono[1].get_text())

    def test_mono_keeps_original_when_page_has_no_translation(self):
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "第一页译文"}}, report, marker=False)
        self.addCleanup(mono.close)
        self.assertIn("第一页译文", mono[0].get_text())
        self.assertIn("Page two body", mono[1].get_text())   # 未译页保留原文

    def test_mono_does_not_write_blank_page_for_skipped(self):
        """mono 跳过页保留原文，不补空白（补空白只服务 dual 对齐）。"""
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        report = DegradationReport(task_id="t")
        report.add(2, SKIP_PAGE)
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "第一页译文"}}, report, marker=False)
        self.addCleanup(mono.close)
        self.assertEqual(len(mono), 2)
        self.assertIn("Page two body", mono[1].get_text())


class DualOutputTests(OutputTestCase):
    def test_dual_interleaves_original_then_translated(self):
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            dual = output.build_dual(
                src, {1: {0: "第一页译文"}, 2: {0: "第二页译文"}}, report, marker=False)
        self.addCleanup(dual.close)
        self.assertEqual(len(dual), 4)                 # 原1,译1,原2,译2
        self.assertIn("Page one body", dual[0].get_text())
        self.assertIn("第一页译文", dual[1].get_text())
        self.assertNotIn("Page one", dual[1].get_text())
        self.assertIn("Page two body", dual[2].get_text())
        self.assertIn("第二页译文", dual[3].get_text())

    def test_dual_blank_padding_preserves_alignment_for_failed_page(self):
        src_path = self.source("报告.pdf", ["Page one", "Page two", "Page three"])
        report = DegradationReport(task_id="t")
        report.add(2, PAGE_FAILED)
        with fitz.open(str(src_path)) as src:
            dual = output.build_dual(
                src, {1: {0: "第一页译文"}, 3: {0: "第三页译文"}}, report, marker=False)
        self.addCleanup(dual.close)
        self.assertEqual(len(dual), 6)                 # 原1,译1,原2,_,原3,译3
        self.assertIn("Page two", dual[2].get_text())  # 原页 2 仍在
        self.assertEqual(dual[3].get_text().strip(), "")   # 译页 2 = 空白占位
        self.assertIn("第三页译文", dual[5].get_text())

    def test_dual_blank_padding_for_skip_and_unknown_pages(self):
        src_path = self.source("报告.pdf", ["Page one", "Page two"])
        report = DegradationReport(task_id="t")
        report.add(1, SKIP_PAGE)
        with fitz.open(str(src_path)) as src:
            dual = output.build_dual(src, {2: {0: "第二页译文"}}, report, marker=False)
        self.addCleanup(dual.close)
        self.assertEqual(len(dual), 4)
        self.assertEqual(dual[1].get_text().strip(), "")   # 跳过的页 → 译页空白
        self.assertIn("第二页译文", dual[3].get_text())

    def test_dual_explicit_blank_pages_override(self):
        src_path = self.source("报告.pdf", ["Page one", "Page two"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            dual = output.build_dual(src, {1: {0: "一"}, 2: {0: "二"}}, report,
                                     marker=False, blank_pages={1})
        self.addCleanup(dual.close)
        self.assertEqual(dual[1].get_text().strip(), "")
        self.assertIn("二", dual[3].get_text())

    def test_dual_keeps_page_geometry_no_auto_landscape(self):
        """默认排布不横向、不改页尺寸：译页与源页同宽高。"""
        src_path = self.source("报告.pdf", ["Page one", "Page two"], width=400, height=500)
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            dual = output.build_dual(src, {1: {0: "第一页译文"}}, report, marker=False)
        self.addCleanup(dual.close)
        self.assertAlmostEqual(dual[0].rect.width, 400, places=1)
        self.assertAlmostEqual(dual[1].rect.width, 400, places=1)
        self.assertLess(dual[1].rect.width, dual[1].rect.height)  # 纵向，不横向

    def test_dual_report_uses_source_page_numbers(self):
        """交错页序不污染报告页码：回填在第 2 页的降级记在第 2 页，而非第 4 页。"""
        src_path = self.source("报告.pdf", ["Page one", "Two"])
        report = DegradationReport(task_id="t")
        long_text = "The translated version is considerably longer than the source. " * 20
        with fitz.open(str(src_path)) as src:
            dual = output.build_dual(src, {2: {0: long_text}}, report, marker=False)
        self.addCleanup(dual.close)
        pages = {item["page"] for item in report.to_dict()["pages"]}
        self.assertIn(2, pages)
        self.assertNotIn(4, pages)

    def test_dual_blank_page_keeps_cropbox_offset(self):
        """F14/F38：补空白页保留源页 CropBox 原点偏移，与源页几何一致。"""
        path = self.tmp / "crop.pdf"
        doc = fitz.open()
        for text in ("Page one", "Page two"):
            page = doc.new_page(width=595, height=842)
            page.set_cropbox(fitz.Rect(50, 100, 545, 600))
            page.insert_text((120, 260), text, fontsize=12)
        doc.save(str(path))
        doc.close()

        report = DegradationReport(task_id="t")
        report.add(1, SKIP_PAGE)   # 第 1 页补空白占位
        with fitz.open(str(path)) as src:
            dual = output.build_dual(src, {2: {0: "第二页译文"}}, report, marker=False)
        self.addCleanup(dual.close)
        self.assertEqual(len(dual), 4)
        self.assertEqual(dual[1].cropbox, dual[0].cropbox)
        self.assertAlmostEqual(dual[1].cropbox.x0, 50, places=1)
        self.assertAlmostEqual(dual[1].cropbox.y0, 100, places=1)


class MarkerTests(OutputTestCase):
    def test_marker_default_on_for_mono_and_dual(self):
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "译文"}}, report)
            dual = output.build_dual(src, {1: {0: "译文"}}, report)
        self.addCleanup(mono.close)
        self.addCleanup(dual.close)
        self.assertTrue(mono[0].search_for(output.MARKER_TEXT))
        self.assertTrue(dual[0].search_for(output.MARKER_TEXT))  # 原页
        self.assertTrue(dual[1].search_for(output.MARKER_TEXT))  # 译页

    def test_marker_can_be_disabled_for_low_risk(self):
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "译文"}}, report, marker=False)
        self.addCleanup(mono.close)
        self.assertFalse(mono[0].search_for(output.MARKER_TEXT))

    def test_marker_mandatory_for_high_risk_even_when_disabled(self):
        """法律/医学高风险模板：marker=False 也必须写出页脚标记（不可关闭）。"""
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "译文"}}, report,
                                     marker=False, high_risk=True)
        self.addCleanup(mono.close)
        self.assertTrue(mono[0].search_for(output.MARKER_TEXT))

    def test_marker_not_in_body_and_tiny_grey(self):
        """标记为 6pt 极小字号、不落在正文块区域内。"""
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "译文"}}, report)
        self.addCleanup(mono.close)
        hits = mono[0].search_for(output.MARKER_TEXT)
        self.assertTrue(hits)
        crop = mono[0].cropbox
        self.assertLess(hits[0].y0, crop.y1)
        self.assertGreater(hits[0].y0, crop.y1 * 0.9)   # 页脚带

    def test_marker_does_not_overwrite_footer_body_text(self):
        """F13：正文落到页脚带时跳过标记，绝不覆盖正文。"""
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=400, height=500)
        page.insert_text((40, 495), "FOOTER CONTENT", fontsize=9)
        self.assertFalse(output.add_marker(page))
        self.assertIn("FOOTER CONTENT", page.get_text())
        self.assertFalse(page.search_for(output.MARKER_TEXT))


class TranslationFormsTests(OutputTestCase):
    def test_flat_blocktranslations_group_by_page(self):
        src_path = self.source("报告.pdf", ["Page one", "Page two"])
        report = DegradationReport(task_id="t")
        translations = [BlockTranslation(index=0, text="甲"), BlockTranslation(index=1, text="乙")]
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, translations, report, marker=False)
        self.addCleanup(mono.close)
        self.assertIn("甲", mono[0].get_text())
        self.assertIn("乙", mono[1].get_text())

    def test_flat_degraded_segment_keeps_original(self):
        src_path = self.source("报告.pdf", ["Page one", "Page two"])
        report = DegradationReport(task_id="t")
        translations = [BlockTranslation(index=0, text="甲", degraded=True),
                        BlockTranslation(index=1, text="乙")]
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, translations, report, marker=False)
        self.addCleanup(mono.close)
        self.assertIn("Page one", mono[0].get_text())   # degraded → 保留原文
        self.assertIn("乙", mono[1].get_text())

    def test_flat_parallel_list_maps_positionally(self):
        src_path = self.source("报告.pdf", ["Page one", "Page two"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, ["甲", "乙"], report, marker=False)
        self.addCleanup(mono.close)
        self.assertIn("甲", mono[0].get_text())
        self.assertIn("乙", mono[1].get_text())

    def test_mapping_value_accepts_blocktranslation_list(self):
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(
                src, {1: [BlockTranslation(index=0, text="甲")]}, report, marker=False)
        self.addCleanup(mono.close)
        self.assertIn("甲", mono[0].get_text())

    def test_flat_duplicate_index_fragments_are_concatenated(self):
        # R5-2 防线：同一下标的多片译文按序拼接，不因覆盖而丢片。
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        translations = [BlockTranslation(index=0, text="前半"),
                        BlockTranslation(index=0, text="后半")]
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, translations, report, marker=False)
        self.addCleanup(mono.close)
        self.assertIn("前半后半", mono[0].get_text())


class FilenameTests(OutputTestCase):
    def test_default_names(self):
        src_path = self.tmp / "报告.pdf"
        self.assertEqual(output.output_filename(src_path, "mono"),
                         "报告-纯译文.pdf")
        self.assertEqual(output.output_filename(src_path, "dual"),
                         "报告-对照.pdf")

    def test_collision_appends_sequence_number(self):
        src_path = self.source("报告.pdf", ["Page one"])
        self.assertEqual(output.output_filename(src_path, "mono", self.tmp),
                         "报告-纯译文.pdf")
        (self.tmp / "报告-纯译文.pdf").write_bytes(b"x")
        self.assertEqual(output.output_filename(src_path, "mono", self.tmp),
                         "报告 (2)-纯译文.pdf")
        (self.tmp / "报告 (2)-纯译文.pdf").write_bytes(b"x")
        self.assertEqual(output.output_filename(src_path, "mono", self.tmp),
                         "报告 (3)-纯译文.pdf")

    def test_unknown_kind_rejected(self):
        with self.assertRaises(ValueError):
            output.output_filename(self.tmp / "报告.pdf", "side")

    def test_markdown_name_uses_md_extension(self):
        src_path = self.tmp / "报告.pdf"
        self.assertEqual(output.output_filename(src_path, "markdown"), "报告-译文.md")
        (self.tmp / "报告-译文.md").write_text("x", encoding="utf-8")
        self.assertEqual(output.output_filename(src_path, "markdown", self.tmp),
                         "报告 (2)-译文.md")


class SaveDocumentTests(OutputTestCase):
    def test_atomic_save_leaves_no_temp_and_outputs_unencrypted(self):
        doc = fitz.open()
        doc.new_page().insert_text((72, 72), "机器译文", fontname="china-s")
        target = output.save_document(doc, self.tmp / "out.pdf")
        doc.close()
        self.assertTrue(target.is_file())
        self.assertEqual(self.leftovers(), [])
        reopened = self.open(target)
        self.assertFalse(reopened.is_encrypted)
        self.assertFalse(output.describe_source_protection(reopened)["restricted"])
        self.assertIn("机器译文", reopened[0].get_text())

    def test_empty_document_is_rejected(self):
        with self.assertRaises(Exception):
            output.save_document(fitz.open(), self.tmp / "empty.pdf")

    def test_task_dir_name_shape(self):
        folder = output.new_task_dir(self.tmp)
        self.assertRegex(folder.name, r"^AIPDF-\d{8}-\d{6}-[0-9a-f]{6}$")
        self.assertTrue(folder.is_dir())


class RestrictedDocTests(OutputTestCase):
    def test_restricted_source_is_disclosed_and_not_inherited(self):
        src_path = self.restricted_source("受限.pdf", ["Secret original body"])
        report = DegradationReport(task_id="t")
        source = self.open(src_path)
        info = output.describe_source_protection(source)
        self.assertTrue(info["restricted"])
        self.assertTrue(info["disclosure"])

        mono = output.build_mono(source, {1: {0: "机器译文"}}, report, marker=False)
        self.addCleanup(mono.close)
        self.assertEqual(getattr(report, "source_protection", None), info)

        saved = output.save_document(mono, self.tmp / "受限-纯译文.pdf")
        reopened = self.open(saved)
        self.assertFalse(reopened.is_encrypted)
        self.assertFalse(output.describe_source_protection(reopened)["restricted"])
        self.assertIn("机器译文", reopened[0].get_text())
        self.assertNotIn("Secret original", reopened[0].get_text())

    def test_plain_source_is_not_flagged(self):
        src_path = self.source("普通.pdf", ["Plain body"])
        source = self.open(src_path)
        info = output.describe_source_protection(source)
        self.assertFalse(info["restricted"])
        self.assertEqual(info["disclosure"], "")


class WriteOutputsTests(OutputTestCase):
    def test_write_outputs_into_task_dir(self):
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            result = output.write_outputs(
                src, src_path, {1: {0: "第一页译文"}, 2: {0: "第二页译文"}},
                report, self.tmp, marker=False)
        self.assertRegex(Path(result["taskDir"]).name,
                         r"^AIPDF-\d{8}-\d{6}-[0-9a-f]{6}$")
        mono = Path(result["mono"])
        dual = Path(result["dual"])
        self.assertTrue(mono.is_file() and mono.name.endswith("-纯译文.pdf"))
        self.assertTrue(dual.is_file() and dual.name.endswith("-对照.pdf"))
        self.assertEqual(mono.parent, Path(result["taskDir"]))

        mono_doc = self.open(mono)
        dual_doc = self.open(dual)
        self.assertEqual(len(mono_doc), 2)
        self.assertEqual(len(dual_doc), 4)

    def test_write_outputs_formats_subset(self):
        src_path = self.source("报告.pdf", ["Page one"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            result = output.write_outputs(src, src_path, {1: {0: "译文"}}, report,
                                          self.tmp, formats=("mono",), marker=False)
        self.assertIn("mono", result)
        self.assertNotIn("dual", result)


class MarkdownOutputTests(OutputTestCase):
    def test_build_markdown_uses_translation_and_falls_back_to_original(self):
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            text = output.build_markdown(src, {1: {0: "第一页译文"}}, report, title="报告")
        self.assertIn("# 报告", text)
        self.assertIn("## 第 1 页", text)
        self.assertIn("## 第 2 页", text)
        self.assertIn("第一页译文", text)
        self.assertIn("Page two body", text)  # 无译文块回退原文，不丢内容

    def test_write_outputs_produces_markdown_file(self):
        src_path = self.source("报告.pdf", ["Page one body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            result = output.write_outputs(
                src, src_path, {1: {0: "第一页译文"}}, report, self.tmp,
                formats=("markdown",), marker=False)
        md = Path(result["markdown"])
        self.assertTrue(md.is_file())
        self.assertEqual(md.name, "报告-译文.md")
        self.assertIn("第一页译文", md.read_text(encoding="utf-8"))

    def test_write_outputs_pdf_plus_markdown(self):
        src_path = self.source("报告.pdf", ["Page one body"])
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            result = output.write_outputs(
                src, src_path, {1: {0: "第一页译文"}}, report, self.tmp,
                formats=("mono", "markdown"), marker=False)
        self.assertIn("mono", result)
        self.assertIn("markdown", result)
        self.assertTrue(Path(result["mono"]).name.endswith("-纯译文.pdf"))
        self.assertTrue(Path(result["markdown"]).name.endswith("-译文.md"))
        self.assertEqual(self.leftovers(), [])  # 原子写不留临时文件


class SourceImmutableTests(OutputTestCase):
    def test_build_does_not_modify_source(self):
        src_path = self.source("报告.pdf", ["Page one body", "Page two body"])
        before = src_path.read_bytes()
        report = DegradationReport(task_id="t")
        with fitz.open(str(src_path)) as src:
            mono = output.build_mono(src, {1: {0: "译文"}}, report, marker=False)
            dual = output.build_dual(src, {1: {0: "译文"}}, report, marker=False)
            source_pages = len(src)
        mono.close()
        dual.close()
        self.assertEqual(src_path.read_bytes(), before)
        self.assertEqual(source_pages, 2)


if __name__ == "__main__":
    unittest.main()
