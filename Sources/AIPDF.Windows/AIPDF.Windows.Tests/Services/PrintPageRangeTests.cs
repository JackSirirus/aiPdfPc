// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T3 打印范围解析测试（纯逻辑）：全部/当前页/自定义；非法（空/越界/逆序/格式）→ IsValid=false。
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class PrintPageRangeTests
{
    private const int Twelve = 12;

    // ---------------------------------------------------------------- 合法路径

    [Fact]
    public void Parse_All_ReturnsEveryZeroBasedPage()
    {
        var result = PrintPageRange.Parse("all", Twelve);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Equal(Enumerable.Range(0, Twelve).ToArray(), result.Pages);
    }

    [Fact]
    public void Parse_AllToken_IsCaseInsensitiveAndTrimmed()
    {
        var result = PrintPageRange.Parse("  ALL  ", Twelve);
        Assert.True(result.IsValid);
        Assert.Equal(Twelve, result.Pages.Count);
    }

    [Fact]
    public void Parse_Current_ReturnsCurrentPage()
    {
        var result = PrintPageRange.Parse("current", Twelve, currentPageIndex: 4);

        Assert.True(result.IsValid);
        Assert.Equal([4], result.Pages);
    }

    [Fact]
    public void Parse_CurrentPageToken_AliasReturnsCurrentPage()
    {
        var result = PrintPageRange.Parse("current-page", Twelve, currentPageIndex: 0);
        Assert.True(result.IsValid);
        Assert.Equal([0], result.Pages);
    }

    [Fact]
    public void Parse_Custom_RangeAndSingle()
    {
        var result = PrintPageRange.Parse("1-3,5", Twelve);

        Assert.True(result.IsValid);
        Assert.Equal([0, 1, 2, 4], result.Pages); // 0 基、升序
    }

    [Fact]
    public void Parse_Custom_DeduplicatesAndMergesOverlap()
    {
        var result = PrintPageRange.Parse("1-3,2-4,3", Twelve);

        Assert.True(result.IsValid);
        Assert.Equal([0, 1, 2, 3], result.Pages);
    }

    [Fact]
    public void Parse_Custom_ToleratesWhitespace()
    {
        var result = PrintPageRange.Parse(" 1 - 3 , 5 ", Twelve);

        Assert.True(result.IsValid);
        Assert.Equal([0, 1, 2, 4], result.Pages);
    }

    [Theory]
    [InlineData("1", new[] { 0 })]                    // 首页
    [InlineData("12", new[] { 11 })]                  // 末页
    [InlineData("1-12", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })] // 全范围
    public void Parse_Custom_Boundaries_AreValid(string spec, int[] expected)
    {
        var result = PrintPageRange.Parse(spec, Twelve);
        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Pages);
    }

    // ---------------------------------------------------------------- 非法：空

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_IsInvalid(string? spec)
    {
        var result = PrintPageRange.Parse(spec, Twelve);
        Assert.False(result.IsValid);
        Assert.Empty(result.Pages);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Theory]
    [InlineData("1,,3")]  // 中间空项
    [InlineData("1,")]    // 尾随逗号
    [InlineData(",1")]    // 前导逗号
    public void Parse_Custom_EmptyToken_IsInvalid(string spec)
    {
        var result = PrintPageRange.Parse(spec, Twelve);
        Assert.False(result.IsValid);
    }

    // ---------------------------------------------------------------- 非法：越界

    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("1-13")]
    [InlineData("12-13")]
    public void Parse_Custom_OutOfRange_IsInvalid(string spec)
    {
        var result = PrintPageRange.Parse(spec, Twelve);
        Assert.False(result.IsValid);
        Assert.Equal(PrintPageRange.ErrorOutOfRange, result.Error);
    }

    [Fact]
    public void Parse_Current_IndexOutOfRange_IsInvalid()
    {
        Assert.False(PrintPageRange.Parse("current", Twelve, currentPageIndex: 12).IsValid);
        Assert.False(PrintPageRange.Parse("current", Twelve, currentPageIndex: -1).IsValid);
    }

    // ---------------------------------------------------------------- 非法：逆序

    [Fact]
    public void Parse_Custom_ReversedRange_IsInvalid()
    {
        var result = PrintPageRange.Parse("3-1", Twelve);
        Assert.False(result.IsValid);
        Assert.Equal(PrintPageRange.ErrorReversed, result.Error);
    }

    // ---------------------------------------------------------------- 非法：格式

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1-2-3")]
    [InlineData("-3")]
    [InlineData("1-")]
    [InlineData("1a")]
    [InlineData("0x2")]
    public void Parse_Custom_Malformed_IsInvalid(string spec)
    {
        var result = PrintPageRange.Parse(spec, Twelve);
        Assert.False(result.IsValid);
    }

    // ---------------------------------------------------------------- 空文档：任何输入均非法

    [Theory]
    [InlineData("all")]
    [InlineData("current")]
    [InlineData("1-3")]
    public void Parse_EmptyDocument_AlwaysInvalid(string spec)
    {
        Assert.False(PrintPageRange.Parse(spec, pageCount: 0).IsValid);
        Assert.False(PrintPageRange.All(0).IsValid);
        Assert.False(PrintPageRange.CurrentPage(0, 0).IsValid);
    }

    [Fact]
    public void Parse_NegativePageCount_AlwaysInvalid()
    {
        Assert.False(PrintPageRange.Parse("1", pageCount: -5).IsValid);
        Assert.False(PrintPageRange.All(-5).IsValid);
    }
}
