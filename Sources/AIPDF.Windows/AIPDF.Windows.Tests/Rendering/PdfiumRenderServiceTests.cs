// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T1 渲染服务测试。语料：tmp/pdfium-spike/gen_corpus.py 生成的合成文件（不提交，不读个人文档）。
using System.IO;
using System.Windows.Media;
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Rendering;

/// <summary>
/// 语料定位：优先仓库 tmp/pdfium-spike/corpus；缺失时尝试用 gen_corpus.py 重新生成。
/// 全部为合成文件。
/// </summary>
public static class Corpus
{
    private static readonly object GenLock = new();
    private static string? _corpusDir;

    public static string Dir
    {
        get
        {
            if (_corpusDir is not null) return _corpusDir;
            lock (GenLock)
            {
                if (_corpusDir is not null) return _corpusDir;
                // 测试输出目录: .../bin/Debug/net8.0-windows → 仓库根在若干级之上
                var dir = AppContext.BaseDirectory;
                string? repoRoot = null;
                var cur = new DirectoryInfo(dir);
                for (var i = 0; i < 8 && cur is not null; i++)
                {
                    if (File.Exists(Path.Combine(cur.FullName, "tmp", "pdfium-spike", "gen_corpus.py")))
                    {
                        repoRoot = cur.FullName;
                        break;
                    }
                    cur = cur.Parent!;
                }
                Assert.NotNull(repoRoot);
                var corpus = Path.Combine(repoRoot!, "tmp", "pdfium-spike", "corpus");
                if (!File.Exists(Path.Combine(corpus, "mixed100.pdf")))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{Path.Combine(repoRoot!, "tmp", "pdfium-spike", "gen_corpus.py")}\" \"{corpus}\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    p.WaitForExit(120_000);
                    Assert.True(File.Exists(Path.Combine(corpus, "mixed100.pdf")), "语料生成失败");
                }
                _corpusDir = corpus;
                return corpus;
            }
        }
    }

    public static string Mixed100 => Path.Combine(Dir, "mixed100.pdf");
    public static string Cjk500 => Path.Combine(Dir, "cjk500.pdf");
    public static string Encrypted => Path.Combine(Dir, "encrypted.pdf");
    public static string GarbageHeader => Path.Combine(Dir, "garbage-header.pdf");
    public static string HugePage => Path.Combine(Dir, "hugepage.pdf");
}

