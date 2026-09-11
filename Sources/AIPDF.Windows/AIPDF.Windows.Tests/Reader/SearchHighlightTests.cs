// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 页内高亮 DIP 换算测试：纯函数换算（已知页宽/显示宽）+ ReaderViewModel 集成（设置/缩放重算/切换/清除）。
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>搜索高亮坐标换算测试（PDF 点 → 页内 DIP）。</summary>
public sealed class SearchHighlightTests
{
    // ---------------------------------------------------------------- 纯换算

    [Fact]
    public void ToSearchHighlightDip_ScalesByDisplayOverPageWidth()
    {
        var rect = new PdfRect(100, 200, 150, 220); // 左上原点 pt
        const double pageWidthPt = 595;
        var displayWidthDip = pageWidthPt * PageLayoutCalculator.DpiScale; // 100%

        var highlight = PageLayoutCalculator.ToSearchHighlightDip(rect, pageWidthPt, displayWidthDip);
        var scale = displayWidthDip / pageWidthPt;

        Assert.Equal(100 * scale, highlight.X, 4);
        Assert.Equal(200 * scale, highlight.Y, 4);
        Assert.Equal(50 * scale, highlight.Width, 4);
        Assert.Equal(20 * scale, highlight.Height, 4);
    }

    [Fact]
    public void ToSearchHighlightDip_At200Percent_DoublesDip()
    {
        var rect = new PdfRect(0, 0, 10, 10);
        const double pageWidthPt = 595;
        var displayWidthDip = pageWidthPt * 2 * PageLayoutCalculator.DpiScale; // 200%

        var highlight = PageLayoutCalculator.ToSearchHighlightDip(rect, pageWidthPt, displayWidthDip);

        // 10pt × 2 × 96/72 = 26.666...
        Assert.Equal(10 * 2 * PageLayoutCalculator.DpiScale, highlight.Width, 5);
        Assert.Equal(highlight.Width, highlight.Height, 5);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(595, 0)]
    [InlineData(-1, 100)]
    public void ToSearchHighlightDip_DegenerateInputs_ReturnZero(double pageWidthPt, double displayWidthDip)
    {
        var highlight = PageLayoutCalculator.ToSearchHighlightDip(new PdfRect(1, 2, 3, 4), pageWidthPt, displayWidthDip);
        Assert.Equal(0, highlight.X);
        Assert.Equal(0, highlight.Y);
        Assert.Equal(0, highlight.Width);
        Assert.Equal(0, highlight.Height);
    }

    // ---------------------------------------------------------------- ReaderViewModel 集成

    [Fact]
    public void SetSearchHighlight_RecomputesOnZoom_SwitchesPage_AndClears()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        var rect = new PdfRect(100, 200, 150, 220);
        vm.SetSearchHighlight(0, [rect]);

        var page0 = vm.Pages[0];
        var at100 = Assert.Single(page0.Highlights);
        var scale100 = page0.DisplayWidth / page0.WidthPt;
        Assert.Equal(rect.Left * scale100, at100.X, 4);
        Assert.Equal(rect.Top * scale100, at100.Y, 4);

        // 缩放 200%：高亮按新显示尺寸重算，坐标同倍放大
        vm.ZoomPercent = 200;
        var at200 = Assert.Single(page0.Highlights);
        var scale200 = page0.DisplayWidth / page0.WidthPt;
        Assert.Equal(rect.Left * scale200, at200.X, 4);
        Assert.True(at200.X > at100.X, "缩放后高亮 X 应增大");

        // 切换到另一页命中：只保留一页高亮
        vm.SetSearchHighlight(1, [rect]);
        Assert.Empty(page0.Highlights);
        Assert.Single(vm.Pages[1].Highlights);

        // 退出搜索：全部清除
        vm.ClearSearchHighlight();
        Assert.Empty(vm.Pages[1].Highlights);
    }

    [Fact]
    public void SetSearchHighlight_OutOfRange_IsIgnored()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        vm.SetSearchHighlight(9999, [new PdfRect(1, 2, 3, 4)]);

        Assert.All(vm.Pages, page => Assert.Empty(page.Highlights));
    }
}
