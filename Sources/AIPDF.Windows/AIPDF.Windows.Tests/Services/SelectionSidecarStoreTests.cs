// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T19 替换标注 sidecar 存储单测：只存 {页码,坐标,译文,语言对,时间戳}、源目录隐藏文件、
// 源目录不可写回退 %TEMP%、按文档清理、仅会话内有效（新实例不自动加载旧文件）。
using System.IO;
using System.Text.Json;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class SelectionSidecarStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _document;
    private readonly string _documentB;

    public SelectionSidecarStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aipdf-sidecar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _document = Path.Combine(_root, "report.pdf");
        _documentB = Path.Combine(_root, "other.pdf");
        File.WriteAllText(_document, "%PDF-1.4 synthetic");
        File.WriteAllText(_documentB, "%PDF-1.4 synthetic");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理尽力而为。
        }
    }

    private string SourceSidecar => Path.Combine(_root, "." + Path.GetFileName(_document) + SelectionSidecarStore.FileSuffix);

    private string SourceSidecarB => Path.Combine(_root, "." + Path.GetFileName(_documentB) + SelectionSidecarStore.FileSuffix);

    private string TempRoot() => Path.Combine(_root, "temp-fallback");

    private static PdfRect Rect() => new(10, 20, 60, 32);

    [Fact]
    public void Add_StoresEntry_AndGetReturnsByPage()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };

        var result = store.Add(0, Rect(), "你好", "en→zh");

        Assert.True(result.Ok);
        var entry = Assert.Single(store.Get(0));
        Assert.Equal(0, entry.PageIndex);
        Assert.Equal(Rect(), entry.Rect);
        Assert.Equal("你好", entry.Translation);
        Assert.Equal("en→zh", entry.LangPair);
        Assert.NotEqual(default, entry.Timestamp);
        Assert.Empty(store.Get(1));
        Assert.Equal(store.Get(0), store.GetAll());
    }

    [Fact]
    public void Add_WritesHiddenSidecarFile_NextToSource()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };

        Assert.True(store.Add(0, Rect(), "你好", "en→zh").Ok);

        Assert.True(File.Exists(SourceSidecar));
        if (OperatingSystem.IsWindows())
        {
            Assert.True((File.GetAttributes(SourceSidecar) & FileAttributes.Hidden) != 0);
        }
    }

    [Fact]
    public void Add_PersistsOnlyAllowedFields_WithoutSourceText()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };
        store.Add(3, Rect(), "你好", "en→zh");

        using var document = JsonDocument.Parse(File.ReadAllText(SourceSidecar));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        var entry = root.GetProperty("annotations")[0];
        var keys = entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["bottom", "langPair", "left", "page", "right", "timestamp", "top", "translation"], keys);
        Assert.Equal(3, entry.GetProperty("page").GetInt32());

        var json = File.ReadAllText(SourceSidecar);
        Assert.DoesNotContain("sourceText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("原文全文", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_FallsBackToTempDir_WhenSourceDirNotWritable()
    {
        var tempRoot = TempRoot();
        var store = new SelectionSidecarStore(tempRoot, _ => false) { ActiveDocument = _document };

        var result = store.Add(0, Rect(), "你好", "en→zh");

        Assert.True(result.Ok);
        Assert.False(File.Exists(SourceSidecar));
        var files = Directory.GetFiles(tempRoot, "*" + SelectionSidecarStore.FileSuffix);
        Assert.Single(files);
        Assert.DoesNotContain(Path.GetFileName(_document), Path.GetFileName(files[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Add_WithoutActiveDocument_Fails()
    {
        var store = new SelectionSidecarStore(TempRoot());

        var result = store.Add(0, Rect(), "你好", "en→zh");

        Assert.False(result.Ok);
        Assert.Contains("没有打开的文档", result.Message);
    }

    [Fact]
    public void Add_EmptyTranslation_Fails()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };

        var result = store.Add(0, Rect(), "   ", "en→zh");

        Assert.False(result.Ok);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void GetAll_IsScopedToActiveDocument()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };
        store.Add(0, Rect(), "A 译文", "en→zh");

        store.ActiveDocument = _documentB;
        store.Add(0, Rect(), "B 译文", "en→zh");

        var b = Assert.Single(store.GetAll());
        Assert.Equal("B 译文", b.Translation);

        store.ActiveDocument = _document;
        var a = Assert.Single(store.GetAll());
        Assert.Equal("A 译文", a.Translation);
    }

    [Fact]
    public void ClearFor_RemovesOnlyThatDocument_AndDeletesItsFile()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };
        store.Add(0, Rect(), "A 译文", "en→zh");
        store.ActiveDocument = _documentB;
        store.Add(0, Rect(), "B 译文", "en→zh");
        Assert.True(File.Exists(SourceSidecar));
        Assert.True(File.Exists(SourceSidecarB));

        store.ClearFor(_document);

        Assert.False(File.Exists(SourceSidecar));
        Assert.True(File.Exists(SourceSidecarB));
        Assert.Single(store.GetAll()); // 活动文档 B 不受影响
    }

    [Fact]
    public void Clear_RemovesAllDocumentsAndDeletesFiles()
    {
        var store = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };
        store.Add(0, Rect(), "A 译文", "en→zh");
        store.ActiveDocument = _documentB;
        store.Add(0, Rect(), "B 译文", "en→zh");

        store.Clear();

        Assert.Null(store.ActiveDocument);
        Assert.Empty(store.GetAll());
        Assert.False(File.Exists(SourceSidecar));
        Assert.False(File.Exists(SourceSidecarB));
    }

    [Fact]
    public void NewStore_DoesNotAutoLoadExistingSidecarFile()
    {
        var first = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };
        first.Add(0, Rect(), "会话内译文", "en→zh");
        Assert.True(File.Exists(SourceSidecar));

        var second = new SelectionSidecarStore(TempRoot()) { ActiveDocument = _document };

        Assert.Empty(second.GetAll()); // 仅会话内有效：新实例不自动加载旧 sidecar
    }
}
