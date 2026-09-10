// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 纯逻辑测试：页码↔偏移换算（混合页尺寸）、缩放档位选择、视口缓冲计算、缩放锚定、页码解析。
using System.Collections.Generic;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>PageLayoutCalculator 纯函数覆盖。页尺寸用合成数据（混合页尺寸文档场景），不读任何真实文档。</summary>
public sealed class PageLayoutCalculatorTests
{
    /// <summary>合成混合页尺寸文档：A4 竖版 595×842、正方形 400×400、加长页 800×1200。</summary>
    private static readonly List<PageSize> MixedSizes =
    [
        new(595, 842),
        new(400, 400),
        new(800, 1200),
    ];

    private const double Dip = PageLayoutCalculator.DpiScale; // 4/3

    // ---------------------------------------------------------------- 布局换算

    [Fact]
    public void ComputePageOffsets_MixedSizes_UsesEachPageSizeAndGap()
    {
        var offsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);

        // DIP 高 = pt × 4/3；偏移逐页累加并加 16px 页间距
        Assert.Equal(0, offsets[0]);
        Assert.Equal(842 * Dip + PageLayoutCalculator.PageGap, offsets[1], 6);
        Assert.Equal(842 * Dip + 400 * Dip + 2 * PageLayoutCalculator.PageGap, offsets[2], 6);

        Assert.Equal(842 * Dip, heights[0], 6);
        Assert.Equal(400 * Dip, heights[1], 6);
        Assert.Equal(1200 * Dip, heights[2], 6);

