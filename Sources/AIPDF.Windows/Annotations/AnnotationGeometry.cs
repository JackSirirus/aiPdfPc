// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 选区几何：多行选区矩形 → 单条批注矩形（T1 模型为单矩形，取并集包围盒）。
// V3 T11（C3）扩展：手写/线形状的点、点序列与包围盒几何；仍为纯函数、不新增坐标换算层。
// 坐标口径与 PdfRect 一致（可见旋转页、左上原点、PDF 点）。
using System;
using System.Collections.Generic;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Annotations;

/// <summary>选区/批注几何辅助（不新增坐标换算层，仅做矩形合并与点几何归一）。</summary>
public static class AnnotationGeometry
{
    /// <summary>
    /// 多行选区矩形并集包围盒（T1 <see cref="AnnotationRecord.Rect"/> 为单矩形）。
    /// 空输入返回零矩形；单元素直接返回其规范化结果。
    /// </summary>
    public static PdfRect Union(IReadOnlyList<PdfRect>? rects)
    {
        if (rects is null || rects.Count == 0) return default;

        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var r in rects)
        {
            left = System.Math.Min(left, System.Math.Min(r.Left, r.Right));
            right = System.Math.Max(right, System.Math.Max(r.Left, r.Right));
            top = System.Math.Min(top, System.Math.Min(r.Top, r.Bottom));
            bottom = System.Math.Max(bottom, System.Math.Max(r.Top, r.Bottom));
        }

        return new PdfRect(left, top, right, bottom);
    }

    /// <summary>规范化矩形（左上 ≤ 右下；拖拽方向任意）。</summary>
    public static PdfRect NormalizeRect(PdfRect rect)
        => new(Math.Min(rect.Left, rect.Right), Math.Min(rect.Top, rect.Bottom),
               Math.Max(rect.Left, rect.Right), Math.Max(rect.Top, rect.Bottom));

    /// <summary>点序列的规范化包围盒；空输入返回零矩形。</summary>
    public static PdfRect BoundingBox(IReadOnlyList<PdfPoint>? points)
    {
        if (points is null || points.Count == 0) return default;

        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X);
            right = Math.Max(right, p.X);
            top = Math.Min(top, p.Y);
            bottom = Math.Max(bottom, p.Y);
        }
        return new PdfRect(left, top, right, bottom);
    }

    /// <summary>多段折线的规范化包围盒；空输入返回零矩形。</summary>
    public static PdfRect BoundingBox(IReadOnlyList<IReadOnlyList<PdfPoint>>? strokes)
    {
        var rect = default(PdfRect);
        var has = false;
        if (strokes is null) return rect;

        foreach (var stroke in strokes)
        {
            var b = BoundingBox(stroke);
            if (!has)
            {
                rect = b;
                has = true;
            }
            else
            {
                rect = new PdfRect(
                    Math.Min(rect.Left, b.Left), Math.Min(rect.Top, b.Top),
                    Math.Max(rect.Right, b.Right), Math.Max(rect.Bottom, b.Bottom));
            }
        }
        return rect;
    }

    /// <summary>
    /// 手写笔迹归一化：丢弃 null、空段与不足两点的段（PyMuPDF ink 需有效折线），
    /// 其余复制为独立点数组。全部退化时返回空列表（由 <see cref="AnnotationRecord.CreateInk"/> 抛错）。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PdfPoint>> NormalizeStrokes(
        IReadOnlyList<IReadOnlyList<PdfPoint>>? strokes)
    {
        var result = new List<IReadOnlyList<PdfPoint>>();
        if (strokes is null) return result;
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.Count < 2) continue;
            var copy = new PdfPoint[stroke.Count];
            for (var i = 0; i < stroke.Count; i++) copy[i] = stroke[i];
            result.Add(copy);
        }
        return result;
    }

    /// <summary>点序列内容相等（顺序敏感）；两者均为 null/空视为相等。</summary>
    public static bool PointsEqual(IReadOnlyList<PdfPoint>? a, IReadOnlyList<PdfPoint>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB) return false;
        if (countA == 0) return true;
        for (var i = 0; i < countA; i++)
        {
            if (a![i] != b![i]) return false;
        }
        return true;
    }

    /// <summary>多段折线内容相等（段序与点序均敏感）；两者均为 null/空视为相等。</summary>
    public static bool StrokesEqual(
        IReadOnlyList<IReadOnlyList<PdfPoint>>? a, IReadOnlyList<IReadOnlyList<PdfPoint>>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB) return false;
        for (var i = 0; i < countA; i++)
        {
            if (!PointsEqual(a![i], b![i])) return false;
        }
        return true;
    }
}
