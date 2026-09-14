# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""页面坐标归一化单元测试；只用内存合成页面，不读写任何用户文档。"""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

import pymupdf as fitz
from translate import geometry

# 允许的最大回环误差（pt），与规格 §1 的 0.5pt 容差一致。
TOLERANCE = 0.5

# (媒体尺寸, CropBox 或 None, 旋转)，覆盖恒等 / 纯旋转 / 偏移裁剪 / 居中裁剪 / 横向裁剪。
CONFIGS = [
    ((400, 600), None, 0),
    ((400, 600), None, 90),
    ((400, 600), None, 180),
    ((400, 600), None, 270),
    ((595, 842), (50, 100, 545, 600), 0),
    ((595, 842), (50, 100, 545, 600), 90),
    ((595, 842), (50, 100, 545, 600), 180),
    ((595, 842), (50, 100, 545, 600), 270),
    ((595, 842), (100, 200, 500, 700), 0),
    ((595, 842), (100, 200, 500, 700), 90),
    ((595, 842), (100, 200, 500, 700), 180),
    ((595, 842), (100, 200, 500, 700), 270),
    ((595, 842), (0, 0, 595, 300), 0),
    ((595, 842), (0, 0, 595, 300), 90),
    ((595, 842), (0, 0, 595, 300), 180),
    ((595, 842), (0, 0, 595, 300), 270),
]


