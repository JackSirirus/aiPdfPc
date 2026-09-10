// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 ReaderViewModel 集成测试（消费 T1 真实渲染服务 + 合成语料）：
//   文档装载、可视±缓冲渲染、视口外释放、页码跳转、缩放阶梯与 DPI 口径、适配档。
// 语料：tmp/pdfium-spike/gen_corpus.py 合成文件（不读个人文档）。
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>ReaderViewModel 与 T1 渲染服务的接线测试（无 UI；测试线程无同步上下文，Marshal 内联执行）。</summary>
public sealed class ReaderViewModelTests
{
    // ---------------------------------------------------------------- 装载

    [Fact]
    public void LoadDocument_BuildsPagesAndLayout()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        Assert.Equal(100, vm.PageCount);
        Assert.True(vm.HasDocument);
        Assert.Equal(100, vm.Pages.Count);
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.Equal("1 / 100", vm.PageIndicatorText);

        // A4 首页：pt 与 DIP 显示尺寸
        Assert.Equal(595, vm.Pages[0].WidthPt, 1);
        Assert.Equal(842, vm.Pages[0].HeightPt, 1);
        Assert.Equal(595 * PageLayoutCalculator.DpiScale, vm.Pages[0].DisplayWidth, 1);
        Assert.Equal(842 * PageLayoutCalculator.DpiScale, vm.Pages[0].DisplayHeight, 1);

