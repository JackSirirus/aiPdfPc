// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 全文搜索面板视图模型（纯 VM，可脱离 UI / PDFium 测试）：
//   输入防抖（默认 280ms，250-300ms 区间）→ 后台线程消费 ISearchIndexProvider（首次搜索构建整册索引，
//   UI 线程零阻塞）→ 结果回传 UI 线程装配命中列表（页码 + 摘录 + 「提取质量低」标注）。
//   状态：索引中过渡态【ST-16】、部分索引提示（SearchIndexStatus）、无文本层降级【ST-15/D12】。
// 事件：HitActivated（宿主跳页 + 页内高亮）、OcrRequested（去 OCR 通道，复用 T7 工具流）、CloseRequested（✕）。
using System.Collections.ObjectModel;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.ViewModels;

/// <summary>命中列表条目（已投影为可直接绑定的展示模型）。</summary>
public sealed class SearchResultItem
{
    public SearchResultItem(int pageIndex, int charStart, int charEnd, IReadOnlyList<PdfRect> rects,
        PdfRect boundingRect, bool isLowQualityPage, string excerpt)
    {
        PageIndex = pageIndex;
        CharStart = charStart;
        CharEnd = charEnd;
        Rects = rects;
        BoundingRect = boundingRect;
        IsLowQualityPage = isLowQualityPage;
        Excerpt = excerpt;
    }

    /// <summary>命中页（0 基）。</summary>
    public int PageIndex { get; }

    public int CharStart { get; }

    public int CharEnd { get; }

    /// <summary>该命中按行拆分的矩形（PDF 点，左上原点；页内高亮消费）。</summary>
    public IReadOnlyList<PdfRect> Rects { get; }

    /// <summary>命中包围盒（多行命中；Rects 为空时高亮兜底）。</summary>
    public PdfRect BoundingRect { get; }

    /// <summary>所在页被乱码率启发式标记为低质量文本层【D12】→ UI 显示「提取质量低」徽标。</summary>
    public bool IsLowQualityPage { get; }

    /// <summary>命中上下文摘录（含首尾省略号）。</summary>
    public string Excerpt { get; }

    /// <summary>页码标签（1 基，展示用）。</summary>
    public string PageLabel => $"第 {PageIndex + 1} 页";
}

/// <summary>命中 → 展示模型的纯投影（摘录截取可单测）。</summary>
public static class SearchResultProjector
{
    /// <summary>把服务层 <see cref="SearchHit"/> 投影为命中列表条目。</summary>
    public static SearchResultItem Project(SearchHit hit, string? pageText, int contextChars)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var excerpt = BuildExcerpt(pageText, hit.CharStart, hit.CharEnd, contextChars);
        return new SearchResultItem(hit.PageIndex, hit.CharStart, hit.CharEnd, hit.LineRects,
            hit.Rect, hit.IsLowQualityPage, excerpt);
    }

    /// <summary>取命中区间前后 <paramref name="contextChars"/> 个字符的摘录；换行折为空格，越界钳制。</summary>
    public static string BuildExcerpt(string? text, int charStart, int charEnd, int contextChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var context = Math.Max(0, contextChars);
        var start = Math.Clamp(charStart, 0, text.Length);
        var end = Math.Clamp(charEnd, start, text.Length);
        var from = Math.Max(0, start - context);
        var to = Math.Min(text.Length, end + context);
        if (to <= from) return string.Empty;

        var snippet = text[from..to].Replace('\r', ' ').Replace('\n', ' ').Trim();
        var prefix = from > 0 ? "…" : string.Empty;
        var suffix = to < text.Length ? "…" : string.Empty;
        return prefix + snippet + suffix;
    }
}

