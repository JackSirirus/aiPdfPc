// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 书签面板视图模型：用户自建书签（添加/删除/跳转）。
// V2 T8 持久化接线（并入 reading-state，Q3）：PersistAsync/RestoreAsync 走 T5 ReadingStateStore；
//   schema/键/MigrateAsync 归 T5 所有，本类不自行迁移、不另设 bookmarks.json。
using System.Collections.ObjectModel;
using AIPDF.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.ViewModels;

/// <summary>
/// 书签自动标题提供者（PRD 3.1 FR-04：目标页首行文字截断）。由宿主注入文本层适配器；
/// 未注入或提取失败时面板回退「第 N 页」，标题永不阻断书签的添加/恢复。
/// </summary>
public interface IBookmarkTitleProvider
{
    /// <summary>返回目标页首行文字；无文本层/越界/失败返回 null（调用方回退页码）。</summary>
    string? GetFirstLine(string docId, int pageIndex);
}

/// <summary>单条用户书签：目标页（0 基）、显示标题与记录时间。</summary>
public class BookmarkItemViewModel
{
    public int PageIndex { get; }

    /// <summary>显示名：无自动标题时为「第 N 页」；有首行文字时为「第 N 页 · 首行」。</summary>
    public string Title { get; }

    /// <summary>加入时间（显示辅助，亦为稳定排序依据）。</summary>
    public DateTime CreatedAt { get; }

    public BookmarkItemViewModel(int pageIndex, DateTime createdAt, string? title = null)
    {
        PageIndex = pageIndex;
        CreatedAt = createdAt;
        Title = string.IsNullOrWhiteSpace(title) ? $"第 {pageIndex + 1} 页" : title;
    }
}

/// <summary>
/// 书签面板视图模型（纯 VM，无 UI 依赖，可单元测试）：
/// - 按文档（docId）分会话书签，切换标签不互串；
/// - V2 T8：书签并入每文件 reading-state（<see cref="ReadingStateStore"/>，Q3），
///   标签关闭 / 应用退出时 <see cref="PersistAsync"/> 落盘，<see cref="AttachDocument"/> 时 <see cref="RestoreAsync"/> 读回；
/// - 删除/跳转由选中项驱动，删除即时移除并在下次保存持久化。
/// </summary>
public partial class BookmarkPanelViewModel : ObservableObject
{
    /// <summary>自动标题首行截断上限（FR-04）。</summary>
    public const int TitleFirstLineMaxChars = 40;

    /// <summary>docId → 会话书签列表（保持添加顺序）。</summary>
    private readonly Dictionary<string, ObservableCollection<BookmarkItemViewModel>> _store =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>T5 阅读状态存储（schema/键/迁移的唯一所有者）；null = 纯会话模式（无宿主/测试默认）。</summary>
    private readonly ReadingStateStore? _stateStore;

    public ObservableCollection<BookmarkItemViewModel> Items { get; } = [];

    /// <summary>点击书签请求跳页（宿主接线 T3 ReaderView 滚动 API）。</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>FR-04 自动标题来源；未注入时回退「第 N 页」。</summary>
    public IBookmarkTitleProvider? TitleProvider { get; set; }

    /// <summary>是否有书签（false → 空态【ST-18】）。</summary>
    public bool HasBookmarks => Items.Count > 0;

    /// <summary>当前挂接的文档（空 = 未挂接，添加按钮禁用）。</summary>
    [ObservableProperty]
    private string? _currentDocId;

    /// <summary>
    /// 宿主推送的当前阅读页（0 基）。
    /// 【接线挂点】T7 将 ReaderView 滚动位置接到此属性；接线前默认第 1 页。
    /// </summary>
    [ObservableProperty]
    private int _currentPageIndex;

    /// <summary>添加按钮可用条件（未挂接文档禁用）。</summary>
    public bool CanAddBookmark => CurrentDocId is not null;

    partial void OnCurrentDocIdChanged(string? value) => OnPropertyChanged(nameof(CanAddBookmark));

