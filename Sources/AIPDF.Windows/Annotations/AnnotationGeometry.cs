// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 选区几何：多行选区矩形 → 单条批注矩形（T1 模型为单矩形，取并集包围盒）。
// 纯函数，xunit 覆盖。坐标口径与 PdfRect 一致（PDF 点、左上原点）。
using System.Collections.Generic;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Annotations;

/// <summary>选区几何辅助（不新增坐标换算层，仅做矩形合并）。</summary>
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
}
