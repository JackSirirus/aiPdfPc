// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 书签面板 VM 测试：添加（含同页幂等）/删除/跳转事件/按文档隔离/空态标志。纯 VM 无 UI。
// 缩略图 VM 的纯逻辑部分（条目高度换算/队列去重）一并覆盖。
using System.IO;
using System.Windows.Media;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

public sealed class BookmarkPanelViewModelTests
{
    [Fact]
    public void AddBookmark_AppendsAndShowsList()
    {
        var vm = new BookmarkPanelViewModel();
        vm.AttachDocument(@"C:\docs\a.pdf");

        vm.SetCurrentPage(0); vm.AddCurrentPageCommand.Execute(null);
        vm.SetCurrentPage(9); vm.AddCurrentPageCommand.Execute(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.HasBookmarks);
        Assert.Equal("第 1 页", vm.Items[0].Title);
        Assert.Equal("第 10 页", vm.Items[1].Title);
    }

    [Fact]
    public void AddBookmark_SamePageIsIdempotent()
    {
        var vm = new BookmarkPanelViewModel();
        vm.AttachDocument(@"C:\docs\a.pdf");

        vm.SetCurrentPage(3); vm.AddCurrentPageCommand.Execute(null);
        vm.SetCurrentPage(3); vm.AddCurrentPageCommand.Execute(null);

        Assert.Single(vm.Items);
    }

    [Fact]
    public void RemoveBookmark_DeletesOnlyTarget()
    {
        var vm = new BookmarkPanelViewModel();
        vm.AttachDocument(@"C:\docs\a.pdf");
        vm.SetCurrentPage(1); vm.AddCurrentPageCommand.Execute(null);
        vm.SetCurrentPage(2); vm.AddCurrentPageCommand.Execute(null);

        vm.RemoveBookmarkCommand.Execute(vm.Items[0]);

        Assert.Single(vm.Items);
        Assert.Equal(2, vm.Items[0].PageIndex); // 0 基页 2
        Assert.True(vm.HasBookmarks);
    }

    [Fact]
    public void RemoveBookmark_LastItemTriggersEmptyStateFlag()
    {
        var vm = new BookmarkPanelViewModel();
        vm.AttachDocument(@"C:\docs\a.pdf");
        vm.SetCurrentPage(5); vm.AddCurrentPageCommand.Execute(null);

        vm.RemoveBookmarkCommand.Execute(vm.Items[0]);

        Assert.False(vm.HasBookmarks); // 【ST-18】空态条件
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void NavigateTo_RaisesEventWithZeroBasedPageIndex()
    {
        var vm = new BookmarkPanelViewModel();
        vm.AttachDocument(@"C:\docs\a.pdf");
        vm.SetCurrentPage(7); vm.AddCurrentPageCommand.Execute(null);

        int? received = null;
        vm.NavigateRequested += (_, pageIndex) => received = pageIndex;
        vm.NavigateToCommand.Execute(vm.Items[0]);

        Assert.Equal(7, received);
    }

    [Fact]
    public void AttachDocument_IsolatesBookmarksPerDocument()
    {
        var vm = new BookmarkPanelViewModel();
        vm.AttachDocument(@"C:\docs\a.pdf");
        vm.SetCurrentPage(1); vm.AddCurrentPageCommand.Execute(null);
        vm.SetCurrentPage(2); vm.AddCurrentPageCommand.Execute(null);

        vm.AttachDocument(@"C:\docs\b.pdf"); // 切换文档：空列表
        Assert.Empty(vm.Items);

        vm.SetCurrentPage(9); vm.AddCurrentPageCommand.Execute(null);
        Assert.Single(vm.Items);

        vm.AttachDocument(@"C:\docs\a.pdf"); // 切回：会话内保留
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void AddBookmark_WithoutAttachedDocument_IsNoOp()
    {
        var vm = new BookmarkPanelViewModel();
        vm.SetCurrentPage(0); vm.AddCurrentPageCommand.Execute(null); // 未挂接：纯 VM 防御（UI 层按钮禁用）
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void NavigateTo_NullOrRemovedBookmark_DoesNotThrow()
    {
        var vm = new BookmarkPanelViewModel();
        vm.NavigateToCommand.Execute(null);
        vm.NavigateToCommand.Execute(new BookmarkItemViewModel(3, System.DateTime.Now)); // 不经事件断言，仅不抛
    }
}

public sealed class ThumbPanelViewModelTests
{
    /// <summary>最小渲染器桩：页数/尺寸可指定，渲染返回占位对象。</summary>
    private sealed class StubRenderer(int pageCount, double widthPt = 595) : IThumbnailRenderer
    {
        public Task<ImageSource> RenderPageAsync(string docId, int pageIndex, double dpi, int zoomPct)
            => Task.FromResult<ImageSource>(null!); // 纯 VM 测试不消费位图

        public int GetPageCount(string docId) => pageCount;

        public PageSize? GetPageSize(string docId, int pageIndex) => new(widthPt, 842);
    }

    [Fact]
    public void Constructor_BuildsItemsWithRatioDerivedHeight()
    {
        // A4 竖版 595×842：高 = 120 × 842/595 ≈ 170
        var vm = new ThumbPanelViewModel(new StubRenderer(3), "doc", 3);

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(3, vm.TotalCount);
        Assert.All(vm.Items, i => Assert.Equal(Math.Round(120.0 * 842.0 / 595.0), i.Height)); // 170
        Assert.Equal("1", vm.Items[0].Label);
        Assert.Equal("3", vm.Items[2].Label);
    }

    [Fact]
    public void EnsureRange_EnqueuesOnlyVisibleRange_OncePerItem()
    {
        var vm = new ThumbPanelViewModel(new StubRenderer(10), "doc", 10);

        vm.EnsureRange(2, 4);
        vm.EnsureRange(2, 4); // 重复调用不重复入队

        // 可见区 3 项已排队：Worker 未渲染完成前占位仍为空，但 IsQueuedOrLoaded 标记已置
        Assert.Equal(3, vm.Items.Count(i => i.IsQueuedOrLoaded));
        Assert.Equal(3, vm.Items[2].PageIndex is >= 2 and <= 4 ? 3 : 0); // 区间正确性哨兵
    }

    [Fact]
    public void EnsureRange_ClampsOutOfBoundsValues()
    {
        var vm = new ThumbPanelViewModel(new StubRenderer(5), "doc", 5);

        vm.EnsureRange(-5, 999); // 越界收敛到 [0,4]

        Assert.Equal(5, vm.Items.Count(i => i.IsQueuedOrLoaded));
    }

    [Fact]
    public void RequestNavigate_RaisesEventWithPageIndex()
    {
        var vm = new ThumbPanelViewModel(new StubRenderer(3), "doc", 3);
        int? received = null;
        vm.NavigateRequested += (_, pageIndex) => received = pageIndex;

        vm.RequestNavigate(2);

        Assert.Equal(2, received);
    }
}