    /// <param name="stateStore">T5 阅读状态存储；null 时退化为仅在会话内保存（既有测试/无宿主场景）。</param>
    public BookmarkPanelViewModel(ReadingStateStore? stateStore = null)
    {
        _stateStore = stateStore;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>宿主推送当前阅读页（ReaderView 滚动/翻页时调用）。</summary>
    public void SetCurrentPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        CurrentPageIndex = pageIndex;
    }

    /// <summary>
    /// 切换挂接文档：书签列表随 docId 切换（同会话内各文档书签互不干扰）。
    /// T8：会话尚未跟踪该文档时，先从 reading-state 回读书签（Q3）；已跟踪则保留会话内最新状态。
    /// </summary>
    public void AttachDocument(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        _ = RestoreAsync(docId); // 同步完成（见 RestoreAsync）；须在建空列表前调用，否则会被判为已跟踪
        CurrentDocId = docId;
        if (!_store.TryGetValue(docId, out var list))
        {
            list = [];
            _store[docId] = list;
        }
        ReplaceItems(list);
    }

    /// <summary>收起面板/关闭文档时保持会话书签（不清理）；清理由 <see cref="RemoveDocument"/> 负责。</summary>
    public void DetachDocument() => CurrentDocId = null;

    /// <summary>
    /// 文档关闭：移除该文档的会话书签（T4 清理挂点，MainWindow 中在持久化之后调用）。
    /// </summary>
    public void RemoveDocument(string docId)
    {
        if (string.IsNullOrWhiteSpace(docId)) return;
        _store.Remove(docId);
        if (string.Equals(CurrentDocId, docId, StringComparison.OrdinalIgnoreCase))
        {
            CurrentDocId = null;
            ReplaceItems([]);
        }
    }

    // ------------------------------------------------------------------ V2 持久化（并入 reading-state）