class GeometryTests(unittest.TestCase):
    def make_page(self, media, crop, rotation):
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=media[0], height=media[1])
        if crop is not None:
            page.set_cropbox(fitz.Rect(crop))
        page.set_rotation(rotation)
        return page

    def assert_rect_close(self, got, want, msg=""):
        for name in ("x0", "y0", "x1", "y1"):
            self.assertAlmostEqual(getattr(got, name), getattr(want, name),
                                   delta=TOLERANCE, msg=f"{msg} 字段 {name}")

    def visible_rects(self, page):
        w, h = page.rect.width, page.rect.height
        return [
            fitz.Rect(page.rect),
            fitz.Rect(w * 0.1, h * 0.1, w * 0.6, h * 0.5),
            fitz.Rect(0, 0, w * 0.2, h * 0.2),
            fitz.Rect(w * 0.4, h * 0.6, w * 0.9, h * 0.95),
        ]

    # --- 单页坐标 ---------------------------------------------------------

    def test_identity(self):
        """无旋转、无裁剪时归一化即恒等。"""
        page = self.make_page((400, 600), None, 0)
        norm = geometry.rect_normalize(page)
        self.assertAlmostEqual(norm.x0, 0); self.assertAlmostEqual(norm.y0, 0)
        self.assertAlmostEqual(norm.x1, 400); self.assertAlmostEqual(norm.y1, 600)
        self.assertEqual(norm.rotation_deg, 0)
        r = fitz.Rect(10, 20, 60, 80)
        self.assertEqual(geometry.rect_normalize(page, r), geometry.RectNorm(10, 20, 60, 80, 0))

    def test_rotation_recorded(self):
        """纯旋转：整页归一化后仍是 CropBox 宽高，rotation_deg 被记录。"""
        for rotation in (0, 90, 180, 270):
            with self.subTest(rotation=rotation):
                page = self.make_page((400, 600), None, rotation)
                norm = geometry.rect_normalize(page)
                self.assertAlmostEqual(norm.x0, 0); self.assertAlmostEqual(norm.y0, 0)
                self.assertAlmostEqual(norm.x1, 400); self.assertAlmostEqual(norm.y1, 600)
                self.assertEqual(norm.rotation_deg, rotation)
                self.assertIsInstance(norm, geometry.RectNorm)

    def test_crop_offset_is_crop_relative(self):
        """CropBox 原点非 (0,0) 时结果相对 CropBox，绝不落到 MediaBox 原点。"""
        page = self.make_page((595, 842), (50, 100, 545, 600), 0)
        norm = geometry.rect_normalize(page)
        self.assert_rect_close(norm.rect, fitz.Rect(0, 0, 495, 500), "整页")
        # 可见坐标 (10,20) 归一化后仍是 (10,20)，而不是 (60,120)。
        self.assertEqual(geometry.rect_normalize(page, fitz.Rect(10, 20, 60, 80)),
                         geometry.RectNorm(10, 20, 60, 80, 0))

    def test_crop_offset_all_rotations(self):
        """偏移裁剪 + 四档旋转：整页归一化恒为 (0,0,W_crop,H_crop)。"""
        for rotation in (0, 90, 180, 270):
            with self.subTest(rotation=rotation):
                page = self.make_page((595, 842), (50, 100, 545, 600), rotation)
                norm = geometry.rect_normalize(page)
                self.assert_rect_close(norm.rect, fitz.Rect(0, 0, 495, 500), "整页")
                self.assertEqual(norm.rotation_deg, rotation)

    def test_crop_smaller_than_media(self):
        """CropBox 小于 MediaBox 时只用 CropBox 尺寸，不用 page.width/height。"""
        page = self.make_page((595, 842), (100, 200, 500, 700), 0)
        norm = geometry.rect_normalize(page)
        self.assert_rect_close(norm.rect, fitz.Rect(0, 0, 400, 500), "整页")
        self.assertNotAlmostEqual(norm.width, page.mediabox.width, delta=1)

    def test_landscape_crop(self):
        """CropBox 宽大于高（横向裁剪区）也成立。"""
        page = self.make_page((595, 842), (0, 0, 595, 300), 0)
        norm = geometry.rect_normalize(page)
        self.assert_rect_close(norm.rect, fitz.Rect(0, 0, 595, 300), "整页")

    # --- 回环 -------------------------------------------------------------

    def test_round_trip_all_configs(self):
        """归一化 -> 还原，四角误差均 < 0.5pt。"""
        for media, crop, rotation in CONFIGS:
            with self.subTest(media=media, crop=crop, rotation=rotation):
                page = self.make_page(media, crop, rotation)
                for source in self.visible_rects(page):
                    norm = geometry.rect_normalize(page, source)
                    restored = geometry.to_page_rect(norm, page)
                    self.assert_rect_close(restored, source, f"回环 {rotation}°")

    def test_round_trip_whole_page(self):
        """整页归一化后还原等于 page.rect。"""
        for media, crop, rotation in CONFIGS:
            with self.subTest(crop=crop, rotation=rotation):
                page = self.make_page(media, crop, rotation)
                restored = geometry.to_page_rect(geometry.rect_normalize(page), page)
                self.assert_rect_close(restored, fitz.Rect(page.rect), f"整页 {rotation}°")

    def test_normalized_inside_crop(self):
        """归一化结果必须落在 [0,W_crop]×[0,H_crop] 内，不出现负坐标。"""
        for media, crop, rotation in CONFIGS:
            with self.subTest(crop=crop, rotation=rotation):
                page = self.make_page(media, crop, rotation)
                bounds = fitz.Rect(0, 0, page.cropbox.width, page.cropbox.height)
                norm = geometry.rect_normalize(page)
                self.assert_rect_close(norm.rect, bounds, "边界")
                self.assertGreaterEqual(norm.x0, -TOLERANCE)
                self.assertGreaterEqual(norm.y0, -TOLERANCE)
                for source in self.visible_rects(page):
                    block = geometry.rect_normalize(page, source)
                    self.assertGreaterEqual(block.x0, -TOLERANCE)
                    self.assertGreaterEqual(block.y0, -TOLERANCE)
                    self.assertLessEqual(block.x1, bounds.x1 + TOLERANCE)
                    self.assertLessEqual(block.y1, bounds.y1 + TOLERANCE)

    def test_restore_uses_stored_rotation(self):
        """页面旋转被临时归零后，回填仍按 RectNorm 保存的 rotation_deg 还原。"""
        page = self.make_page((595, 842), (50, 100, 545, 600), 90)
        source = fitz.Rect(page.rect)
        norm = geometry.rect_normalize(page, source)
        page.set_rotation(0)  # 模拟抽取前把旋转归零
        self.assert_rect_close(geometry.to_page_rect(norm, page), source, "归零后回填")

    # --- 调用形态与辅助属性 -------------------------------------------------

    def test_call_orders_agree(self):
        """(page, rect) 与 (rect, page) 两种入参顺序结果一致。"""
        page = self.make_page((595, 842), (50, 100, 545, 600), 90)
        rect = fitz.Rect(10, 20, 60, 80)
        self.assertEqual(geometry.rect_normalize(page, rect),
                         geometry.rect_normalize(rect, page))

    def test_rectnorm_helpers(self):
        norm = geometry.RectNorm(1, 2, 11, 22, 90)
        self.assertEqual(norm.width, 10)
        self.assertEqual(norm.height, 20)
        self.assertEqual(norm.rect, fitz.Rect(1, 2, 11, 22))
        self.assertEqual(norm.rotation_deg, 90)

    def test_invalid_rotation_rejected(self):
        """非四档旋转（PyMuPDF 读取时会归一化，这里用桩验证守卫）被拒绝。"""
        from types import SimpleNamespace
        with self.assertRaises(ValueError):
            geometry.page_rotation(SimpleNamespace(rotation=45))


if __name__ == "__main__":
    unittest.main()
