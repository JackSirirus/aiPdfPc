// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T14 跨文档搜索测试：范围切换、结果带文档归属、按文档分组、截断但总数完整。
// 全部用假索引提供器（ISearchIndexProvider），不依赖 PDFium 与 UI。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Search;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.Search;

/// <summary>V4 T14 跨文档搜索（SearchPanelViewModel）。</summary>
public sealed class SearchCrossDocumentTests
{
    private static SearchHit Hit(int pageIndex, int charStart, int length)
        => new(pageIndex, charStart, charStart + length,
            new PdfRect(10, 20, 12, 22), [new PdfRect(10, 20, 12, 22)], false);

    [Fact]
    public async Task AllOpenDocuments_GroupsResultsByDocument_AndTagsDocId()
    {
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (docId, _) => docId switch
            {
                "/a.pdf" => [Hit(0, 0, 2), Hit(1, 5, 2)],
                "/b.pdf" => [Hit(2, 0, 2)],
                _ => [],
            },
        };
        using var viewModel = new SearchPanelViewModel(fake, "/a.pdf", debounceMs: 0);
        viewModel.DocumentsProvider = () => [("/a.pdf", "A.pdf"), ("/b.pdf", "B.pdf")];

        viewModel.Scope = SearchScope.AllOpenDocuments;
        viewModel.Query = "x";
        await viewModel.PendingSearchTask!;

        // 命中总数是跨文档之和
        Assert.Equal(3, viewModel.TotalHitCount);
        Assert.Equal(3, viewModel.Results.Count);
        // 每条命中带**所属文档**——宿主据此切标签页（验收③的前提）
        Assert.All(viewModel.Results, item => Assert.False(string.IsNullOrEmpty(item.DocId)));
        Assert.Equal(2, viewModel.Results.Count(item => item.DocId == "/a.pdf"));

        // 按文档分组（验收②）：当前文档在首位，组头含标题与命中数
        Assert.True(viewModel.IsCrossDocument);
        Assert.Equal(2, viewModel.Groups.Count);
        Assert.Equal("/a.pdf", viewModel.Groups[0].DocId);
        Assert.Equal("A.pdf（2 处）", viewModel.Groups[0].Header);
        Assert.Equal("/b.pdf", viewModel.Groups[1].DocId);
        Assert.Single(viewModel.Groups[1].Items);
    }

    [Fact]
    public async Task CurrentDocumentScope_KeepsFlatBehaviour()
    {
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (docId, _) => docId == "/a.pdf" ? [Hit(0, 0, 2)] : [Hit(1, 0, 2), Hit(2, 0, 2)],
        };
        using var viewModel = new SearchPanelViewModel(fake, "/a.pdf", debounceMs: 0);
        viewModel.DocumentsProvider = () => [("/a.pdf", "A.pdf"), ("/b.pdf", "B.pdf")];

        // 默认仅当前文档：不得搜索其它文档（验收①的默认侧）
        Assert.True(viewModel.IsSingleDocument);
        viewModel.Query = "x";
        await viewModel.PendingSearchTask!;

        Assert.Equal(1, viewModel.TotalHitCount);
        Assert.Single(viewModel.Groups);
        Assert.Equal("/a.pdf", viewModel.Groups[0].DocId);
    }

    [Fact]
    public async Task ScopeSwitch_RerunsSearchImmediately()
    {
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (docId, _) => docId == "/a.pdf" ? [Hit(0, 0, 2)] : [Hit(0, 0, 2), Hit(1, 0, 2)],
        };
        using var viewModel = new SearchPanelViewModel(fake, "/a.pdf", debounceMs: 0);
        viewModel.DocumentsProvider = () => [("/a.pdf", "A.pdf"), ("/b.pdf", "B.pdf")];

        viewModel.Query = "x";
        await viewModel.PendingSearchTask!;
        Assert.Equal(1, viewModel.TotalHitCount);

        // 范围变化立即按当前查询重跑（用户不必重新输入）
        viewModel.Scope = SearchScope.AllOpenDocuments;
        Assert.NotNull(viewModel.PendingSearchTask);
        await viewModel.PendingSearchTask!;

        Assert.Equal(3, viewModel.TotalHitCount);
        Assert.True(viewModel.IsCrossDocument);
    }

    [Fact]
    public async Task AllOpenDocuments_TruncatesDisplay_ButCountsAllHits()
    {
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (_, _) => [.. Enumerable.Range(0, 5).Select(index => Hit(index, 0, 2))],
        };
        using var viewModel = new SearchPanelViewModel(fake, "/a.pdf", debounceMs: 0, maxResults: 3);
        viewModel.DocumentsProvider = () => [("/a.pdf", "A.pdf"), ("/b.pdf", "B.pdf")];

        viewModel.Scope = SearchScope.AllOpenDocuments;
        viewModel.Query = "x";
        await viewModel.PendingSearchTask!;

        Assert.Equal(10, viewModel.TotalHitCount);   // 总数统计全部文档
        Assert.Equal(3, viewModel.Results.Count);    // 展示按上限截断
        Assert.Equal("显示前 3 处（共 10 处命中）", viewModel.ResultSummary);
    }

    [Fact]
    public void SearchResultProjector_Project_CarriesDocumentIdentity()
    {
        var item = SearchResultProjector.Project(Hit(3, 0, 2), "text", 10, "/a.pdf", "A.pdf");

        Assert.Equal("/a.pdf", item.DocId);
        Assert.Equal("A.pdf", item.DocumentTitle);
        Assert.Equal("第 4 页", item.PageLabel);

        // 单文档投影（既有调用）不受影响：文档字段为空
        var legacy = SearchResultProjector.Project(Hit(0, 0, 2), "text", 10);
        Assert.Empty(legacy.DocId);
        Assert.Empty(legacy.DocumentTitle);
    }
}
