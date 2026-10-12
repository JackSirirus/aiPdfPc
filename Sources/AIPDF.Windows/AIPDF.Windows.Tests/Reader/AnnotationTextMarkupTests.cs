// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T3 文本标记（下划线/删除线）测试：模型工厂的逐行 quads 承载、内容等价、
// 叠加层投影（逐行线几何 / 多行高亮的逐行填充）、以及「单矩形仍走 BoxLayer」的兼容性。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>下划线/删除线与多行高亮的逐行几何（纯逻辑，无 UI）。</summary>
public sealed class AnnotationTextMarkupTests
{
    private const double A4WidthPt = 595;
    private static double DisplayWidth => A4WidthPt * PageLayoutCalculator.DpiScale;
    private static double Scale => DisplayWidth / A4WidthPt;

    private static readonly PdfRect[] ThreeLines =
    [
        new PdfRect(72, 100, 300, 114),
        new PdfRect(72, 180, 300, 194),
        new PdfRect(72, 260, 300, 274),
    ];

    // ---------------------------------------------------------------- 模型工厂

    [Fact]
    public void CreateTextMarkup_StoresPerLineQuads_AndUnionRect()
    {
        var record = AnnotationRecord.CreateTextMarkup(AnnotationKind.Underline, 3, ThreeLines, "#FF0000");

        Assert.Equal(AnnotationKind.Underline, record.Kind);
        Assert.Equal(3, record.PageIndex);
        Assert.Equal("#FF0000", record.Color);
        // Rect 恒为并集（命中测试/外接框），真正下发的几何是 Quads
        Assert.Equal(new PdfRect(72, 100, 300, 274), record.Rect);
        Assert.NotNull(record.Quads);
        Assert.Equal(3, record.Quads!.Count);
        Assert.Equal(ThreeLines, record.Quads);
    }

    [Fact]
    public void CreateTextMarkup_RejectsEmptyAndNonMarkupKind()
    {
        Assert.Throws<ArgumentException>(
            () => AnnotationRecord.CreateTextMarkup(AnnotationKind.Underline, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AnnotationRecord.CreateTextMarkup(AnnotationKind.Ink, 0, ThreeLines));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AnnotationRecord.CreateTextMarkup(AnnotationKind.Note, 0, ThreeLines));
    }

