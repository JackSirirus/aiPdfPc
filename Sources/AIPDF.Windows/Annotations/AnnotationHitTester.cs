// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 批注悬停命中 + 改色预设色板（FR-13 / H-9）。纯函数，xunit 覆盖。
using System.Collections.Generic;

namespace AIPDF.Windows.Annotations;

/// <summary>命中测试使用页内 DIP 坐标（与 <see cref="AnnotationOverlay"/> 同口径）。</summary>
public static class AnnotationHitTester
{
    /// <summary>命中容差（DIP）：细高亮线也能被鼠标稳定命中。</summary>
    public const double DefaultPad = 2.0;

    /// <summary>
    /// 返回 (x, y) 命中的批注 Id；多条重叠时取最后一条（后绘制者在上）。未命中返回 null。
    /// </summary>
    public static string? HitTest(IReadOnlyList<AnnotationOverlay> overlays, double x, double y, double pad = DefaultPad)
    {
        if (overlays is null || overlays.Count == 0) return null;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            var o = overlays[i];
            if (x >= o.X - pad && x <= o.X + o.Width + pad && y >= o.Y - pad && y <= o.Y + o.Height + pad)
            {
                return o.Id;
            }
        }
        return null;
    }
}

/// <summary>改色预设色（FR-13：至少含默认黄）。</summary>
/// <param name="Hex">#RRGGBB。</param>
/// <param name="Label">色板 tooltip / 无障碍名。</param>
public readonly record struct PaletteColor(string Hex, string Label);

/// <summary>
/// 批注改色预设色板。默认黄（<see cref="AnnotationRecord.DefaultHighlightColor"/>）置于首位。
/// </summary>
public static class AnnotationPalette
{
    /// <summary>预设色板（顺序即显示顺序）。</summary>
    public static readonly IReadOnlyList<PaletteColor> Colors =
    [
        new(AnnotationRecord.DefaultHighlightColor, "黄色"),
        new("#A6E22E", "绿色"),
        new("#5AC8FA", "蓝色"),
        new("#FF9EC4", "粉色"),
    ];

    /// <summary>是否为受支持的预设色（判定用，大小写敏感 #RRGGBB）。</summary>
    public static bool IsPreset(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return false;
        foreach (var c in Colors)
        {
            if (string.Equals(c.Hex, hex, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
