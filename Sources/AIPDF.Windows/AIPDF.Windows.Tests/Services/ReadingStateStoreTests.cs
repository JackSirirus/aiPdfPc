// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T5 阅读状态存储测试：键（size/mtime 敏感）、恢复/损坏、GC 边界、键迁移（BLK-1）、无密码字段。
using System.IO;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class ReadingStateStoreTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string StoreDir { get; }
        public string FilePath { get; }

        public Fixture(string content = "PDF-A")
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-t5-" + Guid.NewGuid().ToString("N"));
            StoreDir = Path.Combine(Root, "state");
            Directory.CreateDirectory(StoreDir);
            FilePath = Path.Combine(Root, "doc.pdf");
            System.IO.File.WriteAllText(FilePath, content);
        }

        public ReadingStateStore NewStore(DateTime? now = null)
            => new(StoreDir, () => now ?? Now);

        public FileStateStamp Stamp => ReadingStateKey.TryCapture(FilePath)!.Value;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 键 / 恢复

    [Fact]
    public void SaveLoad_Roundtrip_PreservesAllFields_Utf8NoBom()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var state = new ReadingState
        {
            Path = fx.FilePath,
            LastPage = 47,
            LastZoom = 150,
            PanelKey = "bookmarks",
            Bookmarks = [new ReadingStateBookmark { PageIndex = 46, CreatedAt = Now }],
            LastAccessUtc = Now,
        };

        store.Save(fx.Stamp.Key, state);

        var bytes = System.IO.File.ReadAllBytes(store.FilePathFor(fx.Stamp.Key));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // UTF-8 无 BOM

        var loaded = store.Load(fx.Stamp.Key);
        Assert.NotNull(loaded);
        Assert.Equal(47, loaded!.LastPage);
        Assert.Equal(150, loaded.LastZoom);
        Assert.Equal("bookmarks", loaded.PanelKey);
        var bookmark = Assert.Single(loaded.Bookmarks);
        Assert.Equal(46, bookmark.PageIndex);
    }

    [Fact]
    public void SerializedState_ContainsNoPasswordField()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        store.Save(fx.Stamp.Key, new ReadingState
        {
            Path = fx.FilePath,
            LastPage = 3,
            LastZoom = 100,
            LastAccessUtc = Now,
        });

        var json = System.IO.File.ReadAllText(store.FilePathFor(fx.Stamp.Key));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRestore_Hit_RestoresAndTouchesLastAccess()
    {
        using var fx = new Fixture();
        var clock = Now;
        var store = fx.NewStore(clock);
        store.Save(fx.Stamp.Key, new ReadingState
        {
            Path = fx.FilePath,
            LastPage = 12,
            LastZoom = 133,
            PanelKey = "outline",
            LastAccessUtc = Now.AddDays(-5),
        });

        var later = Now.AddHours(3);
        var store2 = fx.NewStore(later);
        var restored = store2.TryRestore(fx.FilePath);

        Assert.NotNull(restored);
        Assert.Equal(12, restored!.LastPage);
        Assert.Equal(133, restored.LastZoom);
        Assert.Equal("outline", restored.PanelKey);
        Assert.Equal(later, restored.LastAccessUtc); // 打开时显式写最后访问（M-7，非 FS atime）
        Assert.Equal(later, store2.Load(fx.Stamp.Key)!.LastAccessUtc);
    }

    [Fact]
    public void TryRestore_SizeChange_InvalidatesState_FromPageOne()
    {
        using var fx = new Fixture("PDF-A"); // size = 5
        var store = fx.NewStore();
        var oldKey = fx.Stamp.Key;
        store.Save(oldKey, new ReadingState { LastPage = 47, LastAccessUtc = Now });

        System.IO.File.WriteAllText(fx.FilePath, "PDF-CONTENT-LONGER"); // size 变（AC-25.1）
        var newStamp = fx.Stamp;

        Assert.NotEqual(oldKey, newStamp.Key);
        Assert.Null(store.TryRestore(fx.FilePath)); // 键不匹配 → 查不到 → 第 1 页
        Assert.NotNull(store.Load(oldKey));          // 旧状态文件仍在（未被误删）
    }

    [Fact]
    public void TryRestore_MtimeChange_InvalidatesState()
    {
        using var fx = new Fixture("PDF-A");
        var store = fx.NewStore();
        store.Save(fx.Stamp.Key, new ReadingState { LastPage = 20, LastAccessUtc = Now });

        System.IO.File.SetLastWriteTimeUtc(fx.FilePath, DateTime.UtcNow.AddSeconds(60)); // 仅 mtime 变
        Assert.Null(store.TryRestore(fx.FilePath));
    }

    [Fact]
    public void Load_CorruptOrWrongSchema_ReturnsNull_Silently()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        System.IO.File.WriteAllText(store.FilePathFor("abc"), "{ 不是 json");
        Assert.Null(store.Load("abc")); // 损坏 → 静默重建（AC-24.2）

        System.IO.File.WriteAllText(store.FilePathFor("def"), """{"schema":999,"lastPage":5}""");
        Assert.Null(store.Load("def")); // 版本不识别

        Assert.Null(store.TryRestore(fx.FilePath)); // 无状态 → 从第 1 页
    }

    // ---------------------------------------------------------------- 单一真相源 / 兜底（M-6/BA-6）

    [Fact]
    public void ResolveRestore_ReadingStateWins_OverRecentMirror()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var stamp = fx.Stamp;
        store.Save(stamp.Key, new ReadingState
        {
            Path = fx.FilePath,
            LastPage = 47,
            LastZoom = 150,
            PanelKey = "bookmarks",
            LastAccessUtc = Now.AddDays(-1),
        });

        // 最近记录镜像（过时：页 10）——不得覆盖 reading-state（SoT）
        var recent = new RecentFileEntry { Path = fx.FilePath, Name = "doc.pdf", Bytes = stamp.Size, LastPage = 10, LastZoom = 100 };
        var restore = store.ResolveRestore(fx.FilePath, recent);

        Assert.NotNull(restore);
        Assert.True(restore!.FromReadingState);
        Assert.Equal(47, restore.LastPage);
        Assert.Equal(150, restore.LastZoom);
        Assert.Equal("bookmarks", restore.PanelKey);
    }

    [Fact]
    public void ResolveRestore_NoState_FallsBackToRecentMirror_WhenSizeMatches()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var stamp = fx.Stamp;

        var recent = new RecentFileEntry { Path = fx.FilePath, Name = "doc.pdf", Bytes = stamp.Size, LastPage = 10, LastZoom = 120 };
        var restore = store.ResolveRestore(fx.FilePath, recent);

        Assert.NotNull(restore);
        Assert.False(restore!.FromReadingState); // 兜底投影
        Assert.Equal(10, restore.LastPage);
        Assert.Equal(120, restore.LastZoom);
        Assert.Equal(ReadingState.PanelThumbs, restore.PanelKey);
        Assert.Empty(restore.Bookmarks);
    }

    [Fact]
    public void ResolveRestore_NoState_AndSizeChanged_ReturnsNull_FromPageOne()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var stamp = fx.Stamp;

        // 最近记录镜像的 Bytes 与当前文件不一致（文件已被替换）→ 不回退（AC-25.1）
        var recent = new RecentFileEntry { Path = fx.FilePath, Name = "doc.pdf", Bytes = stamp.Size + 100, LastPage = 10, LastZoom = 120 };
        Assert.Null(store.ResolveRestore(fx.FilePath, recent));
    }

    // ---------------------------------------------------------------- GC（FR-27）

    private static string SaveSynthetic(ReadingStateStore store, int index, DateTime lastAccess)
    {
        var key = ReadingStateKey.Compute($@"C:\docs\f{index:D3}.pdf", 1000 + index, 10_000 + index);
        store.Save(key, new ReadingState { Path = $@"C:\docs\f{index:D3}.pdf", LastPage = index + 1, LastAccessUtc = lastAccess });
        return key;
    }

    [Fact]
    public void GarbageCollect_RemovesOnlyBeyond90Days()
    {
        using var fx = new Fixture();
        var store = fx.NewStore(Now);
        var keptKey = SaveSynthetic(store, 1, Now.AddDays(-89));
        var goneKey = SaveSynthetic(store, 2, Now.AddDays(-91));

        var result = store.GarbageCollect();

        Assert.Equal(1, result.RemovedExpired);
        Assert.Equal(0, result.RemovedOverflow);
        Assert.Equal(1, result.Remaining);
        Assert.NotNull(store.Load(keptKey));
        Assert.Null(store.Load(goneKey));
    }

    [Fact]
    public void GarbageCollect_105_EvictsOldest5_Keeping100()
    {
        using var fx = new Fixture();
        var store = fx.NewStore(Now);
        var keys = new string[105];
        for (var i = 0; i < 105; i++)
        {
            keys[i] = SaveSynthetic(store, i, Now.AddMinutes(i)); // i 越大越新
        }

        var result = store.GarbageCollect();

        Assert.Equal(0, result.RemovedExpired);
        Assert.Equal(5, result.RemovedOverflow);
        Assert.Equal(100, result.Remaining);
        for (var i = 0; i < 5; i++) Assert.Null(store.Load(keys[i]));        // 最旧 5 条被淘汰
        for (var i = 5; i < 105; i++) Assert.NotNull(store.Load(keys[i]));   // 其余保留
    }

    [Fact]
    public void GarbageCollect_80AllRecent_KeepsAll()
    {
        using var fx = new Fixture();
        var store = fx.NewStore(Now);
        for (var i = 0; i < 80; i++) SaveSynthetic(store, i, Now.AddHours(-i));

        var result = store.GarbageCollect();

        Assert.Equal(0, result.RemovedTotal);
        Assert.Equal(80, result.Remaining);
        Assert.Equal(80, store.ListKeys().Count);
    }

    [Fact]
    public void GarbageCollect_PreservesExplicitlyProtectedKeys()
    {
        using var fx = new Fixture();
        var store = fx.NewStore(Now);
        var protectedKey = SaveSynthetic(store, 1, Now.AddDays(-200)); // 超 90 天
        var orphanKey = SaveSynthetic(store, 2, Now.AddDays(-200));

        var result = store.GarbageCollect([protectedKey]);

        Assert.Equal(1, result.RemovedExpired);
        Assert.NotNull(store.Load(protectedKey)); // 当前打开文件的状态不被删
        Assert.Null(store.Load(orphanKey));
    }

    // ---------------------------------------------------------------- 键迁移（BLK-1）

    [Fact]
    public async Task MigrateAsync_WritesNewKey_DeletesOld_PreservesBookmarks()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        const string oldKey = "oldkey0000000000000000000000000000000000000000000000000000000000";
        const string newKey = "newkey0000000000000000000000000000000000000000000000000000000000";
        store.Save(oldKey, new ReadingState
        {
            Path = fx.FilePath,
            LastPage = 33,
            LastZoom = 125,
            PanelKey = "bookmarks",
            Bookmarks = [new ReadingStateBookmark { PageIndex = 7, CreatedAt = Now }],
            LastAccessUtc = Now,
        });

        await store.MigrateAsync(oldKey, newKey);

        Assert.Null(store.Load(oldKey));       // 旧键已删
        var migrated = store.Load(newKey);
        Assert.NotNull(migrated);              // 新键含书签
        Assert.Equal(33, migrated!.LastPage);
        var bookmark = Assert.Single(migrated.Bookmarks);
        Assert.Equal(7, bookmark.PageIndex);
    }

    [Fact]
    public async Task MigrateAsync_OldKeyMissing_IsNoOp()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        await store.MigrateAsync("missing-old", "missing-new");

        Assert.Empty(store.ListKeys());
    }

    [Fact]
    public async Task MigrateAsync_CrashAfterWriteBeforeDelete_OldRemainsReadable_AndRerunIsIdempotent()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        const string oldKey = "oldkey0000000000000000000000000000000000000000000000000000000000";
        const string newKey = "newkey0000000000000000000000000000000000000000000000000000000000";
        var state = new ReadingState { Path = fx.FilePath, LastPage = 9, LastAccessUtc = Now };

        // 模拟「写新成功、删旧前崩溃」：两键并存，旧键仍可读（回退安全）
        store.Save(oldKey, state);
        store.Save(newKey, state);
        Assert.NotNull(store.Load(oldKey));
        Assert.NotNull(store.Load(newKey));

        await store.MigrateAsync(oldKey, newKey); // 幂等重跑

        Assert.Null(store.Load(oldKey));
        Assert.Equal(9, store.Load(newKey)!.LastPage);
    }
}
