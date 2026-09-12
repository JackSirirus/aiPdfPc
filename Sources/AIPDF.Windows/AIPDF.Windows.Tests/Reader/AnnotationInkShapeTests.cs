// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T11（C3）手写/形状批注测试：模型工厂与内容等价、几何承载、叠加层投影（冻结几何/Brush）、
// ReaderViewModel 集成（新增/旋转页禁注）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>手写/形状批注模型与投影（纯逻辑 + ReaderViewModel 集成；无 UI）。</summary>
public sealed class AnnotationInkShapeTests
{
    private const double A4WidthPt = 595;
    private static double DisplayWidth => A4WidthPt * PageLayoutCalculator.DpiScale;

    // ---------------------------------------------------------------- 模型工厂

    [Fact]
    public void CreateInk_AppliesDefaults_AndComputesBoundingBox()
    {
        var strokes = new IReadOnlyList<PdfPoint>[]
        {
            new[] { new PdfPoint(10, 20), new PdfPoint(30, 5), new PdfPoint(50, 40) },
            new[] { new PdfPoint(5, 60) }, // 单点段：归一化时丢弃
        };

        var record = AnnotationRecord.CreateInk(2, strokes);

        Assert.Equal(AnnotationKind.Ink, record.Kind);
        Assert.Equal(2, record.PageIndex);
        Assert.Equal(AnnotationRecord.DefaultInkColor, record.Color);
        Assert.Equal(AnnotationRecord.DefaultLineWidth, record.LineWidth);
        Assert.Null(record.Shape);
        Assert.Null(record.LinePoints);
        Assert.NotNull(record.Strokes);
        Assert.Single(record.Strokes!);
        Assert.Equal(new PdfRect(10, 5, 50, 40), record.Rect);
        // 防御性复制：外部修改源列表不影响记录
        Assert.NotSame(strokes[0], record.Strokes![0]);
    }

    [Fact]
    public void CreateInk_EmptyOrDegenerate_Throws()
    {
        Assert.Throws<ArgumentException>(() => AnnotationRecord.CreateInk(0, []));
        Assert.Throws<ArgumentException>(() => AnnotationRecord.CreateInk(0, [new[] { new PdfPoint(1, 1) }]));
    }

    [Fact]
    public void CreateShape_Rect_NormalizesAndStoresKind()
    {
        var square = AnnotationRecord.CreateShape(0, ShapeKind.Square, new PdfRect(100, 100, 40, 20), "#112233", 3);

        Assert.Equal(AnnotationKind.Shape, square.Kind);
        Assert.Equal(ShapeKind.Square, square.Shape);
        Assert.Equal(new PdfRect(40, 20, 100, 100), square.Rect);
        Assert.Null(square.Strokes);
        Assert.Null(square.LinePoints);
        Assert.Equal("#112233", square.Color);
        Assert.Equal(3, square.LineWidth);
    }

    [Fact]
    public void CreateShape_Line_KeepsStartEndDirection()
    {
        var start = new PdfPoint(10, 200);
        var end = new PdfPoint(120, 80);
        var line = AnnotationRecord.CreateShape(1, ShapeKind.Line, start, end);

        Assert.Equal(AnnotationKind.Shape, line.Kind);
        Assert.Equal(ShapeKind.Line, line.Shape);
        Assert.Equal(new[] { start, end }, line.LinePoints);
        Assert.Equal(new PdfRect(10, 80, 120, 200), line.Rect);
    }

    [Fact]
    public void CreateShape_WrongOverload_Throws()
    {
        // 线/箭头必须用两点重载（传 PdfRect 抛错）
        Assert.Throws<ArgumentException>(() =>
            AnnotationRecord.CreateShape(0, ShapeKind.Line, new PdfRect(0, 0, 10, 10)));
        // 矩形/圆必须用 PdfRect 重载（传两点抛错）
        Assert.Throws<ArgumentException>(() =>
            AnnotationRecord.CreateShape(0, ShapeKind.Square, new PdfPoint(0, 0), new PdfPoint(1, 1)));
    }