        // 页间距与偏移换算一致（页 1 顶部 = 页 0 底部 + 16）
        var offsets = PageLayoutCalculator.ComputePageOffsets(vm.Pages.Select(p => new PageSize(p.WidthPt, p.HeightPt)).ToList(), 100);
        Assert.Equal(vm.Pages[0].DisplayHeight + PageLayoutCalculator.PageGap, offsets[1], 4);
    }

    [Fact]
    public void LoadDocument_CorruptFile_ReportsErrorWithoutCrash()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.GarbageHeader);

        Assert.False(vm.HasDocument);
        Assert.Equal(0, vm.PageCount);
        Assert.StartsWith(ReaderStrings.LoadErrorPrefix, vm.ErrorMessage);
        Assert.True(vm.HasError);
    }

    // ---------------------------------------------------------------- 渲染请求与释放

    [Fact]
    public async Task UpdateViewport_RendersVisibleAndBuffer_ReleasesOutside()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        // 首屏：视口 600 高 → 可见页 0（约 1123px）+ 缓冲 2 → 期望页 0..2 渲染
        vm.UpdateViewport(0, 600, 800);
        await WaitUntilAsync(() => vm.Pages[0].IsRendered, "页 0 应在超时内渲染完成");
        Assert.True(vm.Pages[1].IsRendered || vm.Pages[1].IsRendering || vm.Pages[1].PendingKey is not null,
            "缓冲页应进入渲染管线");
        Assert.Null(vm.Pages[50].Bitmap);
        Assert.Null(vm.Pages[50].PendingKey);

        // 跳到第 30 页：远端页进入可视区，页 0 释放
        var released = new System.Collections.Generic.List<int>();
        vm.PageReleased += (_, e) => released.Add(e.PageIndex);
        vm.GoToPage(30);
        await WaitUntilAsync(() => vm.Pages[29].IsRendered, "页 29 应在跳转后渲染完成");
        await WaitUntilAsync(() => released.Contains(0), "页 0 应在滚出视口后发出释放通知");
        Assert.Null(vm.Pages[0].Bitmap);

        // 当前页判定跟随滚动位置
        Assert.Equal(30, vm.CurrentPageNumber);
    }

    [Fact]
    public async Task RenderCompleted_BitmapIsFrozen()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);
        await WaitUntilAsync(() => vm.Pages[0].IsRendered, "页 0 应渲染完成");
        Assert.True(vm.Pages[0].Bitmap!.IsFrozen, "T1 位图必须冻结才能跨线程绑定");
        Assert.True(vm.Pages[0].Bitmap.PixelWidth > 0);
    }

    // ---------------------------------------------------------------- 页码跳转

    [Fact]
    public async Task JumpToPage_Valid_ScrollsAndTracksCurrentPage()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);

        double? requested = null;
        vm.ScrollRequested += (_, e) => requested = e.Offset;

        vm.JumpText = "5";
        vm.JumpToPageCommand.Execute(null);

        Assert.False(vm.IsJumpInvalid);
        Assert.NotNull(requested);
        var offsets = PageLayoutCalculator.ComputePageOffsets(vm.Pages.Select(p => new PageSize(p.WidthPt, p.HeightPt)).ToList(), 100);
        Assert.Equal(offsets[4], requested!.Value, 4);
        Assert.Equal(5, vm.CurrentPageNumber);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    [InlineData("")]
    public void JumpToPage_Invalid_MarksInvalidAndDoesNotScroll(string text)
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);

        var fired = false;
        vm.ScrollRequested += (_, _) => fired = true;

        vm.JumpText = text;
        vm.JumpToPageCommand.Execute(null);

        Assert.True(vm.IsJumpInvalid, $"输入「{text}」应拒绝跳转");
        Assert.False(fired);
        Assert.Equal(1, vm.CurrentPageNumber);

        // 修改输入后红边状态复位
        vm.JumpText = "2";
        Assert.False(vm.IsJumpInvalid);
    }

    // ---------------------------------------------------------------- 缩放阶梯

    [Fact]
    public void ZoomCommands_WalkLadderAndDisableAtEndpoints()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        Assert.Equal(100, vm.ZoomPercent);
        Assert.True(vm.CanZoomIn);
        Assert.True(vm.CanZoomOut);

        vm.ZoomInCommand.Execute(null);
        Assert.Equal(150, vm.ZoomPercent);
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(100, vm.ZoomPercent);

        // 连续放大到 400% → 放大禁用，再执行不越界【ST-11】
        for (var i = 0; i < 10; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(400, vm.ZoomPercent);
        Assert.False(vm.CanZoomIn);
        vm.ZoomInCommand.Execute(null);
        Assert.Equal(400, vm.ZoomPercent);

        // 连续缩小到 25% → 缩小禁用
        for (var i = 0; i < 10; i++) vm.ZoomOutCommand.Execute(null);
        Assert.Equal(25, vm.ZoomPercent);
        Assert.False(vm.CanZoomOut);
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(25, vm.ZoomPercent);
    }

    [Fact]
    public void ZoomByWheelSteps_SnapsToLadder()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        vm.ZoomByWheelSteps(1);
        Assert.Equal(150, vm.ZoomPercent);
        vm.ZoomByWheelSteps(-2);
        Assert.Equal(67, vm.ZoomPercent);
    }

    [Fact]
    public async Task ZoomChange_RerendersAtHighDpi_WhenAtLeast150Percent()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);
        await WaitUntilAsync(() => vm.Pages[0].IsRendered, "页 0 首次渲染（96dpi）");
        Assert.Equal((96, 100), vm.Pages[0].RenderedKey);

        // 200% ≥ 150% → 150dpi 重渲染（规格键含 dpi）
        vm.ZoomPercent = 200;
        await WaitUntilAsync(() => vm.Pages[0].RenderedKey == (150, 200), "200% 应触发 150dpi 重渲染");
        Assert.NotNull(vm.Pages[0].Bitmap);

        // 回到 100% → 96dpi
        vm.ZoomPercent = 100;
        await WaitUntilAsync(() => vm.Pages[0].RenderedKey == (96, 100), "100% 应回到 96dpi");
    }

    [Fact]
    public void FitWidth_ComputesFromViewport_AndManualZoomLeavesFitMode()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 900);

        vm.FitWidthCommand.Execute(null);
        Assert.True(vm.IsFitWidthActive);
        Assert.False(vm.IsFitPageActive);
        Assert.Equal(PageLayoutCalculator.FitWidthZoomPercent(900, 595), vm.ZoomPercent, 4);

        // 手动缩放脱离适配档（FR-06 AC-06.3）
        vm.ZoomInCommand.Execute(null);
        Assert.False(vm.IsFitWidthActive);
        Assert.Equal(150, vm.ZoomPercent);
    }

    [Fact]
    public void FitPage_TakesTighterConstraint()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 900);

        vm.FitPageCommand.Execute(null);
        Assert.True(vm.IsFitPageActive);
        var expected = PageLayoutCalculator.FitPageZoomPercent(900, 600, 595, 842);
        Assert.Equal(expected, vm.ZoomPercent, 4);
    }

    // ---------------------------------------------------------------- 500 页文档（虚拟化承载）

    [Fact]
    public async Task LoadDocument_Cjk500_RendersOnlyVisibleWindow()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Cjk500);
        Assert.Equal(500, vm.PageCount);

        vm.UpdateViewport(0, 600, 800);
        await WaitUntilAsync(() => vm.Pages[0].IsRendered, "500 页文档页 0 应渲染");

        // 渲染请求只落在可视 ± 缓冲窗口内（0..3，缓冲 2 + 视口可能触及页 1）
        for (var i = 4; i < 500; i++)
        {
            Assert.True(vm.Pages[i].Bitmap is null && vm.Pages[i].PendingKey is null,
                $"第 {i} 页远超缓冲窗口，不应持有位图或在途请求");
        }
    }

    // ---------------------------------------------------------------- 工具

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new Xunit.Sdk.XunitException($"{message}（等待 {timeoutMs}ms 超时）");
            }
            await Task.Delay(50);
        }
    }
}
