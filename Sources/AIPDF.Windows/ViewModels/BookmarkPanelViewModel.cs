// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 书签面板视图模型：用户自建书签（添加/删除/跳转）。
// 【降级点②】V1 书签仅会话内有效，不落盘；V2 持久化挂点 = PersistAsync/RestoreAsync（见文件尾注释）。
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.ViewModels;

/// <summary>单条用户书签：目标页（0 基）与记录时间。</summary>
public class BookmarkItemViewModel
{
    public int PageIndex { get; }

    /// <summary>显示名：第 N 页（N 为 1 基页码）。</summary>
    public string Title => $"第 {PageIndex + 1} 页";

    /// <summary>加入时间（显示辅助，亦为稳定排序依据）。</summary>
    public DateTime CreatedAt { get; }

    public BookmarkItemViewModel(int pageIndex, DateTime createdAt)
    {
        PageIndex = pageIndex;
        CreatedAt = createdAt;
    }
}

/// <summary>
/// 书签面板视图模型（纯 VM，无 UI 依赖，可单元测试）：
/// - 按文档（docId）分会话书签，切换标签不互串；关闭应用即失效（V1 边界）；
/// - 同页重复添加只更新既有条目（不产生重复行）；删除/跳转由选中项驱动。
/// </summary>
public partial class BookmarkPanelViewModel : ObservableObject
{
    /// <summary>docId → 会话书签列表（保持添加顺序）。</summary>
    private readonly Dictionary<string, ObservableCollection<BookmarkItemViewModel>> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<BookmarkItemViewModel> Items { get; } = [];

    /// <summary>点击书签请求跳页（宿主接线 T3 ReaderView 滚动 API；T7 前为事件桩）。</summary>
    public event EventHandler<int>? NavigateRequested;

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

    public BookmarkPanelViewModel()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>宿主推送当前阅读页（ReaderView 滚动/翻页时调用；V1 挂点，见 CurrentPageIndex 注释）。</summary>
    public void SetCurrentPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        CurrentPageIndex = pageIndex;
    }

    /// <summary>切换挂接文档：书签列表随 docId 切换（同会话内各文档书签互不干扰）。</summary>
    public void AttachDocument(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        CurrentDocId = docId;
        if (!_store.TryGetValue(docId, out var list))
        {
            list = [];
            _store[docId] = list;
        }
        ReplaceItems(list);
    }

    /// <summary>收起面板/关闭文档时保持会话书签（不清理）；关闭文档后的清理挂点见 V2 注释。</summary>
    public void DetachDocument() => CurrentDocId = null;

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

        list.Add(new BookmarkItemViewModel(CurrentPageIndex, DateTime.Now));
        ReplaceItems(list);
    }

    /// <summary>删除指定书签（XAML 删除按钮命令）。</summary>
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
}

// ---------------------------------------------------------------------------
// V2 持久化挂点（降级点②，PRD 3.1 FR-04）：
// - 存储形态建议：按文档哈希（而非路径，防止移动文件失效）落 %AppData%/AIPDF/bookmarks.json；
// - PersistAsync(docId)：文档标签关闭 / 应用退出时序列化 _store[docId]；
// - RestoreAsync(docId)：AttachDocument 时读回；时间戳用于稳定排序。
// - V2 同时补「关闭前未保存书签」确认【ST-22 关联】。
// ---------------------------------------------------------------------------