    [Fact]
    public void LineWidth_IsClampedToBounds()
    {
        var thin = AnnotationRecord.CreateInk(
            0, new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(0, 0), new PdfPoint(1, 1) } }, lineWidth: 0);
        Assert.Equal(AnnotationRecord.MinLineWidth, thin.LineWidth);

        var thick = AnnotationRecord.CreateShape(0, ShapeKind.Square, new PdfRect(0, 0, 10, 10), lineWidth: 999);
        Assert.Equal(AnnotationRecord.MaxLineWidth, thick.LineWidth);
    }

    [Fact]
    public void ContentEquals_ComparesNewGeometryFields()
    {
        var ink = AnnotationRecord.CreateInk(
            0, new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(0, 0), new PdfPoint(5, 5) } }) with { Id = "x" };
        Assert.True(ink.ContentEquals(ink with { State = AnnotationState.Saved })); // State 不参与

        var differentStrokes = ink with
        {
            Strokes = new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(0, 0), new PdfPoint(9, 9) } },
        };
        Assert.False(ink.ContentEquals(differentStrokes));

        var line = AnnotationRecord.CreateShape(0, ShapeKind.Line, new PdfPoint(0, 0), new PdfPoint(10, 10)) with { Id = "y" };
        Assert.False(line.ContentEquals(line with { LinePoints = new[] { new PdfPoint(0, 0), new PdfPoint(20, 20) } }));
        Assert.False(line.ContentEquals(line with { Shape = ShapeKind.Arrow }));
        Assert.False(line.ContentEquals(line with { LineWidth = line.LineWidth + 1 }));
    }

    // ---------------------------------------------------------------- 叠加层投影

    [Fact]
    public void Overlay_Ink_BuildsFrozenGeometry_WithOpaqueStroke()
    {
        var record = AnnotationRecord.CreateInk(
            0,
            new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(0, 0), new PdfPoint(50, 50), new PdfPoint(100, 20) } },
            "#0000FF", 3);

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        Assert.NotNull(overlay.Geometry);
        Assert.True(overlay.Geometry!.IsFrozen, "几何必须冻结以复用");
        var stroke = Assert.IsType<SolidColorBrush>(overlay.Border);
        Assert.True(stroke.IsFrozen);
        Assert.Equal((byte)0xFF, stroke.Color.A);
        var scale = DisplayWidth / A4WidthPt;
        Assert.Equal(3 * scale, overlay.StrokeThickness, 4);
        Assert.Equal(100 * scale, overlay.Width, 4);
        Assert.Null(overlay.Shape);
    }

    [Fact]
    public void Overlay_Shapes_UseExpectedGeometry_AndArrowHasExtraSegments()
    {
        var square = AnnotationOverlay.FromRecord(
            AnnotationRecord.CreateShape(0, ShapeKind.Square, new PdfRect(10, 20, 110, 120)), A4WidthPt, DisplayWidth);
        var circle = AnnotationOverlay.FromRecord(
            AnnotationRecord.CreateShape(0, ShapeKind.Circle, new PdfRect(10, 20, 110, 120)), A4WidthPt, DisplayWidth);
        var line = AnnotationOverlay.FromRecord(
            AnnotationRecord.CreateShape(0, ShapeKind.Line, new PdfPoint(0, 0), new PdfPoint(100, 0)), A4WidthPt, DisplayWidth);
        var arrow = AnnotationOverlay.FromRecord(
            AnnotationRecord.CreateShape(0, ShapeKind.Arrow, new PdfPoint(0, 0), new PdfPoint(100, 0)), A4WidthPt, DisplayWidth);

        Assert.IsType<RectangleGeometry>(square.Geometry);
        Assert.IsType<EllipseGeometry>(circle.Geometry);
        var lineGeometry = Assert.IsType<PathGeometry>(line.Geometry);
        var arrowGeometry = Assert.IsType<PathGeometry>(arrow.Geometry);

        Assert.True(arrowGeometry.IsFrozen);
        Assert.True(arrowGeometry.Figures[0].Segments.Count > lineGeometry.Figures[0].Segments.Count,
            "箭头应比线段多出开放式箭头两翼");
        Assert.Equal(ShapeKind.Arrow, arrow.Shape);
    }

    [Fact]
    public void Overlay_DegenerateInk_StillReturnsFrozenBrushes()
    {
        var record = AnnotationRecord.CreateShape(0, ShapeKind.Line, new PdfPoint(1, 2), new PdfPoint(3, 4));
        var overlay = AnnotationOverlay.FromRecord(record, pageWidthPt: 0, displayWidthDip: 100);

        Assert.Equal(0, overlay.X);
        Assert.Equal(0, overlay.Width);
        Assert.Null(overlay.Geometry); // 退化输入不产生几何
        Assert.True(overlay.Border.IsFrozen);
    }

    // ---------------------------------------------------------------- ReaderViewModel 集成

    [Fact]
    public void AddInkAndShapes_ProjectToOverlays()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);

        var ink = vm.AddInk(0, new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(10, 10), new PdfPoint(60, 60) } }, "#000000", 2);
        var square = vm.AddShape(0, ShapeKind.Square, new PdfRect(10, 80, 60, 130));
        var arrow = vm.AddShape(0, ShapeKind.Arrow, new PdfPoint(10, 150), new PdfPoint(80, 200));

        Assert.NotNull(ink);
        Assert.NotNull(square);
        Assert.NotNull(arrow);

        var overlays = vm.Pages[0].Annotations;
        Assert.Equal(3, overlays.Count);
        Assert.Contains(overlays, o => o.Kind == AnnotationKind.Ink && o.Geometry is not null);
        Assert.Contains(overlays, o => o.Shape == ShapeKind.Square && o.Geometry is RectangleGeometry);
        Assert.Contains(overlays, o => o.Shape == ShapeKind.Arrow && o.Geometry is PathGeometry);

        Assert.Equal(3, vm.GetAllRecords().Count);
    }

    [Fact]
    public void AddInk_RotatedPage_IsRejected_M11()
    {
        var rotated = System.IO.Path.Combine(Corpus.Dir, "rotated.pdf");
        Assert.True(System.IO.File.Exists(rotated), "rotated.pdf 语料应存在");

        using var svc = new PdfiumRenderService();
        using var vm = new ReaderViewModel(svc);
        vm.LoadDocument(rotated);

        Assert.False(vm.CanAnnotate(0));
        Assert.Null(vm.AddInk(0, new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(0, 0), new PdfPoint(5, 5) } }));
        Assert.Null(vm.AddShape(0, ShapeKind.Square, new PdfRect(0, 0, 10, 10)));
        Assert.Null(vm.AddShape(0, ShapeKind.Line, new PdfPoint(0, 0), new PdfPoint(5, 5)));
        Assert.Empty(vm.GetAllRecords());
    }
}