        // 内容总高 = 末页底部 + 尾部页间距
        var content = PageLayoutCalculator.ComputeContentHeight(MixedSizes, 100);
        Assert.Equal(offsets[2] + heights[2] + PageLayoutCalculator.PageGap, content, 6);
    }

    [Fact]
    public void ComputePageOffsets_ScalesWithZoom()
    {
        var at100 = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);
        var at200 = PageLayoutCalculator.ComputePageHeights(MixedSizes, 200);
        for (var i = 0; i < MixedSizes.Count; i++)
        {
            Assert.Equal(at100[i] * 2, at200[i], 6);
        }
    }

    [Fact]
    public void ComputePageOffsets_EmptyDocument_ReturnsEmpty()
    {
        Assert.Empty(PageLayoutCalculator.ComputePageOffsets([], 100));
        Assert.Equal(0, PageLayoutCalculator.ComputeContentHeight([], 100));
        Assert.Equal(-1, PageLayoutCalculator.PageAtPoint([], 100));
    }

    // ---------------------------------------------------------------- 页定位

    [Fact]
    public void PageAtPoint_MixedSizes_RoundTrip()
    {
        var offsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);

        for (var i = 0; i < MixedSizes.Count; i++)
        {
            Assert.Equal(i, PageLayoutCalculator.PageAtPoint(offsets, offsets[i]));                    // 页顶
            Assert.Equal(i, PageLayoutCalculator.PageAtPoint(offsets, offsets[i] + heights[i] / 2));   // 页中
            Assert.Equal(i, PageLayoutCalculator.PageAtPoint(offsets, offsets[i] + heights[i] - 0.5)); // 页底前
        }

        // 页间隙 → 返回上一页；越界 → 钳到两端
        Assert.Equal(0, PageLayoutCalculator.PageAtPoint(offsets, offsets[1] - PageLayoutCalculator.PageGap / 2));
        Assert.Equal(0, PageLayoutCalculator.PageAtPoint(offsets, -100));
        Assert.Equal(2, PageLayoutCalculator.PageAtPoint(offsets, 1e6));
    }

    // ---------------------------------------------------------------- 视口缓冲

    [Fact]
    public void VisibleRange_MixedSizes_SinglePageViewport()
    {
        var offsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);

        // 视口 300px 完全落在第 1 页中部 → 仅 (1,1)，无缓冲
        var scrollTop = offsets[1] + heights[1] / 2 - 150;
        var (first, last) = PageLayoutCalculator.VisibleRange(offsets, heights, scrollTop, 300, 0);
        Assert.Equal(1, first);
        Assert.Equal(1, last);
    }

    [Fact]
    public void VisibleRange_MixedSizes_SpansPages()
    {
        var offsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);

        // 视口从第 1 页顶部上方 10px 开始、高 800 → 覆盖第 1 页（≈533）+ 页间距 + 第 2 页顶部（≈251）
        var scrollTop = offsets[1] - 10;
        var (first, last) = PageLayoutCalculator.VisibleRange(offsets, heights, scrollTop, 800, 0);
        Assert.Equal(0, first);
        Assert.Equal(2, last);
    }

    [Fact]
    public void VisibleRange_BufferExpandsAndClampsToBounds()
    {
        // 10 页等尺寸文档
        var tenSizes = new List<PageSize>(10);
        for (var i = 0; i < 10; i++) tenSizes.Add(new PageSize(595, 842));
        var offsets = PageLayoutCalculator.ComputePageOffsets(tenSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(tenSizes, 100);

        // 视口 300px 完全落在高页（1600px）内部 → 可见 (2,2)；+ 缓冲 2 → (0,4)
        var (first, last) = PageLayoutCalculator.VisibleRange(offsets, heights, offsets[2] + 50, 300, 2);
        Assert.Equal(0, first);
        Assert.Equal(4, last);

        // 可见 (7,7) + 缓冲 2 → (5,9)（尾部钳制）
        var (first2, last2) = PageLayoutCalculator.VisibleRange(offsets, heights, offsets[7] + 50, 300, 2);
        Assert.Equal(5, first2);
        Assert.Equal(9, last2);

        // 负缓冲按 0 处理
        var (first3, last3) = PageLayoutCalculator.VisibleRange(offsets, heights, offsets[2] + 50, 300, -1);
        Assert.Equal(2, first3);
        Assert.Equal(last3, first3);
    }

    [Fact]
    public void VisibleRange_EmptyOrUnmeasured_ReturnsEmptyRange()
    {
        var offsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);
        Assert.Equal((0, -1), PageLayoutCalculator.VisibleRange([], [], 0, 800, 2));
        Assert.Equal((0, -1), PageLayoutCalculator.VisibleRange(offsets, heights, 0, 0, 2));
    }

    // ---------------------------------------------------------------- 当前页判定

    [Fact]
    public void CurrentPageByMaxOverlap_MixedSizes_PicksLargestOverlap()
    {
        var offsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var heights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);

        // 视口完全在第 2 页内 → 第 2 页
        var scrollTop = offsets[2] + 100;
        Assert.Equal(2, PageLayoutCalculator.CurrentPageByMaxOverlap(offsets, heights, scrollTop, 300));

        // 90% 短页(400pt) + 10% 长页(1200pt)：短页交叠更大 → 胜出（混合尺寸关键场景）
        var shortPageHeight = heights[1]; // ≈533
        var scrollTop2 = offsets[2] - shortPageHeight * 0.9; // 短页尾 90% 在视口内
        Assert.Equal(1, PageLayoutCalculator.CurrentPageByMaxOverlap(offsets, heights, scrollTop2, shortPageHeight));

        // 精确各半（页 0 尾 100px + 页 1 头 100px，跨 16px 页间隙）→ 并列取靠前页
        // scrollTop = 页0底-100；视口高 = 100 + 16 + 100 = 216
        var scrollTop3 = offsets[1] - PageLayoutCalculator.PageGap - 100;
        Assert.Equal(0, PageLayoutCalculator.CurrentPageByMaxOverlap(offsets, heights, scrollTop3, 216));

        // 无交叠 → -1
        Assert.Equal(-1, PageLayoutCalculator.CurrentPageByMaxOverlap(offsets, heights, 1e5, 300));
    }

    // ---------------------------------------------------------------- 缩放阶梯

    [Fact]
    public void Ladder_NearestIndex_TiePrefersLower()
    {
        Assert.Equal(0, PageLayoutCalculator.NearestLadderIndex(25));
        Assert.Equal(3, PageLayoutCalculator.NearestLadderIndex(80));   // 80-67=13 < 100-80=20
        Assert.Equal(4, PageLayoutCalculator.NearestLadderIndex(87));   // 100-87=13 < 87-67=20
        Assert.Equal(3, PageLayoutCalculator.NearestLadderIndex(83.5)); // 并列取更低档
        Assert.Equal(8, PageLayoutCalculator.NearestLadderIndex(1000));
        Assert.Equal(0, PageLayoutCalculator.NearestLadderIndex(1));
    }

    [Fact]
    public void Ladder_Snap_ClampsToRange()
    {
        Assert.Equal(100, PageLayoutCalculator.SnapToLadder(87));
        Assert.Equal(25, PageLayoutCalculator.SnapToLadder(25));
        Assert.Equal(400, PageLayoutCalculator.SnapToLadder(400));
        Assert.Equal(400, PageLayoutCalculator.SnapToLadder(500));
        Assert.Equal(25, PageLayoutCalculator.SnapToLadder(10));
    }

    [Fact]
    public void Ladder_Step_WalksRungsAndClamps()
    {
        Assert.Equal(150, PageLayoutCalculator.StepLadder(100, 1));
        Assert.Equal(200, PageLayoutCalculator.StepLadder(100, 2));
        Assert.Equal(67, PageLayoutCalculator.StepLadder(100, -1));
        Assert.Equal(25, PageLayoutCalculator.StepLadder(100, -5));
        Assert.Equal(400, PageLayoutCalculator.StepLadder(100, 9));
        Assert.Equal(400, PageLayoutCalculator.StepLadder(400, 1));  // 端点【ST-11】
        Assert.Equal(25, PageLayoutCalculator.StepLadder(25, -1));   // 端点【ST-11】
        Assert.Equal(100, PageLayoutCalculator.StepLadder(100, 0));
    }

    [Fact]
    public void Ladder_Step_FromOffLadderFitZoom_TakesAdjacentRung()
    {
        // 适配档动态值 113.87：向上取首个严格更大档 150；向下取末个严格更小档 100
        Assert.Equal(150, PageLayoutCalculator.StepLadder(113.87, 1));
        Assert.Equal(100, PageLayoutCalculator.StepLadder(113.87, -1));
        // 90 的上一档是 100，下一档是 67
        Assert.Equal(100, PageLayoutCalculator.StepLadder(90, 1));
        Assert.Equal(67, PageLayoutCalculator.StepLadder(90, -1));
    }

    [Fact]
    public void DpiForZoom_SwitchesAt150Percent()
    {
        Assert.Equal(96, PageLayoutCalculator.DpiForZoom(25));
        Assert.Equal(96, PageLayoutCalculator.DpiForZoom(100));
        Assert.Equal(96, PageLayoutCalculator.DpiForZoom(149.9));
        Assert.Equal(150, PageLayoutCalculator.DpiForZoom(150));
        Assert.Equal(150, PageLayoutCalculator.DpiForZoom(400));
    }

    // ---------------------------------------------------------------- 适配档

    [Fact]
    public void FitWidth_ComputesZoomFromViewportAndClamps()
    {
        // 视口 800px 容纳 595pt 页宽（DIP 793.3）→ ≈100.84%
        Assert.Equal(100.84, PageLayoutCalculator.FitWidthZoomPercent(800, 595), 2);
        Assert.Equal(25, PageLayoutCalculator.FitWidthZoomPercent(100, 595));    // 下钳
        Assert.Equal(400, PageLayoutCalculator.FitWidthZoomPercent(100000, 595)); // 上钳
        Assert.Equal(100, PageLayoutCalculator.FitWidthZoomPercent(0, 595));     // 非法视口 → 安全默认
    }

    [Fact]
    public void FitPage_TakesTighterConstraint()
    {
        // 视口 800×600，页 595×842：宽度档 100.84、高度档 ≈53.44 → 取更紧的高度档
        Assert.Equal(53.44, PageLayoutCalculator.FitPageZoomPercent(800, 600, 595, 842), 2);
        // 高度充裕时取宽度档
        Assert.Equal(100.84, PageLayoutCalculator.FitPageZoomPercent(800, 2000, 595, 842), 2);
    }

    // ---------------------------------------------------------------- 缩放锚定

    [Fact]
    public void ZoomAnchorScroll_PreservesViewportCenter()
    {
        var single = new List<PageSize> { new(595, 842) };
        var oldOffsets = PageLayoutCalculator.ComputePageOffsets(single, 100);
        var oldHeights = PageLayoutCalculator.ComputePageHeights(single, 100);
        var newOffsets = PageLayoutCalculator.ComputePageOffsets(single, 200);
        var newHeights = PageLayoutCalculator.ComputePageHeights(single, 200);

        // 100% 时滚动 250（视口 500）→ 中心 500 → 200% 时中心应仍对应 1000 → 新滚动 750
        var target = PageLayoutCalculator.ZoomAnchorScroll(oldOffsets, oldHeights, 250, 500, newOffsets, newHeights);
        Assert.Equal(750, target, 6);
    }

    [Fact]
    public void ZoomAnchorScroll_MixedSizes_KeepsAnchorPageAndFraction()
    {
        var oldOffsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);
        var oldHeights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);
        var newOffsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 150);
        var newHeights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 150);

        // 中心落在第 1 页 40% 处
        var oldScroll = offsetsAt(1, oldOffsets) + oldHeights[1] * 0.4 - 400; // 视口 800
        var target = PageLayoutCalculator.ZoomAnchorScroll(oldOffsets, oldHeights, oldScroll, 800, newOffsets, newHeights);

        var expectedCenter = newOffsets[1] + newHeights[1] * 0.4;
        Assert.Equal(expectedCenter - 400, target, 4);

        static double offsetsAt(int i, double[] offsets) => offsets[i];
    }

    [Fact]
    public void ZoomAnchorScroll_ClampsAtDocumentBounds()
    {
        var single = new List<PageSize> { new(595, 842) };
        var oldOffsets = PageLayoutCalculator.ComputePageOffsets(single, 100);
        var oldHeights = PageLayoutCalculator.ComputePageHeights(single, 100);
        var newOffsets = PageLayoutCalculator.ComputePageOffsets(single, 200);
        var newHeights = PageLayoutCalculator.ComputePageHeights(single, 200);

        // 视口中心已超出末页（旧滚动极大）→ 新滚动钳到末页底 + 页间距 - 视口
        var target = PageLayoutCalculator.ZoomAnchorScroll(oldOffsets, oldHeights, 1e5, 500, newOffsets, newHeights);
        var max = newOffsets[^1] + newHeights[^1] + PageLayoutCalculator.PageGap - 500;
        Assert.Equal(Math.Max(0, max), target, 6);

        // 顶部：中心保留在页 0 的 22.27% 处 → 200% 时视口中心 = 500 → 滚动 250（不越界）
        Assert.Equal(250, PageLayoutCalculator.ZoomAnchorScroll(oldOffsets, oldHeights, 0, 500, newOffsets, newHeights), 6);
    }

    // ---------------------------------------------------------------- 页码解析

    [Theory]
    [InlineData("5", 12, 5)]
    [InlineData(" 7 ", 12, 7)]
    [InlineData("1", 12, 1)]
    [InlineData("12", 12, 12)]
    public void TryParsePageNumber_AcceptsValid(string text, int count, int expected)
    {
        Assert.True(PageLayoutCalculator.TryParsePageNumber(text, count, out var page));
        Assert.Equal(expected, page);
    }

    [Theory]
    [InlineData("0", 12)]
    [InlineData("13", 12)]
    [InlineData("-1", 12)]
    [InlineData("abc", 12)]
    [InlineData("", 12)]
    [InlineData(null, 12)]
    [InlineData("5", 0)]
    public void TryParsePageNumber_RejectsInvalid(string? text, int count)
    {
        Assert.False(PageLayoutCalculator.TryParsePageNumber(text, count, out var page));
        Assert.Equal(0, page);
    }
}
