// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T1 批注模型测试：记录工厂/状态、页内 DIP 投影、页级集合增删、旋转页禁注（M-11）、页释放保留记录。
using System;
using System.Threading.Tasks;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>批注模型与坐标映射（纯逻辑 + ReaderViewModel 集成；无 UI）。</summary>
public sealed class AnnotationModelTests
{
    private const double A4WidthPt = 595;

    // ---------------------------------------------------------------- 记录工厂

    [Fact]
    public void CreateHighlight_AppliesDefaults()
    {
        var rect = new PdfRect(100, 200, 150, 220);
        var record = AnnotationRecord.CreateHighlight(3, rect);

        Assert.False(string.IsNullOrEmpty(record.Id));
        Assert.Equal(3, record.PageIndex);
        Assert.Equal(rect, record.Rect);
        Assert.Equal(AnnotationKind.Highlight, record.Kind);
        Assert.Equal(AnnotationRecord.DefaultHighlightColor, record.Color);
        Assert.Null(record.Note);
        Assert.Equal(AnnotationState.Unsaved, record.State);
        Assert.True(DateTimeOffset.UtcNow - record.CreatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void CreateNote_StoresText_AndRejectsBlank()
    {
        var record = AnnotationRecord.CreateNote(0, new PdfRect(1, 2, 3, 4), "hello", "#00FF00");
        Assert.Equal(AnnotationKind.Note, record.Kind);
        Assert.Equal("hello", record.Note);
        Assert.Equal("#00FF00", record.Color);

        Assert.Throws<ArgumentException>(() => AnnotationRecord.CreateNote(0, new PdfRect(1, 2, 3, 4), "   "));
    }

    [Fact]
    public void WithStateAndColor_ReturnCopies()
    {
        var record = AnnotationRecord.CreateHighlight(0, new PdfRect(1, 2, 3, 4));
        var saving = record.WithState(AnnotationState.Saving);
        var recolored = record.WithColor("#FF0000");

        Assert.Equal(AnnotationState.Unsaved, record.State);
        Assert.Equal(AnnotationState.Saving, saving.State);
        Assert.Equal("#FF0000", recolored.Color);
        Assert.Equal(record.Id, saving.Id);
    }

    // ---------------------------------------------------------------- DIP 投影

    [Fact]
    public void Overlay_Projection_ScalesByDisplayOverPageWidth()
    {
        var record = AnnotationRecord.CreateHighlight(0, new PdfRect(100, 200, 150, 220));
        var displayWidthDip = A4WidthPt * PageLayoutCalculator.DpiScale; // 100%

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, displayWidthDip);
        var scale = displayWidthDip / A4WidthPt;

        Assert.Equal(100 * scale, overlay.X, 4);
        Assert.Equal(200 * scale, overlay.Y, 4);
        Assert.Equal(50 * scale, overlay.Width, 4);
        Assert.Equal(20 * scale, overlay.Height, 4);
        Assert.Equal(record.Id, overlay.Id);
        Assert.True(overlay.Fill.IsFrozen, "叠加层 Brush 必须冻结以复用");
        Assert.True(overlay.Border.IsFrozen);
    }

    [Fact]
    public void Overlay_Projection_DoublesAt200Percent()
    {
        var record = AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 10, 10));
        var displayWidthDip = A4WidthPt * 2 * PageLayoutCalculator.DpiScale;

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, displayWidthDip);

        Assert.Equal(10 * 2 * PageLayoutCalculator.DpiScale, overlay.Width, 5);
        Assert.Equal(overlay.Width, overlay.Height, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#XYZ")]
    [InlineData("#12345")]
    public void Overlay_InvalidColor_FallsBackToDefaultYellow(string color)
    {
        var record = AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 5, 5), color);
        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, A4WidthPt * PageLayoutCalculator.DpiScale);
        var fill = Assert.IsType<System.Windows.Media.SolidColorBrush>(overlay.Fill);
        // 默认 #FFFF00：红/绿高、蓝低
        Assert.True(fill.Color.R > 200 && fill.Color.G > 200 && fill.Color.B < 60);
    }

    [Fact]
    public void Overlay_DegenerateInputs_ReturnZeroRect()
    {
        var record = AnnotationRecord.CreateHighlight(0, new PdfRect(1, 2, 3, 4));
        var overlay = AnnotationOverlay.FromRecord(record, pageWidthPt: 0, displayWidthDip: 100);
        Assert.Equal(0, overlay.X);
        Assert.Equal(0, overlay.Y);
        Assert.Equal(0, overlay.Width);
        Assert.Equal(0, overlay.Height);
    }

    // ---------------------------------------------------------------- ReaderViewModel 集成

    [Fact]
    public void AddRemove_Annotation_UpdatesRecordsAndOverlays()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        var record = vm.AddHighlight(0, new PdfRect(100, 200, 150, 220));
        Assert.NotNull(record);
        Assert.Single(vm.GetAnnotations(0));
        var overlay = Assert.Single(vm.Pages[0].Annotations);
        Assert.Equal(record!.Id, overlay.Id);

        Assert.True(vm.RemoveAnnotation(0, record.Id));
        Assert.Empty(vm.GetAnnotations(0));
        Assert.Empty(vm.Pages[0].Annotations);
    }

    [Fact]
    public void AddNote_CreatesNoteOverlay()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        var record = vm.AddNote(0, new PdfRect(10, 10, 60, 40), "note body");
        Assert.NotNull(record);
        var overlay = Assert.Single(vm.Pages[0].Annotations);
        Assert.Equal(AnnotationKind.Note, overlay.Kind);
        Assert.Equal("note body", overlay.Note);
    }

    [Fact]
    public void AddAnnotation_OutOfRange_IsRejected()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        Assert.Null(vm.AddHighlight(9999, new PdfRect(1, 2, 3, 4)));
        Assert.Null(vm.AddAnnotation(AnnotationRecord.CreateHighlight(-1, new PdfRect(1, 2, 3, 4))));
        Assert.All(vm.Pages, p => Assert.Empty(p.Annotations));
    }

    [Fact]
    public void ZoomChange_RecomputesAnnotationOverlays()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800); // 测量视口，缩放锚定/渲染窗口判定需要

        var rect = new PdfRect(100, 200, 150, 220);
        var record = vm.AddHighlight(0, rect)!;
        var at100 = Assert.Single(vm.Pages[0].Annotations);
        var scale100 = vm.Pages[0].DisplayWidth / vm.Pages[0].WidthPt;
        Assert.Equal(rect.Left * scale100, at100.X, 4);

        vm.ZoomPercent = 200;
        var at200 = Assert.Single(vm.Pages[0].Annotations);
        var scale200 = vm.Pages[0].DisplayWidth / vm.Pages[0].WidthPt;
        Assert.Equal(rect.Left * scale200, at200.X, 4);
        Assert.True(at200.X > at100.X, "缩放后批注叠加 X 应增大");
        Assert.Equal(record.Id, at200.Id);
    }

    [Fact]
    public async Task PageReleased_ReleasesOverlays_ButKeepsRecords_AndRebuilds()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);
        await WaitUntilAsync(() => vm.Pages[0].IsRendered, "页 0 应渲染完成");

        var record = vm.AddHighlight(0, new PdfRect(100, 200, 150, 220))!;
        Assert.Single(vm.Pages[0].Annotations);

        // 跳到远端页：页 0 滚出视口被释放 → 叠加层清空，但记录保留
        vm.GoToPage(30);
        await WaitUntilAsync(() => vm.Pages[0].Annotations.Count == 0, "页 0 释放后叠加层应清空");
        Assert.Single(vm.GetAnnotations(0));
        Assert.Equal(record.Id, vm.GetAnnotations(0)[0].Id);

        // 滚回：页 0 重新进入渲染窗口 → 投影按记录重建
        vm.GoToPage(1);
        await WaitUntilAsync(() => vm.Pages[0].Annotations.Count == 1, "页 0 滚回后批注应重建");
        Assert.Equal(record.Id, vm.Pages[0].Annotations[0].Id);
    }

    [Fact]
    public void RotatedPage_IsForbiddenForAnnotation_M11()
    {
        var rotated = System.IO.Path.Combine(Corpus.Dir, "rotated.pdf");
        Assert.True(System.IO.File.Exists(rotated), "rotated.pdf 语料应存在");

        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(rotated);
        Assert.Equal(90, svc.GetPageRotation(docId, 0));

        using var vm = new ReaderViewModel(svc);
        vm.LoadDocument(rotated);
        Assert.False(vm.CanAnnotate(0), "旋转页（/Rotate 90）在 V2 禁止批注");
        Assert.Null(vm.AddHighlight(0, new PdfRect(100, 200, 150, 220)));
        Assert.Empty(vm.GetAnnotations(0));
    }

    [Fact]
    public void NonRotatedPage_AllowsAnnotation()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(Corpus.Mixed100);
        Assert.Equal(0, svc.GetPageRotation(docId, 0));

        using var vm = new ReaderViewModel(svc);
        vm.LoadDocument(Corpus.Mixed100);
        Assert.True(vm.CanAnnotate(0));
        Assert.NotNull(vm.AddHighlight(0, new PdfRect(10, 10, 50, 30)));
    }

    // ---------------------------------------------------------------- 工具

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Xunit.Sdk.XunitException($"{message}（等待 {timeoutMs}ms 超时）");
            await Task.Delay(50);
        }
    }
}
