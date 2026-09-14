# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""回填排版引擎测试；只用内存合成 PDF，不读写任何用户文档。

覆盖规格 §4 溢出策略 S0→S3、§5 字体策略与 ToUnicode、§6 真实移除 / 保留图像 /
z-order / 光栅叠加、§9 降级报告。字形与系统字体回退用注入 provider 及真实字体
文件字节验证，不依赖某一平台已安装的具体字体。
"""
import struct
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

import pymupdf as fitz
from translate import extract, geometry, reflow
from translate.chunk import BlockTranslation


def block(rect, text, size=12.0, font="helv", page_no=1):
    """构造一个规范坐标块，避免依赖抽取器即可独立测试回填。"""
    return extract.TextBlock(
        page_no=page_no,
        rect=geometry.RectNorm(rect[0], rect[1], rect[2], rect[3], 0),
        text=text, size=size, font=font)


def set_fstype(data, value):
    """把字体的 OS/2 fsType 改写为 `value`（仅用于构造受限许可样例）。"""
    num_tables = struct.unpack(">H", data[4:6])[0]
    for index in range(num_tables):
        offset = 12 + index * 16
        if data[offset:offset + 4] == b"OS/2":
            table = struct.unpack(">I", data[offset + 8:offset + 12])[0]
            data[table + 8:table + 10] = struct.pack(">H", value)
            return True
    return False


class ReflowTests(unittest.TestCase):
    def make_page(self, width=300, height=400, rotation=0):
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=width, height=height)
        if rotation:
            page.set_rotation(rotation)
        return page

    def png(self):
        pixmap = fitz.Pixmap(fitz.csRGB, fitz.IRect(0, 0, 12, 12))
        pixmap.clear_with(180)
        return pixmap.tobytes("png")

    def page_report(self, report, page_no=1):
        return next((item for item in report.to_dict()["pages"]
                     if item["page"] == page_no), None)

    def reasons(self, report, page_no=1):
        item = self.page_report(report, page_no)
        return [] if item is None else item["reasons"]

    def searchable(self, report, page_no=1):
        item = self.page_report(report, page_no)
        return True if item is None else item["searchable"]

    # --- §4 溢出策略 --------------------------------------------------------

    def test_s0_fits_at_original_size_without_degradation(self):
        """S0：原块框 + 原字号可容纳；原文被真实移除，译文可搜索，无降级。"""
        page = self.make_page()
        page.insert_text((40, 60), "Original English sentence here", fontsize=12)
        report = reflow.DegradationReport(task_id="t")
        reflow.backfill_page(
            page, [block((35, 45, 260, 75), "Original English sentence here")],
            {0: "你好，世界"}, report)
        self.assertNotIn("Original English", page.get_text())
        self.assertTrue(page.search_for("你好"))
        self.assertEqual(self.reasons(report), [])
        self.assertTrue(self.searchable(report))
        # 不得以黑框/redaction 注释冒充删除。
        self.assertEqual(len(list(page.annots() or [])), 0)
        self.assertEqual(len(page.get_drawings()), 0)

    def test_s1_shrinks_font_before_overflow(self):
        """S1：S0 放不下 → 缩小字号（步进 0.05，下限 0.70），不直接溢出/裁切。"""
        page = self.make_page()
        translated = "The translated version is considerably longer than the source text"
        page.insert_text((40, 60), "SOURCE", fontsize=14)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 45, 200, 90), "SOURCE", size=14)],
                             {0: translated}, report)
        self.assertIn("font_shrink", self.reasons(report))
        self.assertNotIn("overflow_margin", self.reasons(report))
        self.assertNotIn("clip", self.reasons(report))
        self.assertNotIn("SOURCE", page.get_text())
        self.assertIn(reflow._normalize(translated)[:8], reflow._normalize(page.get_text()))

    def test_s2_overflows_to_margin_after_shrink(self):
        """S2：缩到 0.70 仍放不下 → 溢出到下方页边留白（先下），记 overflow_margin。"""
        page = self.make_page()
        translated = ("The translated version is considerably longer than the "
                      "source text and keeps going")
        page.insert_text((40, 55), "SOURCE", fontsize=13)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 40, 150, 60), "SOURCE", size=13)],
                             {0: translated}, report)
        self.assertIn("overflow_margin", self.reasons(report))
        self.assertNotIn("clip", self.reasons(report))
        self.assertNotIn("SOURCE", page.get_text())

    def test_s3_clips_as_last_resort(self):
        """S3：扩展后仍放不下 → 裁切，写入可容纳部分并记 clip。"""
        page = self.make_page()
        page.insert_text((40, 55), "SOURCE", fontsize=13)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 40, 150, 60), "SOURCE", size=13)],
                             {0: "overflow " * 400}, report)
        self.assertIn("clip", self.reasons(report))
        self.assertTrue(page.get_text().strip())  # 部分译文已真实写入
        self.assertNotIn("SOURCE", page.get_text())

    # --- §6 真实移除 / 图像 / z-order / 光栅 --------------------------------

    def test_redaction_preserves_images_and_text_lands_on_top(self):
        """redaction 移除原文但保留图像；译文以 overlay 写在图像之上。"""
        page = self.make_page()
        page.insert_image(fitz.Rect(35, 40, 260, 80), stream=self.png())
        page.insert_text((40, 60), "SECRET ORIGINAL", fontsize=12)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 45, 260, 80), "SECRET ORIGINAL")],
                             {0: "译文覆盖图像"}, report)
        self.assertNotIn("SECRET", page.get_text())
        self.assertEqual(len(page.get_images()), 1)
        self.assertTrue(page.search_for("译文"))

    def test_raster_block_overlays_without_redaction(self):
        """扫描块（font='ocr'）不做文本移除，只叠加译文并记 raster_overlay。"""
        page = self.make_page()
        page.insert_image(fitz.Rect(40, 40, 140, 140), stream=self.png())
        report = reflow.DegradationReport()
        reflow.backfill_page(page,
                             [block((40, 40, 140, 140), "scan text", font="ocr")],
                             {0: "扫描译文"}, report)
        self.assertIn("raster_overlay", self.reasons(report))
        self.assertFalse(self.searchable(report))
        self.assertEqual(len(page.get_images()), 1)
        self.assertEqual(len(list(page.annots() or [])), 0)
        self.assertTrue(page.search_for("扫描"))

    # --- §3 译文映射 --------------------------------------------------------

    def test_translation_list_variants_and_degraded_kept(self):
        """支持 dict 与 BlockTranslation 列表；degraded 段保留原文。"""
        page = self.make_page()
        page.insert_text((40, 60), "ONE", fontsize=12)
        page.insert_text((40, 120), "TWO", fontsize=12)
        blocks = [block((35, 45, 260, 80), "ONE"), block((35, 105, 260, 140), "TWO")]
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, [
            BlockTranslation(index=0, text="第一"),
            BlockTranslation(index=1, text="第二", degraded=True),
        ], report)
        text = page.get_text()
        self.assertIn("第一", text)
        self.assertIn("TWO", text)   # degraded → 原文保留，不硬填译文
        self.assertNotIn("ONE", text)

    def test_missing_translation_keeps_original(self):
        page = self.make_page()
        page.insert_text((40, 60), "KEEP ORIGINAL", fontsize=12)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 45, 260, 80), "KEEP ORIGINAL")],
                             {}, report)
        self.assertIn("KEEP ORIGINAL", page.get_text())
        self.assertEqual(self.reasons(report), [])

    # --- §5 字体策略 --------------------------------------------------------

    def test_cjk_output_is_searchable_after_save(self):
        """译文用带 ToUnicode 的真文本写入：保存重开后仍可搜索（§8）。"""
        page = self.make_page()
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 45, 260, 80), "source")],
                             {0: "中文可搜索"}, report)
        reopened = fitz.open(stream=page.parent.tobytes(), filetype="pdf")
        self.addCleanup(reopened.close)
        self.assertTrue(reopened[0].search_for("中文可搜索"))

    def test_resolve_font_picks_script_natural_font(self):
        cjk = reflow.resolve_font("你好", base_font="Helvetica")
        self.assertEqual(cjk.missing, ())
        self.assertFalse(cjk.fallback)  # CJK 译文选 CJK 内置字体，不算降级
        latin = reflow.resolve_font("hello world", base_font="Helvetica")
        self.assertEqual(latin.name, "helv")
        self.assertFalse(latin.fallback)

    def test_glyph_missing_uses_visible_placeholder(self):
        """候选字体缺字形 → 可见占位符 + glyph_missing，不静默 tofu。"""
        page = self.make_page()
        provider = lambda text, base: reflow.FontChoice(font=fitz.Font("helv"), name="helv")
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 45, 200, 90), "src")], {0: "你好"},
                             report, font_provider=provider)
        self.assertIn("glyph_missing", self.reasons(report))
        self.assertIn("□", page.get_text())

    def test_font_fallback_reason_from_provider(self):
        page = self.make_page()
        provider = lambda text, base: reflow.FontChoice(
            font=fitz.Font("china-s"), name="system-cjk", fallback=True)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, [block((35, 45, 200, 90), "src")], {0: "回退字体"},
                             report, font_provider=provider)
        self.assertIn("font_fallback", self.reasons(report))

    def test_fstype_restricted_bit_and_parser(self):
        self.assertTrue(reflow.is_embeddable(0))
        self.assertTrue(reflow.is_embeddable(None))
        self.assertFalse(reflow.is_embeddable(0x0002))  # Restricted License
        self.assertIsNone(reflow.font_fs_type(b"not-a-font"))
        self.assertIsNone(reflow.font_fs_type(b"ttcf" + b"\x00" * 20))  # 集合不支持
        fs_type = reflow.font_fs_type(fitz.Font("china-s").buffer)
        self.assertIsNotNone(fs_type)
        self.assertTrue(reflow.is_embeddable(fs_type))

    def test_scan_system_font_skips_restricted_license(self):
        """系统回退只采用可解析且非受限许可的字体（§5.3）。"""
        data = bytearray(fitz.Font("china-s").buffer)
        with tempfile.TemporaryDirectory() as tmp:
            good = Path(tmp) / "good.ttf"
            good.write_bytes(bytes(data))
            choice = reflow._scan_system_font(("中",), system_files=[good])
            self.assertIsNotNone(choice)
            self.assertTrue(choice.fallback)
            self.assertEqual(choice.missing, ())

            restricted = Path(tmp) / "restricted.ttf"
            self.assertTrue(set_fstype(data, 0x0002))
            restricted.write_bytes(bytes(data))
            self.assertEqual(reflow.font_fs_type(bytes(data)), 0x0002)
            self.assertIsNone(reflow._scan_system_font(("中",), system_files=[restricted]))

    # --- §1 旋转 / §9 报告 --------------------------------------------------

    def test_rotated_page_backfill_restores_rotation(self):
        page = self.make_page(rotation=90)
        page.insert_text((40, 60), "Rotated source", fontsize=12)
        blocks = extract.extract_page(page)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "旋转译文"}, report)
        self.assertEqual(page.rotation, 90)
        self.assertNotIn("Rotated", page.get_text())
        self.assertTrue(page.search_for("旋转"))

    def test_report_schema_and_unknown_reason(self):
        report = reflow.DegradationReport(task_id="job-1")
        report.add(3, "font_shrink")
        report.add(3, "table_fail")
        report.add(7, "raster_overlay")
        data = report.to_dict()
        self.assertEqual(data["task_id"], "job-1")
        self.assertEqual(data["summary"], {
            "degraded_pages": 2, "unsearchable_pages": 1, "total_pages": 2})
        page7 = next(item for item in data["pages"] if item["page"] == 7)
        self.assertFalse(page7["searchable"])
        self.assertEqual(page7["reasons"], ["raster_overlay"])
        with self.assertRaises(ValueError):
            report.add(1, "not-a-reason")


if __name__ == "__main__":
    unittest.main()
