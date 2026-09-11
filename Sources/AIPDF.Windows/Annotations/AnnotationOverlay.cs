// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 批注叠加层投影（T1）：批注记录（PDF 点）→ 页内 DIP 叠加条目（供 ReaderView 的独立 Canvas 层消费）。
//
// 换算复用 Views/Reader/PageLayoutCalculator.ToPageDip（与搜索高亮同一函数），不新增坐标换算层（PRD 4.2.2）。
// 生成的 Brush 已 Freeze，可跨线程/缓存复用；半透明填充保证下方文字可见（对照 Brush.SearchHighlight 的 0x66 alpha 口径）。
using System.Globalization;
using System.Windows.Media;
using AIPDF.Windows.Views.Reader;

namespace AIPDF.Windows.Annotations;

/// <summary>
/// 单条批注的页内显示叠加（DIP，相对页左上角）。XAML 以 Canvas 绝对定位消费，独立 IsHitTestVisible=False 层。
/// </summary>
public readonly record struct AnnotationOverlay(
    string Id,
    AnnotationKind Kind,
    string? Note,
    double X,
    double Y,
    double Width,
    double Height,
    Brush Fill,
    Brush Border)
{
    /// <summary>高亮填充 alpha（40%），叠加在渲染页上仍清晰可辨且不遮字符。</summary>
    private const byte FillAlpha = 0x66;

    /// <summary>高亮边框 alpha（80%）。</summary>
    private const byte BorderAlpha = 0xCC;

    /// <summary>边框相对填充色的压暗系数（在浅色页上可辨）。</summary>
    private const double BorderDarken = 0.72;

    /// <summary>
    /// 记录（PDF 点）→ 叠加条目（DIP）。缩放变更时需按新显示宽重算（调用方在 RebuildAnnotations）。
    /// 退化输入（页宽/显示宽 ≤ 0）返回零矩形但保留颜色/类型（不绘制）。
    /// </summary>
    public static AnnotationOverlay FromRecord(AnnotationRecord record, double pageWidthPt, double displayWidthDip)
    {
        ArgumentNullException.ThrowIfNull(record);
        var dip = PageLayoutCalculator.ToPageDip(record.Rect, pageWidthPt, displayWidthDip);
        var (r, g, b) = ParseRgb(record.Color);
        var fill = Freeze(Color.FromArgb(FillAlpha, r, g, b));
        var border = Freeze(Color.FromArgb(BorderAlpha, Darken(r), Darken(g), Darken(b)));
        return new AnnotationOverlay(record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height, fill, border);
    }

    private static byte Darken(byte channel) => (byte)Math.Round(channel * BorderDarken, MidpointRounding.AwayFromZero);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>解析 #RRGGBB；格式非法时回退默认高亮色（不抛，UI 叠加层不应因脏数据崩溃）。</summary>
    private static (byte R, byte G, byte B) ParseRgb(string? hex)
    {
        var fallback = (R: (byte)0xFF, G: (byte)0xFF, B: (byte)0x00);
        if (string.IsNullOrEmpty(hex)) return fallback;

        var text = hex.StartsWith('#') ? hex[1..] : hex;
        if (text.Length != 6) return fallback;
        if (!byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return fallback;
        }
        return (r, g, b);
    }
}
