// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 搜索面板视图模型测试：防抖/节流、命中列表投影、低质量标注、索引过渡态、无文本层降级。
// 全部用假索引提供器（ISearchIndexProvider），不依赖 PDFium 与 UI。
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Search;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.Search;

/// <summary>可桩替的搜索索引提供器：记录调用次数，可阻塞以便观测索引过渡态。</summary>
internal sealed class FakeSearchIndexProvider : ISearchIndexProvider
{
    private int _searchCalls;

    /// <summary>Search 被调用的次数（线程安全读）。</summary>
    public int SearchCalls => Volatile.Read(ref _searchCalls);

    public Func<string, string, IReadOnlyList<SearchHit>>? SearchImpl { get; set; }

    public bool TextLayerAvailable { get; set; } = true;

    public SearchIndexStatus Status { get; set; } = new(1, 1, 10, 100, true, false, true, 0);

    public Dictionary<int, string> PageTexts { get; } = [];

    /// <summary>非空时 Search 进入后阻塞，供测试观测「搜索在途」状态。</summary>
    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>Search 一进入即完成，通知测试已进入后台搜索。</summary>
    public TaskCompletionSource<bool>? SearchEntered { get; set; }

    public IReadOnlyList<SearchHit> Search(string docId, string query)
    {
        Interlocked.Increment(ref _searchCalls);
        SearchEntered?.TrySetResult(true);
        Gate?.Wait();
        return SearchImpl?.Invoke(docId, query) ?? Array.Empty<SearchHit>();
    }

    public SearchIndexStatus GetStatus(string docId) => Status;

    public bool IsTextLayerAvailable(string docId) => TextLayerAvailable;

    public string GetPageText(string docId, int pageIndex)
        => PageTexts.TryGetValue(pageIndex, out var text) ? text : string.Empty;
}

/// <summary>T8 SearchPanelViewModel 行为测试。</summary>
public sealed class SearchPanelViewModelTests
{
    private static SearchHit Hit(int pageIndex, int charStart, int length, bool lowQuality = false)
        => new(pageIndex, charStart, charStart + length,
            new PdfRect(10, 20, 12, 22), [new PdfRect(10, 20, 12, 22)], lowQuality);

    // ---------------------------------------------------------------- 防抖 / 节流

    [Fact]
    public async Task RapidQueryChanges_DebounceIntoSingleSearch()
    {
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => [Hit(0, 0, 2)] };
        using var vm = new SearchPanelViewModel(fake, "/doc.pdf", debounceMs: 60);

        vm.Query = "a";
        vm.Query = "ab";
        vm.Query = "abc";

        Assert.NotNull(vm.PendingSearchTask);
        await vm.PendingSearchTask!;

        Assert.Equal(1, fake.SearchCalls); // 前两次被防抖取消，未触发搜索
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task SequentialQueries_EachRunsAfterSettling()
    {
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => [Hit(0, 0, 1)] };
        using var vm = new SearchPanelViewModel(fake, "/doc.pdf", debounceMs: 20);

        vm.Query = "a";
        await vm.PendingSearchTask!;
        vm.Query = "ab";
        await vm.PendingSearchTask!;

