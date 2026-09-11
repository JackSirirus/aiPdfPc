// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T5 最近卡片进度元信息测试：镜像同步、去重保留、卡片文案（FR-33 / AC-33.2）。
using System.IO;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class RecentProgressTests
{
    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "aipdf-t5-recent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static StartPageViewModel NewVm(RecentFilesStore store)
        => new(Array.Empty<Tool>(), store);

    [Fact]
    public void UpdateProgress_UpdatesFields_AndPersists()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            var document = new RecentFilesDocument();
            document.Items.Add(new RecentFileEntry { Path = @"D:\docs\a.pdf", Name = "a.pdf", LastOpenedAt = DateTime.Now });
            store.Save(document);

            var ok = store.UpdateProgress(@"D:\docs\a.pdf", pageCount: 120, bytes: 2_500_000, lastPage: 47, lastZoom: 150);

            Assert.True(ok);
            var reloaded = new RecentFilesStore(dir).Load().Items.Single();
            Assert.Equal(120, reloaded.PageCount);
            Assert.Equal(2_500_000, reloaded.Bytes);
            Assert.Equal(47, reloaded.LastPage);
            Assert.Equal(150, reloaded.LastZoom);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UpdateProgress_UnknownPath_ReturnsFalse_NoNewEntry()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            Assert.False(store.UpdateProgress(@"D:\docs\missing.pdf", 1, 1, 1, 100));
            Assert.Empty(store.Load().Items);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UpdateProgress_ZeroValues_KeepExisting()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            var document = new RecentFilesDocument();
            document.Items.Add(new RecentFileEntry
            {
                Path = @"D:\docs\a.pdf", Name = "a.pdf", LastOpenedAt = DateTime.Now,
                PageCount = 10, Bytes = 1000, LastPage = 5, LastZoom = 120,
            });
            store.Save(document);

            store.UpdateProgress(@"D:\docs\a.pdf", pageCount: 0, bytes: 0, lastPage: 0, lastZoom: 0);

            var entry = store.Load().Items.Single();
            Assert.Equal(10, entry.PageCount);
            Assert.Equal(1000, entry.Bytes);
            Assert.Equal(5, entry.LastPage);
            Assert.Equal(120, entry.LastZoom);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RegisterOpened_Dedup_PreservesAndKeepsProgressMirror()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            var document = new RecentFilesDocument();
            document.Items.Add(new RecentFileEntry
            {
                Path = Path.Combine(dir, "a.pdf"), Name = "a.pdf", LastOpenedAt = DateTime.Now,
                PageCount = 12, Bytes = 2048, LastPage = 8, LastZoom = 140,
            });
            document.Items.Add(new RecentFileEntry { Path = Path.Combine(dir, "b.pdf"), Name = "b.pdf", LastOpenedAt = DateTime.Now });
            store.Save(document);

            var vm = NewVm(store);
            vm.RegisterOpened(Path.Combine(dir, "a.pdf")); // 重复打开（去重上浮）

            Assert.Equal(2, vm.RecentFiles.Count);
            var head = vm.RecentFiles[0];
            Assert.Equal("a.pdf", head.Name);
            Assert.Equal(12, head.PageCount);
            Assert.Equal(8, head.LastPage); // 进度镜像随条目保留，不被清零
            Assert.Equal(140, head.LastZoom);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RecentFileItem_MetaAndProgressText()
    {
        var item = new RecentFileItem(new RecentFileEntry
        {
            Path = @"D:\docs\report.pdf",
            Name = "report.pdf",
            PageCount = 120,
            Bytes = 2 * 1024 * 1024,
            LastPage = 47,
            LastZoom = 150,
        });

        Assert.True(item.HasMeta);
        Assert.True(item.HasProgress);
        Assert.Contains("120 页", item.MetaText);
        Assert.Contains("MB", item.MetaText);
        Assert.Contains("第 47 页", item.ProgressText);
        Assert.Contains("150%", item.ProgressText);
    }

    [Fact]
    public void RecentFileItem_NoProgress_HidesLines()
    {
        var item = new RecentFileItem(new RecentFileEntry { Path = @"D:\docs\new.pdf", Name = "new.pdf" });

        Assert.False(item.HasMeta);
        Assert.False(item.HasProgress);
        Assert.Equal("", item.MetaText);
        Assert.Equal("", item.ProgressText);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FormatBytes_FormatsHumanReadable(long bytes, string expected)
        => Assert.Equal(expected, RecentFileItem.FormatBytes(bytes));
}