public sealed class PdfiumRenderServiceTests
{
    private const string Password = "SECRET-123";
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PdfiumRenderServiceTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void OpenDocument_ReturnsId_AndPageCountAndSizes()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(Corpus.Mixed100);
        Assert.Equal(Path.GetFullPath(Corpus.Mixed100), docId);
        Assert.Equal(100, svc.GetPageCount(docId));
        // A4: 595x842 pt
        var size = svc.GetPageSize(docId, 0);
        Assert.NotNull(size);
        Assert.Equal(595, size!.Value.Width, 1);
        Assert.Equal(842, size!.Value.Height, 1);
    }

    [Fact]
    public void OpenDocument_DeduplicatesSamePath()
    {
        using var svc = new PdfiumRenderService();
        var id1 = svc.OpenDocument(Corpus.Mixed100);
        var id2 = svc.OpenDocument(Corpus.Mixed100);
        Assert.Equal(id1, id2);
        svc.CloseDocument(id1);
        // 关闭一次后句柄已释放；再次打开应成功（去重表已移除）
        var id3 = svc.OpenDocument(Corpus.Mixed100);
        Assert.Equal(id1, id3);
    }

    [Fact]
    public void PageSize_ParsedOnceAtOpen_NoReparseOnQuery()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(Corpus.Cjk500);
        // 500 页打开即解析；查询只是读缓存
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            var s = svc.GetPageSize(docId, i);
            Assert.True(s is { Width: > 0, Height: > 0 });
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"500 次尺寸查询耗时 {sw.ElapsedMilliseconds}ms，疑似重新解析");
    }

    [Fact]
    public async Task RenderPageAsync_ReturnsFrozenBitmap()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(Corpus.Mixed100);
        var bmp = await svc.RenderPageAsync(docId, 0, dpi: 96);
        Assert.True(bmp.IsFrozen, "BitmapSource 必须冻结才能跨线程绑定");
        Assert.True(bmp.PixelWidth > 0 && bmp.PixelHeight > 0);
        // 96dpi A4 ≈ 793x1123
        Assert.InRange(bmp.PixelWidth, 700, 900);
        Assert.InRange(bmp.PixelHeight, 1000, 1250);
        // 二次渲染命中缓存
        var bmp2 = await svc.RenderPageAsync(docId, 0, dpi: 96);
        Assert.Same(bmp, bmp2);
        var stats = svc.GetLruStats(docId);
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public async Task Lru_Evicts_WhenBudgetExceeded()
    {
        // 预算调小强制淘汰：A4 96dpi ≈ 793*1123*4 ≈ 3.5MB/页 → 2MB 预算只驻留 1 页
        using var svc = new PdfiumRenderService(byteBudget: 2 * 1024 * 1024);
        var docId = svc.OpenDocument(Corpus.Mixed100);
        for (var i = 0; i < 3; i++)
        {
            var bmp = await svc.RenderPageAsync(docId, i, dpi: 96);
            Assert.NotNull(bmp);
        }

        var stats = svc.GetLruStats(docId);
        Assert.True(stats.Evictions >= 1, $"预期发生淘汰，实际 evictions={stats.Evictions}");
        Assert.True(stats.EstimatedBytes <= 2L * 1024 * 1024 + 4L * 1024 * 1024,
            $"驻留字节超预算过多: {stats.EstimatedBytes}");
        Assert.True(stats.EntryCount <= 2, $"驻留页数过多: {stats.EntryCount}");
    }

    [Fact]
    public async Task ConcurrentRender_2ThreadsX50Pages_NoDeadlock()
    {
        using var svc = new PdfiumRenderService();
        var swOpen = System.Diagnostics.Stopwatch.StartNew();
        var docId = svc.OpenDocument(Corpus.Cjk500);
        swOpen.Stop();
        _output.WriteLine($"open 500 页文档（句柄+尺寸解析）: {swOpen.ElapsedMilliseconds} ms");
        Assert.True(swOpen.ElapsedMilliseconds < 1000, $"500 页打开耗时 {swOpen.ElapsedMilliseconds}ms 应 <1s（AC）");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allDone = Task.WhenAll(
            Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var bmp = await svc.RenderPageAsync(docId, i, dpi: 96);
                    Assert.NotNull(bmp);
                }
            }),
            Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var bmp = await svc.RenderPageAsync(docId, i, dpi: 150);
                    Assert.NotNull(bmp);
                }
            }));

        var completed = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromMinutes(5)));
        sw.Stop();
        _output.WriteLine($"并发 2 线程 × 50 页渲染总耗时: {sw.ElapsedMilliseconds} ms（100 页冷渲染）");
        Assert.Same(allDone, completed); // 超时=死锁
        var stats = svc.GetLruStats(docId);
        Assert.Equal(100, stats.Misses);
        Assert.True(stats.EntryCount > 0);
        _output.WriteLine($"LRU: hits={stats.Hits} misses={stats.Misses} evictions={stats.Evictions} entries={stats.EntryCount} bytes={stats.EstimatedBytes}");

        // 单页热渲染量级对照（spike 参考 96dpi ~4ms）
        var swHot = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
            await svc.RenderPageAsync(docId, 0, dpi: 96);
        swHot.Stop();
        _output.WriteLine($"热缓存单页 96dpi ×30 平均: {swHot.ElapsedMilliseconds / 30.0:F2} ms");
    }

    [Fact]
    public async Task Render_Clamps_WhenExceedingTextureLimit()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(Corpus.HugePage); // 10000x10000 pt
        var bmp = await svc.RenderPageAsync(docId, 0, dpi: 96);
        Assert.True(bmp.PixelWidth <= PdfiumRenderService.MaxTextureEdge);
        Assert.True(bmp.PixelHeight <= PdfiumRenderService.MaxTextureEdge);
        Assert.Equal(1, svc.ClampedCount);
    }

    [Fact]
    public void OpenDocument_CorruptFile_ThrowsPdfiumOpenException()
    {
        using var svc = new PdfiumRenderService();
        var ex = Assert.Throws<PdfiumOpenException>(() => svc.OpenDocument(Corpus.GarbageHeader));
        Assert.NotEqual(0UL, ex.ErrorCode);
        // 句柄表无泄漏：损坏路径不在文档表中
        Assert.Equal(-1, svc.GetPageCount(Path.GetFullPath(Corpus.GarbageHeader)));
    }

    [Fact]
    public void OpenDocument_MissingFile_ThrowsPdfiumOpenException()
    {
        using var svc = new PdfiumRenderService();
        var missing = Path.Combine(Path.GetTempPath(), "aipdf-test-nonexistent-" + Guid.NewGuid().ToString("N") + ".pdf");
        Assert.Throws<PdfiumOpenException>(() => svc.OpenDocument(missing));
    }

    [Fact]
    public async Task OpenDocument_Encrypted_WrongPassword_Throws()
    {
        using var svc = new PdfiumRenderService();
        await Assert.ThrowsAsync<PdfiumOpenException>(async () =>
        {
            // OpenDocument 是同步方法，直接断言同步异常
            svc.OpenDocument(Corpus.Encrypted, "wrong-password");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OpenDocument_Encrypted_CorrectPassword_Opens()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(Corpus.Encrypted, Password);
        Assert.Equal(10, svc.GetPageCount(docId));
        // 密码不存储：无法从服务侧取回（无任何密码暴露 API——编译期保证，此处验证打开后正常渲染）
        var bmp = await svc.RenderPageAsync(docId, 0, dpi: 96);
        Assert.True(bmp.PixelWidth > 0);
    }
}
