// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// V1 工具通道路由测试。权威基准 = 设计/v1.0-阅读器/prd/01-工具通道映射表.md：
/// 右抽屉 2（organize / pdfa）+ 起始页 6 + 其余 24 临时标签 = 32。
/// id 显式列出，并验证路由不依赖 <see cref="Tool.Multiple"/>。
/// </summary>
public sealed class ToolChannelsTests
{
    private static readonly string[] ExpectedRightDrawer = ["organize", "pdfa"];

    private static readonly string[] ExpectedStartPage =
        ["scan", "images_to_pdf", "word_to_pdf", "ppt_to_pdf", "excel_to_pdf", "html_to_pdf"];

    private static readonly string[] ExpectedTempTab =
    [
        "merge", "split", "remove", "extract", "compress", "repair", "ocr",
        "pdf_to_images", "pdf_to_word", "pdf_to_ppt", "pdf_to_excel", "rotate",
        "numbers", "watermark", "crop", "edit", "forms", "unlock", "protect",
        "sign", "redact", "compare", "markdown", "workflow",
    ];

    [Fact]
    public void ChannelFor_RightDrawer_IsExactlyOrganizeAndPdfa()
    {
        var actual = IdsWith(ToolChannel.RightDrawer);

        Assert.Equal(ExpectedRightDrawer.OrderBy(id => id), actual);
    }

    [Fact]
    public void ChannelFor_StartPage_IsExactlyTheSixNonPdfInputTools()
    {
        var actual = IdsWith(ToolChannel.StartPage);

        Assert.Equal(ExpectedStartPage.OrderBy(id => id), actual);
    }

    [Fact]
    public void ChannelFor_Remaining24Tools_AreTempTab()
    {
        var actual = IdsWith(ToolChannel.TempTab);

        Assert.Equal(ExpectedTempTab.OrderBy(id => id), actual);
    }

    [Fact]
    public void EveryCatalogTool_RoutesToExactlyOneChannel_AndAll32AccountedFor()
    {
        var tools = ToolCatalog.Load();
        var byChannel = tools
            .GroupBy(ToolChannels.ChannelFor)
            .ToDictionary(group => group.Key, group => group.Select(t => t.Id).ToList());

        Assert.Equal(32, tools.Count);
        Assert.Equal(ExpectedRightDrawer.Length, byChannel[ToolChannel.RightDrawer].Count);
        Assert.Equal(ExpectedStartPage.Length, byChannel[ToolChannel.StartPage].Count);
        Assert.Equal(ExpectedTempTab.Length, byChannel[ToolChannel.TempTab].Count);

        var union = byChannel.Values.SelectMany(ids => ids).ToList();
        Assert.Equal(32, union.Count); // 无工具落入两个通道（分组拆解后总数不变）
        Assert.Equal(32, union.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            tools.Select(t => t.Id).OrderBy(id => id),
            union.OrderBy(id => id));
    }

    [Fact]
    public void RoutesToRightDrawer_MatchesChannelFor_AndDoesNotFollowMultiple()
    {
        var tools = ToolCatalog.Load();

        Assert.Equal(
            ExpectedRightDrawer.OrderBy(id => id),
            tools.Where(ToolChannels.RoutesToRightDrawer).Select(t => t.Id).OrderBy(id => id));

        // 映射表与 Tool.Multiple 不一致的证据：pdfa Multiple=true 仍走右抽屉，forms Multiple=false 仍走临时标签。
        var pdfa = tools.Single(t => t.Id == "pdfa");
        var forms = tools.Single(t => t.Id == "forms");
        Assert.True(pdfa.Multiple);
        Assert.False(forms.Multiple);
        Assert.Equal(ToolChannel.RightDrawer, ToolChannels.ChannelFor(pdfa));
        Assert.Equal(ToolChannel.TempTab, ToolChannels.ChannelFor(forms));
    }

    private static IReadOnlyList<string> IdsWith(ToolChannel channel)
    {
        var tools = ToolCatalog.Load();
        return [.. tools.Where(t => ToolChannels.ChannelFor(t) == channel).Select(t => t.Id).OrderBy(id => id)];
    }
}