/// <summary>
/// 搜索面板状态机 + 异步搜索管线。构造须在 UI 线程（捕获 SynchronizationContext 回传结果；
/// 测试/无 UI 环境同步上下文为 null 时内联执行）。
/// </summary>
public partial class SearchPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>输入防抖时长（ms）：PRD 3.3 建议 250-300ms。</summary>
    public const int DefaultDebounceMs = 280;

    /// <summary>命中列表上限（服务返回全量；UI 层截断，防 500 页大结果集撑爆列表）。</summary>
    public const int DefaultMaxResults = 200;

    /// <summary>摘录上下文窗口（命中区间两侧各取字符数）。</summary>
    public const int ExcerptContextChars = 40;

    private readonly ISearchIndexProvider _index;
    private readonly int _debounceMs;
    private readonly int _maxResults;
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    /// <summary>点击命中：宿主跳页并在 ReaderView 绘制页内高亮。</summary>
    public event EventHandler<SearchResultItem>? HitActivated;

    /// <summary>无文本层文档的「去 OCR」：宿主复用 T7 工具流打开 ocr 工具。</summary>
    public event EventHandler? OcrRequested;

    /// <summary>面板 ✕：宿主退出搜索并恢复原面板。</summary>
    public event EventHandler? CloseRequested;

    public SearchPanelViewModel(ISearchIndexProvider index, string docId,
        int debounceMs = DefaultDebounceMs, int maxResults = DefaultMaxResults)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentOutOfRangeException.ThrowIfNegative(debounceMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        _index = index;
        DocId = docId;
        _debounceMs = debounceMs;
        _maxResults = maxResults;
        Results.CollectionChanged += (_, _) => NotifyResultStateChanged();
    }

    /// <summary>挂接的文档标识（与阅读标签 docId 一致 = 规范化路径）。</summary>
    public string DocId { get; }

    /// <summary>防抖时长（测试可读）。</summary>
    public int DebounceMs => _debounceMs;

    /// <summary>命中列表上限（测试可读）。</summary>
    public int MaxResults => _maxResults;

    /// <summary>命中列表（已截断到上限）。</summary>
    public ObservableCollection<SearchResultItem> Results { get; } = [];

    /// <summary>在途搜索任务（测试等待；应用内不需要）。</summary>
    internal Task? PendingSearchTask { get; private set; }

    /// <summary>在途文本层探测任务（测试等待）。</summary>
    internal Task? PendingProbeTask { get; private set; }

    // ------------------------------------------------------------------ 绑定状态

    [ObservableProperty]
    private string _query = "";

    /// <summary>搜索在途（含防抖等待后的后台搜索）。</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>索引构建中（首次搜索承担整册索引【ST-16】）→ UI 显示「索引中…」。</summary>
    [ObservableProperty]
    private bool _isIndexing;

    /// <summary>文本层可用；false → 搜索框置灰 + 「去 OCR」【ST-15】。</summary>
    [ObservableProperty]
    private bool _isTextLayerAvailable = true;

    /// <summary>状态提示（部分索引 / 预算超限 / 低质量页计数 / 错误）。</summary>
    [ObservableProperty]
    private string? _statusHint;

    /// <summary>是否已完成过一次搜索（空结果态只在搜索后显示）。</summary>
    [ObservableProperty]
    private bool _hasSearched;

    /// <summary>命中总数（未截断）。</summary>
    [ObservableProperty]
    private int _totalHitCount;

    /// <summary>结果摘要（「共 N 处命中」/「显示前 200 处（共 N 处命中）」）。</summary>
    [ObservableProperty]
    private string _resultSummary = "";

    /// <summary>当前激活命中索引（-1 = 无），供列表选中态。</summary>
    [ObservableProperty]
    private int _selectedIndex = -1;

    /// <summary>是否有命中。</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>搜索后无命中的空态（文本层可用时才提示「无结果」）。</summary>
    public bool HasNoResults => HasSearched && !IsSearching && Results.Count == 0 && IsTextLayerAvailable;

    /// <summary>无文本层降级态【ST-15】。</summary>
    public bool ShowNoTextLayer => !IsTextLayerAvailable;

    /// <summary>是否有状态提示。</summary>
    public bool HasStatusHint => !string.IsNullOrEmpty(StatusHint);

    partial void OnIsTextLayerAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoTextLayer));
        OnPropertyChanged(nameof(HasNoResults));
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnStatusHintChanged(string? value) => OnPropertyChanged(nameof(HasStatusHint));

    partial void OnQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CancelPendingDebounce();
            ResetForEmptyQuery();
            return;
        }

        ScheduleSearch(value);
    }

    // ------------------------------------------------------------------ 文本层探测（面板打开时）

    /// <summary>
    /// 面板打开时探测文本层可用性：惰性索引首页后查询文档级判定【ST-15】。
    /// 扫描件（首页无文本）→ 立即置灰搜索框并显示「去 OCR」，无需用户先输入。
    /// </summary>
    public void BeginProbe()
    {
        PendingProbeTask = Task.Run(async () =>
        {
            bool available;
            try
            {
                _index.GetPageText(DocId, 0); // 惰性索引首页（未挂接/失败由宿主保证）
                available = _index.IsTextLayerAvailable(DocId);
            }
            catch (Exception ex)
            {
                await PostAsync(() => StatusHint = "搜索初始化失败：" + ex.Message).ConfigureAwait(false);
                return;
            }

            await PostAsync(() => IsTextLayerAvailable = available).ConfigureAwait(false);
        });
    }

    // ------------------------------------------------------------------ 搜索命令

    /// <summary>点击命中：记录选中并通知宿主跳页 + 高亮。</summary>
    [RelayCommand]
    private void ActivateHit(SearchResultItem? item)
    {
        if (item is null) return;
        SelectedIndex = Results.IndexOf(item);
        HitActivated?.Invoke(this, item);
    }

    /// <summary>无文本层 → 去 OCR（宿主复用 T7 工具流）。</summary>
    [RelayCommand]
    private void GoToOcr() => OcrRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>面板 ✕ / Esc：宿主退出搜索并恢复原面板。</summary>
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ 异步搜索管线

    private void ScheduleSearch(string query)
    {
        CancelPendingDebounce();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        PendingSearchTask = RunSearchAsync(query, cts.Token);
    }

    private async Task RunSearchAsync(string query, CancellationToken token)
    {
        // 防抖：等待窗口内新输入会取消上一个 token（旧任务静默退出，不触发搜索）
        try
        {
            await Task.Delay(_debounceMs, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested) return;

        await PostAsync(() =>
        {
            IsSearching = true;
            IsIndexing = true;
            StatusHint = null;
        }).ConfigureAwait(false);

        IReadOnlyList<SearchHit>? allHits = null;
        List<SearchResultItem> projected = [];
        SearchIndexStatus? status = null;
        var textAvailable = IsTextLayerAvailable;
        Exception? failure = null;

        try
        {
            // 搜索走后台线程：Search 内部有 PDFium 调用且首次调用构建整册索引（500 页约 150ms）
            await Task.Run(() =>
            {
                allHits = _index.Search(DocId, query);
                status = _index.GetStatus(DocId);
                textAvailable = _index.IsTextLayerAvailable(DocId);
                projected = ProjectHits(allHits);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (token.IsCancellationRequested) return;

        await PostAsync(() =>
        {
            IsSearching = false;
            IsIndexing = false;
            HasSearched = true;

            if (failure is not null)
            {
                ClearResultItems();
                StatusHint = "搜索失败：" + failure.Message;
                return;
            }

            IsTextLayerAvailable = textAvailable;
            ReplaceResults(projected);
            TotalHitCount = allHits?.Count ?? 0;
            ResultSummary = BuildSummary(TotalHitCount, projected.Count);
            StatusHint = BuildStatusHint(status);
        }).ConfigureAwait(false);
    }

    private List<SearchResultItem> ProjectHits(IReadOnlyList<SearchHit> hits)
    {
        var list = new List<SearchResultItem>(Math.Min(hits.Count, _maxResults));
        foreach (var hit in hits)
        {
            if (list.Count >= _maxResults) break;
            var pageText = _index.GetPageText(DocId, hit.PageIndex);
            list.Add(SearchResultProjector.Project(hit, pageText, ExcerptContextChars));
        }
        return list;
    }

    private static string BuildSummary(int total, int shown)
        => total == 0 ? string.Empty
            : shown < total ? $"显示前 {shown} 处（共 {total} 处命中）"
            : $"共 {total} 处命中";

    private static string? BuildStatusHint(SearchIndexStatus? status)
    {
        if (status is null) return null;
        if (status.MemoryBudgetExceeded)
        {
            return $"部分索引：已达内存预算上限，仅索引 {status.IndexedPageCount}/{status.PageCount} 页，结果可能不完整";
        }
        if (!status.IsFullyIndexed)
        {
            return $"部分索引：已索引 {status.IndexedPageCount}/{status.PageCount} 页，结果可能不完整";
        }
        if (status.LowQualityPageCount > 0)
        {
            return $"有 {status.LowQualityPageCount} 页文本提取质量低，命中已标注";
        }
        return null;
    }

    private void ReplaceResults(IReadOnlyList<SearchResultItem> items)
    {
        Results.Clear();
        foreach (var item in items) Results.Add(item);
        NotifyResultStateChanged();
    }

    private void ClearResultItems()
    {
        if (Results.Count > 0) Results.Clear();
        TotalHitCount = 0;
        ResultSummary = string.Empty;
    }

    private void ResetForEmptyQuery()
    {
        if (Results.Count > 0) Results.Clear();
        HasSearched = false;
        IsSearching = false;
        IsIndexing = false;
        TotalHitCount = 0;
        ResultSummary = string.Empty;
        StatusHint = null;
    }

    private void NotifyResultStateChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private void CancelPendingDebounce()
    {
        if (_debounceCts is null) return;
        try
        {
            _debounceCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：无操作
        }
        _debounceCts.Dispose();
        _debounceCts = null;
    }

    /// <summary>
    /// 结果/状态回传 UI 线程（构造时捕获同步上下文；无上下文时内联）。
    /// 返回的 Task 在回调执行完成后完成——保证 PendingSearchTask 完成时状态已装配（测试可确定性等待）。
    /// </summary>
    private Task PostAsync(Action action)
    {
        var ctx = _syncContext;
        if (ctx is null || ReferenceEquals(ctx, SynchronizationContext.Current))
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Post(_ =>
        {
            try
            {
                action();
            }
            finally
            {
                tcs.TrySetResult();
            }
        }, null);
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingDebounce();
    }
}
