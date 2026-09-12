// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T9 对开布局纯函数测试：组偏移/宽高（混合页尺寸）、组内页偏移、组内命中映射、适配宽度/页面（含页间距扣减）。
using System.Collections.Generic;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>PageLayoutCalculator 对开（spread）纯函数覆盖。合成尺寸，不读真实文档。</summary>
public sealed class PageLayoutCalculatorSpreadTests
{
    private const double Dip = PageLayoutCalculator.DpiScale; // 4/3

    /// <summary>混合页尺寸：A4 竖版 595×842、正方形 400×400、加长 800×1200。</summary>
    private static readonly List<PageSize> MixedSizes =
    [
        new(595, 842),
        new(400, 400),
        new(800, 1200),
    ];

    [Fact]
    public void ComputeGroups_CoverAlone_MixedSizes_UsesMaxHeightAndSummedWidth()
    {
        var map = PageGroupMap.Spread(MixedSizes.Count, coverPageAlone: true);
        Assert.Equal(2, map.GroupCount); // (0) 与 (1,2)

        var heights = PageLayoutCalculator.ComputeGroupHeights(MixedSizes, 100, map);
        var offsets = PageLayoutCalculator.ComputeGroupOffsets(MixedSizes, 100, map);
        var widths = PageLayoutCalculator.ComputeGroupWidths(MixedSizes, 100, map);

        Assert.Equal(842 * Dip, heights[0], 6);
        Assert.Equal(1200 * Dip, heights[1], 6); // 组内页高最大值

        Assert.Equal(0, offsets[0]);
        Assert.Equal(842 * Dip + PageLayoutCalculator.PageGap, offsets[1], 6);

        Assert.Equal(595 * Dip, widths[0], 6);
        Assert.Equal((400 + 800) * Dip + PageLayoutCalculator.PageGap, widths[1], 6); // 两页宽 + 组内页间距

        var content = PageLayoutCalculator.ComputeGroupsContentHeight(MixedSizes, 100, map);
        Assert.Equal(offsets[1] + heights[1] + PageLayoutCalculator.PageGap, content, 6);
    }

    [Fact]
    public void ComputeGroups_NoCover_MixedSizes()
    {
        var sizes = new List<PageSize> { new(500, 700), new(600, 800), new(700, 500) };
        var map = PageGroupMap.Spread(sizes.Count, coverPageAlone: false);
        Assert.Equal(2, map.GroupCount); // (0,1) 与 (2)

        var heights = PageLayoutCalculator.ComputeGroupHeights(sizes, 100, map);
        var widths = PageLayoutCalculator.ComputeGroupWidths(sizes, 100, map);

        Assert.Equal(800 * Dip, heights[0], 6);
        Assert.Equal(500 * Dip, heights[1], 6);
        Assert.Equal((500 + 600) * Dip + PageLayoutCalculator.PageGap, widths[0], 6);
        Assert.Equal(700 * Dip, widths[1], 6);
    }

    [Fact]
    public void ComputeGroups_ScalesWithZoom()
    {
        var map = PageGroupMap.Spread(MixedSizes.Count, coverPageAlone: true);
        var at100 = PageLayoutCalculator.ComputeGroupHeights(MixedSizes, 100, map);
        var at200 = PageLayoutCalculator.ComputeGroupHeights(MixedSizes, 200, map);
        for (var i = 0; i < at100.Length; i++) Assert.Equal(at100[i] * 2, at200[i], 6);
    }

    [Fact]
    public void SingleMode_GroupsEqualPages()
    {
        var map = PageGroupMap.Single(MixedSizes.Count);
        var heights = PageLayoutCalculator.ComputeGroupHeights(MixedSizes, 100, map);
        var pageHeights = PageLayoutCalculator.ComputePageHeights(MixedSizes, 100);
        var offsets = PageLayoutCalculator.ComputeGroupOffsets(MixedSizes, 100, map);
        var pageOffsets = PageLayoutCalculator.ComputePageOffsets(MixedSizes, 100);

        Assert.Equal(pageHeights, heights);
        Assert.Equal(pageOffsets, offsets);
        Assert.Equal(0, PageLayoutCalculator.PageXWithinGroup(2, MixedSizes, 100, map), 6);
    }

    [Fact]
    public void PageXWithinGroup_CoverAlone_RightPageOffsetByLeftWidthPlusGap()
    {
        var map = PageGroupMap.Spread(MixedSizes.Count, coverPageAlone: true);
        Assert.Equal(0, PageLayoutCalculator.PageXWithinGroup(0, MixedSizes, 100, map), 6); // 封面单列
        Assert.Equal(0, PageLayoutCalculator.PageXWithinGroup(1, MixedSizes, 100, map), 6); // 左页
        Assert.Equal(400 * Dip + PageLayoutCalculator.PageGap, PageLayoutCalculator.PageXWithinGroup(2, MixedSizes, 100, map), 6); // 右页
    }

