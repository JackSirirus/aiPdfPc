// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 批注叠加层投影（T1）：批注记录（PDF 点）→ 页内 DIP 叠加条目（供 ReaderView 的独立 Canvas 层消费）。
// V3 T11（C3）扩展：手写（Ink，多段折线）与形状（矩形/圆/线/箭头）。
//
// 换算复用 Views/Reader/PageLayoutCalculator.ToPageDip（与搜索高亮同一函数），不新增坐标换算层（PRD 4.2.2）。
// 生成的 Brush 与 Geometry 已 Freeze，可跨线程/缓存复用；半透明填充保证下方文字可见
// （对照 Brush.SearchHighlight 的 0x66 alpha 口径）。手写/形状为不透明描边，避免线条发虚。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Views.Reader;

namespace AIPDF.Windows.Annotations;

/// <summary>
/// 单条批注的页内显示叠加（DIP，相对页左上角）。XAML 以 Canvas 绝对定位消费，独立 IsHitTestVisible=False 层。
/// 高亮/笔记用 <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/> + Fill/Border 画矩形；
/// 手写/形状以 <see cref="Geometry"/>（页局部 DIP，已冻结）配合 <see cref="Border"/> 描边渲染，
/// 矩形外接框仍写 X/Y/Width/Height 供命中测试与弹层锚定。
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
    Brush Border,
    ShapeKind? Shape = null,
    Geometry? Geometry = null,
    double StrokeThickness = 0,
    bool UsesGeometryLayer = false,
    string? Label = null,
    ImageSource? Image = null)
{
    /// <summary>高亮填充 alpha（40%），叠加在渲染页上仍清晰可辨且不遮字符。</summary>
    private const byte FillAlpha = 0x66;

    /// <summary>高亮边框 alpha（80%）。</summary>
    private const byte BorderAlpha = 0xCC;

    /// <summary>手写/形状描边与箭头的 alpha（完全不透明）。</summary>
    private const byte StrokeAlpha = 0xFF;

    /// <summary>形状填充 alpha（0 = 仅描边；与高亮区分，不遮挡页面内容）。</summary>
    private const byte ShapeFillAlpha = 0x00;

    /// <summary>边框相对填充色的压暗系数（在浅色页上可辨）。</summary>
    private const double BorderDarken = 0.72;

    /// <summary>箭头开口张角（弧度，约为 28.6°；与引擎 OPEN_ARROW 语义一致）。</summary>
    private const double ArrowSpread = 0.5;

    /// <summary>
    /// 记录（PDF 点）→ 叠加条目（DIP）。缩放变更时需按新显示宽重算（调用方在 RebuildAnnotations）。
    /// 退化输入（页宽/显示宽 ≤ 0）返回零矩形但保留颜色/类型（不绘制）。
    /// </summary>
    public static AnnotationOverlay FromRecord(AnnotationRecord record, double pageWidthPt, double displayWidthDip)
    {
        ArgumentNullException.ThrowIfNull(record);
        var dip = PageLayoutCalculator.ToPageDip(record.Rect, pageWidthPt, displayWidthDip);
        var (r, g, b) = ParseRgb(record.Color);
        var scale = pageWidthPt > 0 && displayWidthDip > 0 ? displayWidthDip / pageWidthPt : 0;
        var thickness = Math.Max(0.5, record.LineWidth * scale);

        switch (record.Kind)
        {
            case AnnotationKind.Ink:
            {
                // 手写：不透明描边 + 多段折线 Geometry；Fill 透明（仅 Stroke 生效）。
                // 几何相对本条目左上角（条目由 Canvas.Left/Top 定位到 X/Y），避免二次偏移。
                var stroke = Freeze(Color.FromArgb(StrokeAlpha, r, g, b));
                var fill = Freeze(Color.FromArgb(ShapeFillAlpha, r, g, b));
                var bounds = AnnotationGeometry.NormalizeRect(record.Rect);
                return new AnnotationOverlay(
                    record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height,
                    fill, stroke, null, BuildInkGeometry(record.Strokes, scale, bounds.Left, bounds.Top), thickness,
                    UsesGeometryLayer: true);
            }

            case AnnotationKind.Shape:
            {
                var stroke = Freeze(Color.FromArgb(StrokeAlpha, r, g, b));
                var fill = Freeze(Color.FromArgb(ShapeFillAlpha, r, g, b));
                return new AnnotationOverlay(
                    record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height,
                    fill, stroke, record.Shape, BuildShapeGeometry(record, scale, thickness), thickness,
                    UsesGeometryLayer: true);
            }

            case AnnotationKind.Underline:
            case AnnotationKind.Strikeout:
            {
                // 文本标记（V4 T3）：逐行一条线——下划线在行底、删除线在行中线。
                // 不透明描边（与手写/形状同口径），填充透明。
                var stroke = Freeze(Color.FromArgb(StrokeAlpha, r, g, b));
                var fill = Freeze(Color.FromArgb(ShapeFillAlpha, r, g, b));
                return new AnnotationOverlay(
                    record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height,
                    fill, stroke, null,
                    BuildTextMarkupLineGeometry(record, scale, record.Kind == AnnotationKind.Underline),
                    Math.Max(1.0, thickness),
                    UsesGeometryLayer: true);
            }

            case AnnotationKind.Stamp:
            {
                // 图章（V4 T4）：未保存前用叠加层预览——预置图章显示文字标签，图片图章显示图片。
                // 保存后记录会移出客户端模型，改由 PDF 内嵌注释渲染（引擎生成的真实外观）。
                var stroke = Freeze(Color.FromArgb(StrokeAlpha, r, g, b));
                var fill = Freeze(Color.FromArgb(ShapeFillAlpha, r, g, b));
                return new AnnotationOverlay(
                    record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height,
                    fill, stroke, null, null, 0,
                    Label: record.StampImage is null ? StampPresets.LabelFor(record.StampToken) : null,
                    Image: BuildStampImage(record.StampImage));
            }

            default:
            {
                // Highlight / Note：半透明填充 + 压暗边框。
                // V4 T3：多行选区按逐行 quads 填充——单矩形填充会把行间空隙也涂满
                // （实测 227,962 px vs 56,468 px，见 .omo/evidence/v4-spike-underline.md）。
                var fill = Freeze(Color.FromArgb(FillAlpha, r, g, b));
                var border = Freeze(Color.FromArgb(BorderAlpha, Darken(r), Darken(g), Darken(b)));
                if (BuildHighlightFillGeometry(record, scale) is { } multiLine)
                {
                    return new AnnotationOverlay(
                        record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height,
                        fill, border, null, multiLine, 1.0, UsesGeometryLayer: true);
                }

                return new AnnotationOverlay(
                    record.Id, record.Kind, record.Note, dip.X, dip.Y, dip.Width, dip.Height, fill, border);
            }
        }
    }

    /// <summary>
    /// 文本标记（下划线/删除线）的逐行线几何。坐标相对条目左上角（= 记录并集矩形左上角）：
    /// 下划线画在每行底边、删除线画在每行中线。V4 T3。
    /// </summary>
    private static Geometry? BuildTextMarkupLineGeometry(AnnotationRecord record, double scale, bool underline)
    {
        if (scale <= 0) return null;

        IReadOnlyList<PdfRect> rects = record.Quads is { Count: > 0 } quads ? quads : [record.Rect];
        var origin = AnnotationGeometry.NormalizeRect(record.Rect);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var item in rects)
        {
            var line = AnnotationGeometry.NormalizeRect(item);
            var y = underline ? line.Bottom : (line.Top + line.Bottom) / 2.0;
            var figure = new PathFigure
            {
                StartPoint = new Point((line.Left - origin.Left) * scale, (y - origin.Top) * scale),
                IsClosed = false,
                IsFilled = false,
            };
            figure.Segments.Add(new LineSegment(
                new Point((line.Right - origin.Left) * scale, (y - origin.Top) * scale), true));
            group.Children.Add(new PathGeometry([figure]));
        }

        if (group.Children.Count == 0) return null;
        group.Freeze();
        return group;
    }

    /// <summary>
    /// 图片图章的预览位图（V4 T4）。解码失败返回 null——坏图不阻断叠加层，
    /// 真正下发时引擎会显式报「图章图片格式不受支持」。
    /// </summary>
    private static ImageSource? BuildStampImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 立即解码，不持有流
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();                                 // 冻结以复用（叠加层会随缩放重建）
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 高亮/笔记的逐行填充几何。仅当存在多个 quads 时返回（单矩形继续走 BoxLayer，
    /// 保持 V2 渲染契约不变）。行间空隙不填充。V4 T3。
    /// </summary>
    private static Geometry? BuildHighlightFillGeometry(AnnotationRecord record, double scale)
    {
        if (scale <= 0 || record.Quads is not { Count: > 1 } quads) return null;

        var origin = AnnotationGeometry.NormalizeRect(record.Rect);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var item in quads)
        {
            var line = AnnotationGeometry.NormalizeRect(item);
            group.Children.Add(new RectangleGeometry(new Rect(
                (line.Left - origin.Left) * scale,
                (line.Top - origin.Top) * scale,
                Math.Max(0, line.Width * scale),
                Math.Max(0, line.Height * scale))));
        }

        if (group.Children.Count == 0) return null;
        group.Freeze();
        return group;
    }

    private static Geometry? BuildInkGeometry(
        IReadOnlyList<IReadOnlyList<PdfPoint>>? strokes, double scale, double originX, double originY)
    {
        if (strokes is null || strokes.Count == 0 || scale <= 0) return null;

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.Count < 2) continue;
            var figure = new PathFigure
            {
                StartPoint = ToDip(stroke[0], scale, originX, originY),
                IsClosed = false,
                IsFilled = false,
            };
            for (var i = 1; i < stroke.Count; i++)
            {
                figure.Segments.Add(new LineSegment(ToDip(stroke[i], scale, originX, originY), true));
            }
            group.Children.Add(new PathGeometry([figure]));
        }

        if (group.Children.Count == 0) return null;
        group.Freeze();
        return group;
    }

    private static Geometry? BuildShapeGeometry(AnnotationRecord record, double scale, double thickness)
    {
        if (scale <= 0) return null;

        var rect = AnnotationGeometry.NormalizeRect(record.Rect);
        // 几何相对条目左上角（条目由 Canvas.Left/Top 定位），避免与 X/Y 二次偏移。
        var width = Math.Max(0, rect.Width * scale);
        var height = Math.Max(0, rect.Height * scale);

        switch (record.Shape)
        {
            case ShapeKind.Square:
            {
                var geometry = new RectangleGeometry(new Rect(0, 0, width, height));
                geometry.Freeze();
                return geometry;
            }

            case ShapeKind.Circle:
            {
                var geometry = new EllipseGeometry(new Rect(0, 0, width, height));
                geometry.Freeze();
                return geometry;
            }

            case ShapeKind.Line:
            case ShapeKind.Arrow:
            {
                if (record.LinePoints is not { Count: 2 } points) return null;
                var start = ToDip(points[0], scale, rect.Left, rect.Top);
                var end = ToDip(points[1], scale, rect.Left, rect.Top);
                var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
                figure.Segments.Add(new LineSegment(end, true));
                if (record.Shape == ShapeKind.Arrow)
                {
                    // 开口箭头：以终点为顶点、回指起点方向的两段短线（方向无二义：起点→终点）。
                    var head = Math.Max(8.0, thickness * 3.0);
                    var angle = Math.Atan2(start.Y - end.Y, start.X - end.X);
                    figure.Segments.Add(new LineSegment(
                        new Point(end.X + head * Math.Cos(angle - ArrowSpread),
                                  end.Y + head * Math.Sin(angle - ArrowSpread)), true));
                    figure.Segments.Add(new LineSegment(end, true));
                    figure.Segments.Add(new LineSegment(
                        new Point(end.X + head * Math.Cos(angle + ArrowSpread),
                                  end.Y + head * Math.Sin(angle + ArrowSpread)), true));
                }

                var geometry = new PathGeometry([figure]);
                geometry.Freeze();
                return geometry;
            }
        }

        return null;
    }

    private static Point ToDip(PdfPoint point, double scale, double originX, double originY)
        => new((point.X - originX) * scale, (point.Y - originY) * scale);

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
