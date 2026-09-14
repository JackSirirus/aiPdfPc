# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""页面坐标归一化（翻译引擎几何基础）。

翻译引擎的抽取（T3）、回填排版（T8）与输出（T9）统一在同一个规范坐标系里工作：
**未旋转、相对 CropBox、左上原点、单位 pt**。页面可能带 /Rotate（0/90/180/270），
CropBox 也可能与 MediaBox 不一致。PyMuPDF 的文本与绘制坐标本就是「未旋转 + 相对
CropBox」，而可见页 page.rect 是旋转后的；本模块显式、可逆地在这两个坐标系之间
转换：块坐标归一化一次，回填时再映射回可见页面坐标。规则见《翻译-回填排版规格.md》§1。
"""
from __future__ import annotations

from dataclasses import dataclass

import pymupdf as fitz


# /Rotate 在有效 PDF 中只取这四档，其余一律视为无效。
_VALID_ROTATIONS = (0, 90, 180, 270)


@dataclass(frozen=True)
class RectNorm:
    """规范坐标系内的矩形：未旋转、相对 CropBox、左上原点，单位 pt。

    rotation_deg 只在还原到可见页面坐标时使用；布局计算只看前四个坐标。
    """

    x0: float
    y0: float
    x1: float
    y1: float
    rotation_deg: int

    @property
    def width(self):
        return self.x1 - self.x0

    @property
    def height(self):
        return self.y1 - self.y0

    @property
    def rect(self):
        """返回 fitz.Rect 副本，便于与 PyMuPDF 接口对接。"""
        return fitz.Rect(self.x0, self.y0, self.x1, self.y1)


def page_rotation(page):
    """返回归一化后的 /Rotate 值（0/90/180/270）。"""
    rotation = int(page.rotation or 0) % 360
    if rotation not in _VALID_ROTATIONS:
        raise ValueError(f"不支持的页面旋转角度：{page.rotation}")
    return rotation


def _visible_matrix(rotation, width, height):
    """规范坐标 -> 可见页面坐标的矩阵（等价 page.rotation_matrix）。

    由纯旋转矩阵加平移组成，平移量取决于 CropBox 宽高，从而显式纳入 CropBox，
    不假设其原点为 (0,0)，也不硬编码任何一种旋转顺序。
    """
    base = fitz.Matrix(rotation)
    moved = fitz.Rect(0, 0, width, height) * base
    moved.normalize()
    return base * fitz.Matrix(1, 0, 0, 1, -moved.x0, -moved.y0)


def _resolve(page, rect):
    """兼容 rect_normalize(page)、(page, rect)、(rect, page) 三种调用。"""
    if not isinstance(page, fitz.Page):
        page, rect = rect, page
    if rect is not None:
        rect = fitz.Rect(rect)
    return page, rect


def rect_normalize(page, rect=None) -> RectNorm:
    """把可见页面坐标系的矩形归一化到规范坐标系。

    只传 page 时归一化整页可见矩形 page.rect；也可显式传入可见坐标下的 rect。
    PyMuPDF 的 derotation_matrix 输出原点即 CropBox 左上角，因此结果天然相对
    CropBox，不会落在 MediaBox 原点上；CropBox 偏移由该矩阵一并纳入。
    """
    page, rect = _resolve(page, rect)
    source = page.rect if rect is None else rect
    normalized = source * page.derotation_matrix
    normalized.normalize()
    return RectNorm(normalized.x0, normalized.y0, normalized.x1, normalized.y1,
                    page_rotation(page))


def to_page_rect(norm, page) -> fitz.Rect:
    """把规范坐标矩形还原为可见页面坐标（rect_normalize 的逆变换）。

    使用 norm 保存的 rotation_deg，而不是 page 当前旋转，因此页面在归一化之后被
    临时归零旋转（见规格 §1 第 2 步）时仍能正确回填。
    """
    crop = page.cropbox
    restored = norm.rect * _visible_matrix(norm.rotation_deg, crop.width, crop.height)
    restored.normalize()
    return restored
