// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T9（M8）page↔group 映射纯逻辑测试：单页恒等、对开封面单列/不留封面、奇偶页、空文档、越界钳制、往返。
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>PageGroupMap 纯函数覆盖（合成页数，不读任何真实文档）。</summary>
public sealed class PageGroupMapTests
{
    [Fact]
    public void Single_IsIdentityMapping()
    {
        var map = PageGroupMap.Single(5);
        Assert.True(map.IsIdentity);
        Assert.False(map.SpreadEnabled);
        Assert.Equal(5, map.PageCount);
        Assert.Equal(5, map.GroupCount);

        for (var p = 0; p < 5; p++)
        {
            Assert.Equal(p, map.PageToGroup(p));
            Assert.Equal((p, p), map.GroupToPages(p));
            Assert.Equal(0, map.PageIndexInGroup(p));
        }
    }

    [Fact]
    public void Spread_CoverAlone_OddPageCount_GroupsCorrectly()
    {
        var map = PageGroupMap.Spread(5, coverPageAlone: true);
        Assert.True(map.SpreadEnabled);
        Assert.False(map.IsIdentity);
        Assert.Equal(3, map.GroupCount);

        Assert.Equal((0, 0), map.GroupToPages(0));
        Assert.Equal((1, 2), map.GroupToPages(1));
        Assert.Equal((3, 4), map.GroupToPages(2));

        Assert.Equal(0, map.PageToGroup(0));
        Assert.Equal(1, map.PageToGroup(1));
        Assert.Equal(1, map.PageToGroup(2));
        Assert.Equal(2, map.PageToGroup(3));
        Assert.Equal(2, map.PageToGroup(4));

        Assert.Equal(0, map.PageIndexInGroup(0));
        Assert.Equal(0, map.PageIndexInGroup(1));
        Assert.Equal(1, map.PageIndexInGroup(2));
    }

    [Fact]
    public void Spread_CoverAlone_EvenPageCount_GroupsCorrectly()
    {
        var map = PageGroupMap.Spread(4, coverPageAlone: true);
        Assert.Equal(3, map.GroupCount);
        Assert.Equal((0, 0), map.GroupToPages(0));
        Assert.Equal((1, 2), map.GroupToPages(1));
        Assert.Equal((3, 3), map.GroupToPages(2)); // 末页单列
        Assert.Equal(2, map.PageToGroup(3));
    }

    [Fact]
    public void Spread_CoverAlone_SinglePage_IsSingleGroup()
    {
        var map = PageGroupMap.Spread(1, coverPageAlone: true);
        Assert.Equal(1, map.GroupCount);
        Assert.Equal((0, 0), map.GroupToPages(0));
    }

    [Fact]
    public void Spread_NoCover_PairsFromFirstPage()
    {
        var map = PageGroupMap.Spread(5, coverPageAlone: false);
        Assert.Equal(3, map.GroupCount);
        Assert.Equal((0, 1), map.GroupToPages(0));
        Assert.Equal((2, 3), map.GroupToPages(1));
        Assert.Equal((4, 4), map.GroupToPages(2)); // 末页单列
        Assert.Equal(0, map.PageToGroup(0));
        Assert.Equal(0, map.PageToGroup(1));
        Assert.Equal(1, map.PageToGroup(2));
        Assert.Equal(1, map.PageToGroup(3));
        Assert.Equal(2, map.PageToGroup(4));
    }

    [Fact]
    public void Spread_NoCover_SinglePage_IsSingleGroup()
    {
        var map = PageGroupMap.Spread(1, coverPageAlone: false);
        Assert.Equal(1, map.GroupCount);
        Assert.Equal((0, 0), map.GroupToPages(0));
    }

    [Fact]
    public void EmptyDocument_HasNoGroups()
    {
        Assert.Equal(0, PageGroupMap.Single(0).GroupCount);
        Assert.Equal(0, PageGroupMap.Spread(0, true).GroupCount);
        Assert.Equal(-1, PageGroupMap.Single(0).PageToGroup(0));
        Assert.Equal((0, -1), PageGroupMap.Spread(0, true).GroupToPages(0));
        Assert.Equal(-1, PageGroupMap.Spread(0, true).PageIndexInGroup(0));
    }

    [Fact]
    public void OutOfRange_ClampsToBounds()
    {
        var map = PageGroupMap.Spread(5, coverPageAlone: true);
        Assert.Equal(0, map.PageToGroup(-10));
        Assert.Equal(2, map.PageToGroup(99));
        Assert.Equal((0, 0), map.GroupToPages(-1));
        Assert.Equal((3, 4), map.GroupToPages(99));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Spread_PageToGroup_RoundTripsThroughGroupToPages(int pageCount, bool coverAlone)
    {
        var map = PageGroupMap.Spread(pageCount, coverAlone);
        for (var p = 0; p < pageCount; p++)
        {
            var group = map.PageToGroup(p);
            var (first, last) = map.GroupToPages(group);
            Assert.InRange(p, first, last);
            Assert.Equal(group, map.PageToGroup(first));
            Assert.Equal(group, map.PageToGroup(last));
        }
    }
}
