// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T8 书签持久化测试：并入每文件 reading-state（Q3，与 T5 共用键/序列化）——
// persist/restore round-trip、删除持久化、按文档隔离、损坏回退、保留 T5 字段、时间戳稳定排序。
using System.IO;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public sealed class BookmarkPersistenceTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string StoreDir { get; }
        public string PathA { get; }
        public string PathB { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-t8-" + Guid.NewGuid().ToString("N"));
            StoreDir = Path.Combine(Root, "state");
            Directory.CreateDirectory(StoreDir);
            PathA = Path.Combine(Root, "a.pdf");
            PathB = Path.Combine(Root, "b.pdf");
            File.WriteAllText(PathA, "PDF-A");
            File.WriteAllText(PathB, "PDF-B");
        }

        public ReadingStateStore NewStore() => new(StoreDir, () => Now);

        public string KeyFor(string path) => ReadingStateKey.TryCapture(path)!.Value.Key;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>确定性标题桩：仅第 3 页（0 基 2）有首行文字。</summary>
    private sealed class StubTitles : IBookmarkTitleProvider
    {
        public string? GetFirstLine(string docId, int pageIndex)
            => pageIndex == 2 ? "第三章 结论" : null;
    }

    [Fact]
    public async Task PersistRestore_RoundTrip_PreservesPagesAndOrder()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var writer = new BookmarkPanelViewModel(store);
        writer.AttachDocument(fx.PathA);
        writer.SetCurrentPage(0); writer.AddCurrentPageCommand.Execute(null);
        writer.SetCurrentPage(4); writer.AddCurrentPageCommand.Execute(null);
        writer.SetCurrentPage(2); writer.AddCurrentPageCommand.Execute(null);

        await writer.PersistAsync(fx.PathA);

        // 与 T5 共用键/同一文件；不得出现独立 bookmarks.json（Q3）
        Assert.True(File.Exists(store.FilePathFor(fx.KeyFor(fx.PathA))));
        Assert.Empty(Directory.GetFiles(fx.StoreDir, "*bookmarks*"));

        var reader = new BookmarkPanelViewModel(store);
        reader.AttachDocument(fx.PathA);

        Assert.True(reader.HasBookmarks);
        Assert.Equal(new[] { 0, 4, 2 }, reader.Items.Select(i => i.PageIndex));
        Assert.Equal("第 3 页", reader.Items[2].Title); // 无标题提供者 → 回退页码
    }

    [Fact]
    public async Task Persist_DeleteRemovesImmediately_AndPersists()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var writer = new BookmarkPanelViewModel(store);
        writer.AttachDocument(fx.PathA);
        writer.SetCurrentPage(0); writer.AddCurrentPageCommand.Execute(null);
        writer.SetCurrentPage(1); writer.AddCurrentPageCommand.Execute(null);
        await writer.PersistAsync(fx.PathA);

        writer.RemoveBookmarkCommand.Execute(writer.Items[0]);
        Assert.Single(writer.Items); // 即时移除
        await writer.PersistAsync(fx.PathA);

        var reader = new BookmarkPanelViewModel(store);
        reader.AttachDocument(fx.PathA);
        Assert.Single(reader.Items);
        Assert.Equal(1, reader.Items[0].PageIndex);
    }

    [Fact]
    public async Task PerDocumentIsolation_NoCrossTalk()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var writer = new BookmarkPanelViewModel(store);
        writer.AttachDocument(fx.PathA);
        writer.SetCurrentPage(2); writer.AddCurrentPageCommand.Execute(null);
        await writer.PersistAsync(fx.PathA);

        writer.AttachDocument(fx.PathB);
        writer.SetCurrentPage(7); writer.AddCurrentPageCommand.Execute(null);
        await writer.PersistAsync(fx.PathB);

        var reader = new BookmarkPanelViewModel(store);
        reader.AttachDocument(fx.PathA);
        Assert.Single(reader.Items);
        Assert.Equal(2, reader.Items[0].PageIndex);

        reader.AttachDocument(fx.PathB);
        Assert.Single(reader.Items);
        Assert.Equal(7, reader.Items[0].PageIndex);
    }

    [Fact]
    public void CorruptState_FallsBackToEmpty_NoThrow()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        File.WriteAllText(store.FilePathFor(fx.KeyFor(fx.PathA)), "{ 不是 json");

        var reader = new BookmarkPanelViewModel(store);
        reader.AttachDocument(fx.PathA);

        Assert.Empty(reader.Items);
        Assert.False(reader.HasBookmarks);
    }

    [Fact]
    public async Task Persist_PreservesReadingStateFieldsOwnedByT5()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var key = fx.KeyFor(fx.PathA);
        store.Save(key, new ReadingState
        {
            Path = fx.PathA,
            LastPage = 47,
            LastZoom = 150,
            PanelKey = "bookmarks",
            LastAccessUtc = Now,
        });

        var panel = new BookmarkPanelViewModel(store);
        panel.AttachDocument(fx.PathA);
        panel.SetCurrentPage(4); panel.AddCurrentPageCommand.Execute(null);
        await panel.PersistAsync(fx.PathA);

        var state = store.Load(key)!;
        Assert.Equal(47, state.LastPage);        // 读改写不得覆盖 T5 字段
        Assert.Equal(150, state.LastZoom);
        Assert.Equal("bookmarks", state.PanelKey);
        var bookmark = Assert.Single(state.Bookmarks);
        Assert.Equal(4, bookmark.PageIndex);
    }

    [Fact]
    public async Task Persist_SortsBookmarksByCreatedAt_Stable()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var panel = new BookmarkPanelViewModel(store);
        panel.AttachDocument(fx.PathA);
        var unordered = new (int PageIndex, DateTime CreatedAt)[]
        {
            (2, Now.AddMinutes(5)),
            (0, Now),
            (1, Now.AddMinutes(1)),
        };
        panel.RestoreDocument(fx.PathA, unordered);

        await panel.PersistAsync(fx.PathA);

        var loaded = store.Load(fx.KeyFor(fx.PathA))!;
        Assert.Equal(new[] { 0, 1, 2 }, loaded.Bookmarks.Select(b => b.PageIndex));
    }

    [Fact]
    public void Title_UsesFirstLineProvider_WhenAvailable_AndTruncates()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var panel = new BookmarkPanelViewModel(store) { TitleProvider = new StubTitles() };
        panel.AttachDocument(fx.PathA);
        panel.SetCurrentPage(2); panel.AddCurrentPageCommand.Execute(null);
        Assert.Equal("第 3 页 · 第三章 结论", panel.Items[0].Title);

        panel.SetCurrentPage(9); panel.AddCurrentPageCommand.Execute(null);
        Assert.Equal("第 10 页", panel.Items[1].Title); // 无文本 → 回退页码
    }
}