    [Fact]
    public void CreateHighlight_SingleRect_KeepsNullQuads_ForBackwardCompatibility()
    {
        var record = AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 10, 10));

        Assert.Equal(AnnotationKind.Highlight, record.Kind);
        Assert.Null(record.Quads);
        Assert.Equal(new PdfRect(0, 0, 10, 10), record.Rect);
        // 单矩形文本标记同样不落 Quads（与 V2 记录等价）
        var single = AnnotationRecord.CreateTextMarkup(AnnotationKind.Underline, 0, [new PdfRect(0, 0, 10, 10)]);
        Assert.Null(single.Quads);
        Assert.Equal(new PdfRect(0, 0, 10, 10), single.Rect);
    }

    [Fact]
    public void ContentEquals_ComparesQuads()
    {
        var record = AnnotationRecord.CreateTextMarkup(AnnotationKind.Strikeout, 0, ThreeLines) with { Id = "x" };

        Assert.True(record.ContentEquals(record with { State = AnnotationState.Saved })); // State 不参与
        Assert.False(record.ContentEquals(record with { Quads = [ThreeLines[0]] }));
        Assert.False(record.ContentEquals(record with
        {
            Quads = [ThreeLines[0], ThreeLines[1], new PdfRect(72, 260, 300, 999)],
        }));
        Assert.True(record.ContentEquals(record with { Quads = ThreeLines.ToArray() }));
    }

    // ---------------------------------------------------------------- 叠加层投影

    [Fact]
    public void Overlay_Underline_DrawsOneLinePerRow_AtRowBottom()
    {
        var record = AnnotationRecord.CreateTextMarkup(AnnotationKind.Underline, 0, ThreeLines, "#FF0000");

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        Assert.True(overlay.UsesGeometryLayer, "下划线必须走几何层");
        var group = Assert.IsType<GeometryGroup>(overlay.Geometry);
        Assert.True(group.IsFrozen, "几何必须冻结以复用");
        Assert.Equal(3, group.Children.Count);   // 逐行一条线（单框会退化成跨行实心带）
        // 几何相对条目左上角（= 并集矩形左上角 y=100）：每行下划线落在自己那行的底边
        var ys = group.Children
            .Select(child => Assert.IsType<PathGeometry>(child).Figures[0].StartPoint.Y / Scale)
            .ToArray();
        Assert.Equal([14.0, 94.0, 174.0], ys.Select(v => Math.Round(v, 3)).ToArray());

        var stroke = Assert.IsType<SolidColorBrush>(overlay.Border);
        Assert.Equal((byte)0xFF, stroke.Color.A);      // 不透明描边（与手写/形状同口径）
        Assert.Equal(0.0, Assert.IsType<SolidColorBrush>(overlay.Fill).Color.A);
    }

    [Fact]
    public void Overlay_Strikeout_DrawsOneLinePerRow_AtRowMiddle()
    {
        var record = AnnotationRecord.CreateTextMarkup(AnnotationKind.Strikeout, 0, ThreeLines, "#0000FF");

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        var group = Assert.IsType<GeometryGroup>(overlay.Geometry);
        Assert.Equal(3, group.Children.Count);
        // 删除线落在每行中线：107/187/267 相对并集原点 y=100 → 7/87/167
        var ys = group.Children
            .Select(child => Assert.IsType<PathGeometry>(child).Figures[0].StartPoint.Y / Scale)
            .ToArray();
        Assert.Equal([7.0, 87.0, 167.0], ys.Select(v => Math.Round(v, 3)).ToArray());
    }

    [Fact]
    public void Overlay_MultiLineHighlight_FillsEachRow_WithoutFillingGaps()
    {
        var record = AnnotationRecord.CreateTextMarkup(AnnotationKind.Highlight, 0, ThreeLines);

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        Assert.True(overlay.UsesGeometryLayer, "多行高亮必须走逐行填充几何");
        var group = Assert.IsType<GeometryGroup>(overlay.Geometry);
        Assert.Equal(3, group.Children.Count);
        foreach (var child in group.Children)
        {
            var rect = Assert.IsType<RectangleGeometry>(child).Rect;
            // 每行只填自己那一行的高度（14pt），而不是并集的 174pt——行间空隙保持不填充
            Assert.Equal(14 * Scale, rect.Height, 3);
        }
    }

    [Fact]
    public void Overlay_SingleLineHighlight_StaysOnBoxLayer()
    {
        var single = AnnotationRecord.CreateHighlight(0, new PdfRect(72, 100, 300, 114));
        var overlay = AnnotationOverlay.FromRecord(single, A4WidthPt, DisplayWidth);

        // 单矩形保持 V2 渲染契约（BoxLayer：Border + 半透明填充）
        Assert.False(overlay.UsesGeometryLayer);
        Assert.Null(overlay.Geometry);
        var fill = Assert.IsType<SolidColorBrush>(overlay.Fill);
        Assert.Equal(0x66, fill.Color.A);
    }

    [Fact]
    public void Overlay_Note_StaysOnBoxLayer_SoMarkerDotKeepsRendering()
    {
        var note = AnnotationRecord.CreateNote(0, new PdfRect(72, 100, 300, 114), "备注");
        var overlay = AnnotationOverlay.FromRecord(note, A4WidthPt, DisplayWidth);

        Assert.False(overlay.UsesGeometryLayer, "笔记靠 BoxLayer 里的标记圆点识别");
        Assert.Equal(AnnotationKind.Note, overlay.Kind);
    }
}