    [Fact]
    public void MaxGroupContentWidthAndMultiPageFlags()
    {
        var map = PageGroupMap.Spread(MixedSizes.Count, coverPageAlone: true);
        Assert.Equal(1200, PageLayoutCalculator.MaxGroupContentWidthPt(MixedSizes, map)); // 400+800
        Assert.Equal(1200, PageLayoutCalculator.MaxGroupContentHeightPt(MixedSizes, map));
        Assert.True(PageLayoutCalculator.HasMultiPageGroup(map));

        Assert.False(PageLayoutCalculator.HasMultiPageGroup(PageGroupMap.Single(3)));
        Assert.Equal(800, PageLayoutCalculator.MaxGroupContentWidthPt(MixedSizes, PageGroupMap.Single(3)));
    }

    [Fact]
    public void GroupNavigationWrappers_MatchGenericSemantics()
    {
        var offsets = new double[] { 0, 100, 210 };
        var heights = new double[] { 100, 110, 50 };

        Assert.Equal(0, PageLayoutCalculator.GroupAtPoint(offsets, 5));
        Assert.Equal(1, PageLayoutCalculator.GroupAtPoint(offsets, 105));
        Assert.Equal(2, PageLayoutCalculator.GroupAtPoint(offsets, 1e5));

        Assert.Equal(
            PageLayoutCalculator.VisibleRange(offsets, heights, 50, 120, 1),
            PageLayoutCalculator.VisibleGroupRange(offsets, heights, 50, 120, 1));

        Assert.Equal(
            PageLayoutCalculator.CurrentPageByMaxOverlap(offsets, heights, 50, 120),
            PageLayoutCalculator.CurrentGroupByMaxOverlap(offsets, heights, 50, 120));
    }

    [Fact]
    public void HitPageInGroup_MapsGapAndPagesToLocalCoords()
    {
        var widths = new List<double> { 100, 200 };
        var heights = new List<double> { 300, 400 };

        var left = PageLayoutCalculator.HitPageInGroup(widths, heights, 10, 10);
        Assert.True(left.Inside);
        Assert.Equal(0, left.PageOffset);
        Assert.Equal(10, left.LocalX, 6);
        Assert.Equal(10, left.LocalY, 6);

        var right = PageLayoutCalculator.HitPageInGroup(widths, heights, 130, 20);
        Assert.True(right.Inside);
        Assert.Equal(1, right.PageOffset);
        Assert.Equal(14, right.LocalX, 6); // 130 - (100 + 16)
        Assert.Equal(20, right.LocalY, 6);

        // 组内页间距（100..116）：不 Inside，但按最近页给出页序号
        var gap = PageLayoutCalculator.HitPageInGroup(widths, heights, 108, 10);
        Assert.False(gap.Inside);
        Assert.InRange(gap.PageOffset, 0, 1);

        // 左页下方空白（页高 300，点在 y=350）：不 Inside，局部 y 被钳制到页高
        var below = PageLayoutCalculator.HitPageInGroup(widths, heights, 10, 350);
        Assert.False(below.Inside);
        Assert.Equal(0, below.PageOffset);
        Assert.Equal(300, below.LocalY, 6);
    }

    [Fact]
    public void HitPageInGroup_Empty_ReturnsMinusOne()
    {
        var hit = PageLayoutCalculator.HitPageInGroup(new List<double>(), new List<double>(), 1, 1);
        Assert.False(hit.Inside);
        Assert.Equal(-1, hit.PageOffset);
    }

    [Fact]
    public void FitWidth_ExtraDipReservesGroupGap()
    {
        // 不加预留（单页语义）
        Assert.Equal(100.84, PageLayoutCalculator.FitWidthZoomPercent(800, 595), 2);
        // 预留一个 16px 页间距（对开两页组）：略小
        var withGap = PageLayoutCalculator.FitWidthZoomPercent(800, 595, PageLayoutCalculator.PageGap);
        var expected = (800 - PageLayoutCalculator.PageGap) / (595 * Dip) * 100;
        Assert.Equal(expected, withGap, 6);
        Assert.True(withGap < PageLayoutCalculator.FitWidthZoomPercent(800, 595));
    }

    [Fact]
    public void FitPage_ExtraDipReservesGroupGap()
    {
        // 视口高度充裕 → 宽度为约束；预留一个页间距后宽度档变小
        var plain = PageLayoutCalculator.FitPageZoomPercent(800, 2000, 595, 842);
        var reserved = PageLayoutCalculator.FitPageZoomPercent(800, 2000, 595, 842, PageLayoutCalculator.PageGap, 0);
        var expected = (800 - PageLayoutCalculator.PageGap) / (595 * Dip) * 100;
        Assert.Equal(expected, reserved, 6);
        Assert.True(reserved < plain);
    }
}