    /// <summary>
    /// 把某文档的会话书签写入它自己的每文件 reading-state（Q3：与 T5 共用键与序列化，不另设 bookmarks.json）。
    /// 读改写：仅替换 <see cref="ReadingState.Bookmarks"/>，页码/缩放/面板等 T5 字段原样保留；按 CreatedAt 稳定排序。
    /// 会话未跟踪该文档或文件已不可读时不做任何写入（避免用空列表覆盖已落盘书签）。
    /// </summary>
    public Task PersistAsync(string docId)
    {
        if (_stateStore is null || string.IsNullOrWhiteSpace(docId)) return Task.CompletedTask;
        if (!_store.ContainsKey(docId)) return Task.CompletedTask;

        var stamp = ReadingStateKey.TryCapture(docId);
        if (stamp is null) return Task.CompletedTask;

        var state = _stateStore.Load(stamp.Value.Key)
                    ?? new ReadingState { Path = docId, LastAccessUtc = DateTime.UtcNow };
        state.Bookmarks =
        [
            .. SnapshotBookmarks(docId)
                .OrderBy(b => b.CreatedAt) // LINQ OrderBy 稳定：时间戳并列时保留加入顺序
                .Select(b => new ReadingStateBookmark { PageIndex = b.PageIndex, CreatedAt = b.CreatedAt }),
        ];
        _stateStore.Save(stamp.Value.Key, state);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从该文档的 reading-state 读回书签（AttachDocument 时调用）。
    /// 只读不迁移；损坏/缺失静默回退（AC-24.2）；会话已跟踪该文档时不回读，避免覆盖会话内新增/删除。
    /// </summary>
    public Task RestoreAsync(string docId)
    {
        if (_stateStore is null || string.IsNullOrWhiteSpace(docId)) return Task.CompletedTask;
        if (_store.ContainsKey(docId)) return Task.CompletedTask;

        var stamp = ReadingStateKey.TryCapture(docId);
        if (stamp is null) return Task.CompletedTask;

        var state = _stateStore.Load(stamp.Value.Key);
        if (state is null || state.Bookmarks.Count == 0) return Task.CompletedTask;

        RestoreDocument(docId, state.Bookmarks
            .OrderBy(b => b.CreatedAt)
            .Select(b => (b.PageIndex, b.CreatedAt)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 导出某文档的全部书签快照（0 基页码 + 加入时间），供 <see cref="PersistAsync"/> 写入每文件阅读状态。
    /// 未挂接/无书签返回空列表。
    /// </summary>
    public IReadOnlyList<(int PageIndex, DateTime CreatedAt)> SnapshotBookmarks(string docId)
    {
        if (string.IsNullOrWhiteSpace(docId) || !_store.TryGetValue(docId, out var list))
        {
            return [];
        }

        return [.. list.Select(b => (b.PageIndex, b.CreatedAt))];
    }

    /// <summary>
    /// 从阅读状态恢复某文档的书签：写入会话存储；若该文档当前已挂接，同步刷新列表。
    /// 同页去重、越界（负数）忽略；按传入顺序保留；标题按 FR-04 重新计算（提供者可用时取首行）。
    /// </summary>
    public void RestoreDocument(string docId, IEnumerable<(int PageIndex, DateTime CreatedAt)> bookmarks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentNullException.ThrowIfNull(bookmarks);

        var restored = new ObservableCollection<BookmarkItemViewModel>();
        foreach (var (pageIndex, createdAt) in bookmarks)
        {
            if (pageIndex < 0) continue;
            if (restored.Any(b => b.PageIndex == pageIndex)) continue;
            restored.Add(CreateItem(docId, pageIndex, createdAt));
        }

        _store[docId] = restored;
        if (string.Equals(CurrentDocId, docId, StringComparison.OrdinalIgnoreCase))
        {
            ReplaceItems(restored);
        }
    }

    /// <summary>收藏当前页（宿主推送的 CurrentPageIndex）。重复页幂等不重复加行。</summary>
    [RelayCommand]
    private void AddCurrentPage()
    {
        if (CurrentDocId is null)
        {
            return; // 未挂接文档：无操作（按钮已禁用，此为纯 VM 防御）
        }

        var list = _store[CurrentDocId];
        if (list.Any(b => b.PageIndex == CurrentPageIndex))
        {
            return; // 同页已收藏：幂等
        }

        list.Add(CreateItem(CurrentDocId, CurrentPageIndex, DateTime.Now));
        ReplaceItems(list);
    }

    /// <summary>删除指定书签（XAML 删除按钮命令）；即时移除，随下次 <see cref="PersistAsync"/> 落盘。</summary>
    [RelayCommand]
    private void RemoveBookmark(BookmarkItemViewModel? bookmark)
    {
        if (bookmark is null || CurrentDocId is null || !_store.TryGetValue(CurrentDocId, out var list))
        {
            return;
        }
        list.Remove(bookmark);
        ReplaceItems(list);
    }

    /// <summary>跳转到指定书签（XAML 绑定命令）。</summary>
    [RelayCommand]
    private void NavigateTo(BookmarkItemViewModel? bookmark)
    {
        if (bookmark is not null)
        {
            NavigateRequested?.Invoke(this, bookmark.PageIndex);
        }
    }

    private void ReplaceItems(ObservableCollection<BookmarkItemViewModel> list)
    {
        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }
        OnPropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>构造带自动标题的书签项（FR-04）：有首行文字时附于页码后，否则仅页码。</summary>
    private BookmarkItemViewModel CreateItem(string docId, int pageIndex, DateTime createdAt)
    {
        var firstLine = TryGetFirstLine(docId, pageIndex);
        var title = string.IsNullOrWhiteSpace(firstLine)
            ? $"第 {pageIndex + 1} 页"
            : $"第 {pageIndex + 1} 页 · {firstLine}";
        return new BookmarkItemViewModel(pageIndex, createdAt, title);
    }

    /// <summary>取目标页首行（截断）；提供者缺失/异常/空文本均返回 null 回退页码——标题绝不阻断书签。</summary>
    private string? TryGetFirstLine(string docId, int pageIndex)
    {
        if (TitleProvider is null) return null;
        try
        {
            var text = TitleProvider.GetFirstLine(docId, pageIndex);
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                return line.Length <= TitleFirstLineMaxChars ? line : line[..TitleFirstLineMaxChars] + "…";
            }
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null; // 标题仅为展示：文本层/原生异常一律回退页码
        }
    }
}