        Assert.Equal(2, fake.SearchCalls);
    }

    [Fact]
    public async Task EmptyQuery_ClearsResultsWithoutSearchingAgain()
    {
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => [Hit(0, 0, 1)] };
        using var vm = new SearchPanelViewModel(fake, "/doc.pdf", debounceMs: 0);

        vm.Query = "abc";
        await vm.PendingSearchTask!;
        Assert.True(vm.HasSearched);
        Assert.Single(vm.Results);

        vm.Query = "";
        Assert.Empty(vm.Results);
        Assert.False(vm.HasSearched);
        Assert.Equal(1, fake.SearchCalls);
    }

    // ---------------------------------------------------------------- 命中列表投影

    [Fact]
    public async Task HitProjection_PageLabelExcerptAndSummary()
    {
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (_, _) => [Hit(2, 5, 3)],
            Status = new SearchIndexStatus(10, 10, 120, 1000, true, false, true, 0),
        };
        fake.PageTexts[2] = "abcdefghijKLMnopqrstuvwxyz"; // 命中 5..8 = "fgh"

        using var vm = new SearchPanelViewModel(fake, "/doc.pdf", debounceMs: 0);
        vm.Query = "fgh";
        await vm.PendingSearchTask!;

        var item = Assert.Single(vm.Results);
        Assert.Equal("第 3 页", item.PageLabel);
        Assert.Contains("fgh", item.Excerpt);
        Assert.False(item.IsLowQualityPage);
        Assert.Equal(1, vm.TotalHitCount);
        Assert.Contains("共 1 处命中", vm.ResultSummary);
        Assert.True(vm.HasResults);
    }

    [Fact]
    public async Task LowQualityHit_IsFlaggedForBadge()
    {
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => [Hit(0, 0, 1, lowQuality: true)] };
        using var vm = new SearchPanelViewModel(fake, "/garbage.pdf", debounceMs: 0);

        vm.Query = "x";
        await vm.PendingSearchTask!;

        Assert.True(Assert.Single(vm.Results).IsLowQualityPage);
    }

    [Fact]
    public async Task ResultsOverCap_AreTruncatedWithSummary()
    {
        var hits = Enumerable.Range(0, 250).Select(i => Hit(i % 500, 0, 1)).ToArray();
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => hits };
        using var vm = new SearchPanelViewModel(fake, "/big.pdf", debounceMs: 0);

        vm.Query = "x";
        await vm.PendingSearchTask!;

        Assert.Equal(SearchPanelViewModel.DefaultMaxResults, vm.Results.Count);
        Assert.Equal(250, vm.TotalHitCount);
        Assert.Contains("显示前 200 处", vm.ResultSummary);
    }

    [Fact]
    public async Task NoHitAfterSearch_ShowsEmptyState()
    {
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => Array.Empty<SearchHit>() };
        using var vm = new SearchPanelViewModel(fake, "/doc.pdf", debounceMs: 0);

        vm.Query = "zzz";
        await vm.PendingSearchTask!;

        Assert.Empty(vm.Results);
        Assert.True(vm.HasNoResults);
    }

    // ---------------------------------------------------------------- 索引过渡态

    [Fact]
    public async Task IndexingState_VisibleWhileSearchInFlight_ThenClears()
    {
        using var gate = new ManualResetEventSlim(false);
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (_, _) => [Hit(0, 0, 1)],
            Gate = gate,
            SearchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var vm = new SearchPanelViewModel(fake, "/big.pdf", debounceMs: 0);

        vm.Query = "x";
        await fake.SearchEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => vm.IsIndexing && vm.IsSearching);

        gate.Set();
        await vm.PendingSearchTask!;

        Assert.False(vm.IsIndexing);
        Assert.False(vm.IsSearching);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task PartialIndex_ShowsBudgetHint()
    {
        var fake = new FakeSearchIndexProvider
        {
            SearchImpl = (_, _) => [Hit(0, 0, 1)],
            Status = new SearchIndexStatus(500, 20, 2000, 1000, false, true, true, 3),
        };
        using var vm = new SearchPanelViewModel(fake, "/big.pdf", debounceMs: 0);

        vm.Query = "x";
        await vm.PendingSearchTask!;

        Assert.True(vm.HasStatusHint);
        Assert.Contains("部分索引", vm.StatusHint);
    }

    // ---------------------------------------------------------------- 无文本层降级

    [Fact]
    public async Task NoTextLayer_DisablesSearchAndRaisesOcr()
    {
        var fake = new FakeSearchIndexProvider
        {
            TextLayerAvailable = false,
            SearchImpl = (_, _) => Array.Empty<SearchHit>(),
        };
        using var vm = new SearchPanelViewModel(fake, "/scan.pdf", debounceMs: 0);

        var ocrRequested = false;
        vm.OcrRequested += (_, _) => ocrRequested = true;

        vm.Query = "hello";
        await vm.PendingSearchTask!;

        Assert.False(vm.IsTextLayerAvailable);
        Assert.True(vm.ShowNoTextLayer);

        vm.GoToOcrCommand.Execute(null);
        Assert.True(ocrRequested);
    }

    [Fact]
    public async Task Probe_ScannedDocument_MarksNoTextLayerWithoutTyping()
    {
        var fake = new FakeSearchIndexProvider { TextLayerAvailable = false };
        using var vm = new SearchPanelViewModel(fake, "/scan.pdf");

        vm.BeginProbe();
        Assert.NotNull(vm.PendingProbeTask);
        await vm.PendingProbeTask!;

        Assert.False(vm.IsTextLayerAvailable);
        Assert.True(vm.ShowNoTextLayer);
    }

    [Fact]
    public async Task Probe_TextDocument_KeepsSearchEnabled()
    {
        var fake = new FakeSearchIndexProvider { TextLayerAvailable = true };
        fake.PageTexts[0] = "有文本";
        using var vm = new SearchPanelViewModel(fake, "/text.pdf");

        vm.BeginProbe();
        await vm.PendingProbeTask!;

        Assert.True(vm.IsTextLayerAvailable);
        Assert.False(vm.ShowNoTextLayer);
    }

    // ---------------------------------------------------------------- 命中激活

    [Fact]
    public async Task ActivateHit_RaisesEventWithItem()
    {
        var fake = new FakeSearchIndexProvider { SearchImpl = (_, _) => [Hit(4, 0, 1)] };
        using var vm = new SearchPanelViewModel(fake, "/doc.pdf", debounceMs: 0);
        vm.Query = "x";
        await vm.PendingSearchTask!;

        SearchResultItem? activated = null;
        vm.HitActivated += (_, item) => activated = item;

        vm.ActivateHitCommand.Execute(vm.Results[0]);

        Assert.NotNull(activated);
        Assert.Equal(4, activated!.PageIndex);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void RequestClose_RaisesCloseEvent()
    {
        var fake = new FakeSearchIndexProvider();
        using var vm = new SearchPanelViewModel(fake, "/doc.pdf");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.RequestClose();

        Assert.True(closed);
    }

    // ---------------------------------------------------------------- 工具

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Xunit.Sdk.XunitException($"等待条件超时（{timeoutMs}ms）");
            await Task.Delay(10);
        }
    }
}
