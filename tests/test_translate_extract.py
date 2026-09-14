# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""文本抽取与阅读序重建测试；只用内存合成 PDF，不读写任何用户文档。

覆盖规格 §2：去连字、单/双栏与混排的阅读序、段落重建；以及 §9 的低置信标记
（extraction_low_conf）与零块页覆盖（empty_page）。扫描页 OCR 用注入框验证，
不依赖本机 Vision 服务。
"""
import sys
import unittest
import unittest.mock
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

import pymupdf as fitz
from translate import extract


def _text_block(doc, blocks, prefix):
    return next((b for b in blocks if b.text.lstrip().startswith(prefix)), None)


class ExtractTests(unittest.TestCase):
    def make_doc(self):
        doc = fitz.open()
        self.addCleanup(doc.close)
        return doc

    def make_page(self, doc, width=400, height=600):
        return doc.new_page(width=width, height=height)

    def column_page(self, doc, title=None):
        """合成双栏页；两栏内容足够高，列缝可被 §2.2 判定为列间空白带。"""
        page = self.make_page(doc)
        if title:
            page.insert_textbox(fitz.Rect(30, 30, 370, 55), title, fontsize=14)
        top = 65 if title else 60
        page.insert_textbox(fitz.Rect(30, top, 170, 590), "alpha " * 150, fontsize=10)
        page.insert_textbox(fitz.Rect(230, top, 370, 590), "beta " * 150, fontsize=10)
        return page

    def positions(self, blocks, prefix):
        return [i for i, block in enumerate(blocks)
                if block.text.lstrip().startswith(prefix)]

    # --- 单栏 -------------------------------------------------------------

    def test_single_column_reading_order(self):
        doc = self.make_doc()
        page = self.make_page(doc)
        page.insert_text((50, 100), "First line here")
        page.insert_text((50, 114), "continues second line")
        page.insert_text((50, 300), "Separate far away")
        blocks = extract.extract_page(page)
        self.assertEqual([b.page_no for b in blocks], [1, 1])
        self.assertIn("First line here continues second line", blocks[0].text)
        self.assertEqual(blocks[1].text, "Separate far away")

    def test_paragraph_merge_and_split(self):
        """同列且垂直间距小 → 合并；间距大 → 另起段落（§2.3）。"""
        doc = self.make_doc()
        page = self.make_page(doc)
        for y in (100, 114, 128):
            page.insert_text((50, y), "wrapped paragraph content")
        page.insert_text((50, 320), "next block far below")
        blocks = extract.extract_page(page)
        self.assertEqual(len(blocks), 2)
        self.assertEqual(blocks[0].text.count("wrapped"), 3)
        self.assertIn("next block far below", blocks[1].text)

    # --- 双栏与混排 --------------------------------------------------------

    def test_two_column_reading_order(self):
        """双栏按「左栏全部 → 右栏全部」排序，不按整页 y 交错。"""
        doc = self.make_doc()
        blocks = extract.extract_page(self.column_page(doc))
        left, right = self.positions(blocks, "alpha"), self.positions(blocks, "beta")
        self.assertTrue(left and right, f"未分离出两栏：{[b.text[:12] for b in blocks]}")
        self.assertLess(max(left), min(right))

    def test_mixed_layout_title_before_columns(self):
        """通栏标题在列之前，且列序仍为左→右。"""
        doc = self.make_doc()
        blocks = extract.extract_page(self.column_page(doc, title="TITLEPAGE headline"))
        title = self.positions(blocks, "TITLEPAGE")
        left, right = self.positions(blocks, "alpha"), self.positions(blocks, "beta")
        self.assertTrue(title and left and right)
        self.assertLess(max(title), min(left))
        self.assertLess(max(left), min(right))

    def test_single_column_never_splits(self):
        """窄页单栏（列缝不足 24pt）不得被误判为多栏。"""
        doc = self.make_doc()
        page = self.make_page(doc, width=220, height=400)
        page.insert_text((30, 50), "single column text")
        page.insert_text((30, 80), "more text same column")
        page.insert_text((30, 320), "bottom line")
        blocks = extract.extract_page(page)
        self.assertTrue(blocks)
        # 被误判为两栏时，底部单行会因 column 不同而排到中间行之前。
        self.assertEqual(blocks[-1].text, "bottom line")

    # --- 去连字（§2.1） ---------------------------------------------------

    def test_dehyphenation_joins_and_preserves(self):
        doc = self.make_doc()
        page = self.make_page(doc)
        page.insert_text((50, 100), "inter-")
        page.insert_text((50, 114), "national")
        page.insert_text((50, 200), "well-")
        page.insert_text((50, 214), "Known")
        text = " ".join(b.text for b in extract.extract_page(page))
        self.assertIn("international", text)
        self.assertNotIn("inter-", text)
        self.assertIn("well-", text)          # 下一行首字母大写 → 保留连字符
        self.assertNotIn("wellKnown", text)

    def test_dehyphenation_can_be_disabled(self):
        doc = self.make_doc()
        page = self.make_page(doc)
        page.insert_text((50, 100), "inter-")
        page.insert_text((50, 114), "national")
        text = " ".join(b.text for b in extract.extract_page(page, dehyphenate=False))
        self.assertIn("inter- national", text)
        self.assertNotIn("international", text)

    # --- 坐标归一化 --------------------------------------------------------

    def test_rotated_page_coords_are_normalized(self):
        doc = self.make_doc()
        page = self.make_page(doc)
        page.insert_text((50, 100), "Rotated text", fontsize=12)
        page.set_rotation(90)
        blocks = extract.extract_page(page)
        self.assertEqual(len(blocks), 1)
        self.assertEqual(blocks[0].rect.rotation_deg, 90)
        crop = page.cropbox
        self.assertGreaterEqual(blocks[0].rect.x0, 0)
        self.assertGreaterEqual(blocks[0].rect.y0, 0)
        self.assertLessEqual(blocks[0].rect.x1, crop.width)
        self.assertLessEqual(blocks[0].rect.y1, crop.height)
        # dict bbox 本就未旋转：可见坐标 = bbox * rotation_matrix，二者应一致。
        raw = fitz.Rect(page.get_text("dict")["blocks"][0]["bbox"])
        expected = extract.geometry.rect_normalize(page, raw * page.rotation_matrix)
        self.assertAlmostEqual(blocks[0].rect.x0, expected.x0, delta=0.5)
        self.assertAlmostEqual(blocks[0].rect.y0, expected.y0, delta=0.5)

    def test_cropped_rotated_page_within_crop(self):
        doc = self.make_doc()
        page = doc.new_page(width=595, height=842)
        page.insert_text((120, 260), "inside crop", fontsize=12)
        page.set_cropbox(fitz.Rect(50, 100, 545, 600))
        page.set_rotation(270)
        blocks = extract.extract_page(page)
        self.assertTrue(blocks)
        crop = page.cropbox
        for block in blocks:
            self.assertGreaterEqual(block.rect.x0, 0)
            self.assertGreaterEqual(block.rect.y0, 0)
            self.assertLessEqual(block.rect.x1, crop.width + 0.5)
            self.assertLessEqual(block.rect.y1, crop.height + 0.5)
            self.assertEqual(block.rect.rotation_deg, 270)

    # --- 扫描页 OCR 分支 ---------------------------------------------------

    def test_scan_page_uses_injected_ocr_boxes(self):
        doc = self.make_doc()
        page = self.make_page(doc, width=400, height=600)
        self.assertEqual(page.get_text().strip(), "")
        boxes = [{"text": "SCANNED TEXT", "x": 0.1, "y": 0.1, "w": 0.5, "h": 0.05}]
        blocks = extract.extract_page(page, ocr_boxes=boxes)
        self.assertEqual(len(blocks), 1)
        self.assertEqual(blocks[0].text, "SCANNED TEXT")
        self.assertEqual(blocks[0].page_no, 1)
        self.assertEqual(blocks[0].font, "ocr")
        self.assertAlmostEqual(blocks[0].rect.x0, 40, delta=0.5)
        self.assertAlmostEqual(blocks[0].rect.y0, 60, delta=0.5)

    def test_text_layer_disables_ocr(self):
        doc = self.make_doc()
        page = self.make_page(doc)
        page.insert_text((50, 100), "Real text layer")
        sentinel = [{"text": "SHOULD_NOT_APPEAR", "x": 0.1, "y": 0.1, "w": 0.5, "h": 0.05}]
        blocks = extract.extract_page(page, ocr_boxes=sentinel)
        self.assertIn("Real text layer", " ".join(b.text for b in blocks))
        self.assertNotIn("SHOULD_NOT_APPEAR", " ".join(b.text for b in blocks))

    # --- 低置信（§9 extraction_low_conf） ---------------------------------

    def test_low_confidence_helpers(self):
        self.assertFalse(extract.is_low_confidence("normal readable text"))
        self.assertTrue(extract.is_low_confidence("\ue000\ue001\ue002ab"))
        self.assertTrue(extract.is_low_confidence("(cid:123)(cid:456)"))
        self.assertEqual(extract.suspicious_ratio(""), 0.0)
        block = extract.TextBlock(page_no=1, rect=extract.geometry.RectNorm(0, 0, 1, 1, 0),
                                  text="\ue000\ue001\ue002ab", confidence=0.25)
        self.assertTrue(block.low_confidence)

    # --- 每页覆盖报告（§9.2） ---------------------------------------------

    def test_document_report_flags_empty_page(self):
        doc = self.make_doc()
        self.make_page(doc)  # 无文本层，注入空 OCR 框避免真实识别
        result = extract.extract_document(doc, ocr_boxes_by_page={0: []})
        self.assertEqual(result.blocks, [])
        self.assertEqual(result.report["pages"], [
            {"page": 1, "degraded": True, "reasons": ["empty_page"]}])
        self.assertEqual(result.report["summary"], {"degraded_pages": 1, "total_pages": 1})

    def test_document_report_flags_low_conf_page(self):
        doc = self.make_doc()
        page = self.make_page(doc)
        boxes = [{"text": "\ue000\ue001\ue002\ue003", "x": 0.1, "y": 0.1, "w": 0.5, "h": 0.05}]
        result = extract.extract_document(doc, ocr_boxes_by_page={page.number: boxes})
        self.assertEqual(result.report["pages"][0]["reasons"], ["extraction_low_conf"])
        self.assertTrue(result.blocks[0].low_confidence)

    def test_document_aggregates_pages_in_order(self):
        doc = self.make_doc()
        first = self.make_page(doc)
        first.insert_text((50, 100), "PAGE ONE")
        second = self.make_page(doc)
        second.insert_text((50, 100), "PAGE TWO")
        result = extract.extract_document(doc)
        self.assertEqual([b.page_no for b in result.blocks], [1, 2])
        self.assertEqual(result.report["summary"]["total_pages"], 2)
        self.assertEqual(result.report["summary"]["degraded_pages"], 0)
        self.assertTrue(all(not item["degraded"] for item in result.report["pages"]))


class BBoxGuardTests(unittest.TestCase):
    """F5：dict bbox 规范坐标假设的运行时守卫与回退。"""

    def make_doc(self):
        doc = fitz.open()
        self.addCleanup(doc.close)
        return doc

    def test_guard_holds_on_real_rotated_page(self):
        """真实旋转页上回环恒等（仅浮点噪声）→ 不触发回退。"""
        doc = self.make_doc()
        page = doc.new_page(width=300, height=400)
        page.insert_text((50, 100), "Hello world", fontsize=12)
        page.set_rotation(90)
        raw = fitz.Rect(page.get_text("dict")["blocks"][0]["bbox"])
        self.assertFalse(extract._bbox_needs_normalization(page, raw))

    def test_guard_detects_inconsistent_matrices(self):
        """矩阵回环明显偏离（模拟另类构建返回可见坐标）→ 触发回退。"""
        class Fake:
            rotation_matrix = fitz.Matrix(2, 0, 0, 2, 0, 0)   # 非恒等缩放
            derotation_matrix = fitz.Matrix(1, 0, 0, 1, 0, 0)

        self.assertTrue(extract._bbox_needs_normalization(Fake(), fitz.Rect(0, 0, 10, 10)))
        ok = type("Ok", (), {"rotation_matrix": fitz.Matrix(1, 0, 0, 1, 0, 0),
                             "derotation_matrix": fitz.Matrix(1, 0, 0, 1, 0, 0)})()
        self.assertFalse(extract._bbox_needs_normalization(ok, fitz.Rect(0, 0, 10, 10)))

    def test_fallback_invokes_rect_normalize(self):
        """守卫判为不一致时，`_text_blocks_from_dict` 改用 rect_normalize 归一化。"""
        doc = self.make_doc()
        page = doc.new_page(width=300, height=400)
        page.insert_text((50, 100), "Hello world", fontsize=12)
        page.set_rotation(90)
        with unittest.mock.patch.object(extract, "_bbox_needs_normalization",
                                        return_value=True), \
                unittest.mock.patch.object(extract.geometry, "rect_normalize",
                                           wraps=extract.geometry.rect_normalize) as normalize:
            blocks = extract._text_blocks_from_dict(page, True)
        self.assertTrue(blocks)
        normalize.assert_called_once()
        self.assertIs(normalize.call_args.args[0], page)


if __name__ == "__main__":
    unittest.main()
