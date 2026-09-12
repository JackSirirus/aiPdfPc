// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T3 打印服务测试：
//   - 门禁（AC-30.2：未加载 / 加密未解锁 → 不可打印）；
//   - 打印使用 PrintRenderFlags（非 ScreenRenderFlags）→ 不进入屏幕 LRU（cacheable 语义）；
//   - 两阶段：后台预渲染到有界临时文件 + 分页器仅读产物（原生句柄已关后仍可用）；
//   - 内存有界（K 页窗口）；取消/异常清理临时文件；
//   - 句柄隔离（打印作业前后阅读器仍可渲染）；
//   - DPI 取设置并受钳制；进度回封送到调用方同步上下文。
// 全部使用合成 PDF（TestPdfFactory / 既有加密语料），不读个人文档。
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Windows.Documents;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class PrintServiceTests : IDisposable
{
    private const int PageCount = 12;
    private const double PageWidthPt = 200;
    private const double PageHeightPt = 240;
    private const string CorpusPassword = "SECRET-123";

    private static readonly int[] AllPages = Enumerable.Range(0, PageCount).ToArray();

    private readonly string _work;
    private readonly string _printRoot;
    private readonly string _settingsPath;
    private readonly string _docPath;

    public PrintServiceTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "aipdf-v3t3-" + Guid.NewGuid().ToString("N"));
        _printRoot = Path.Combine(_work, "print");
        Directory.CreateDirectory(_printRoot);
        _settingsPath = Path.Combine(_work, "settings.json");
        _docPath = Path.Combine(_work, "doc.pdf");
        File.WriteAllBytes(
            _docPath,
            TestPdfFactory.Create(Enumerable.Repeat((PageWidthPt, PageHeightPt), PageCount).ToArray()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- 辅助

    private SettingsStore CreateSettings(AppSettings? seed = null)
    {
        var store = new SettingsStore(_settingsPath);
        if (seed is not null) store.Save(seed);
        return store;
    }

    private PrintService CreateService(
        SessionCredentialStore? credentials = null,
        Func<PdfiumRenderService>? factory = null,
        AppSettings? seed = null)
        => new(CreateSettings(seed), credentials, factory, _printRoot);

    private static string[] PrintArtifactDirs(string printRoot)
        => Directory.GetDirectories(printRoot, "aipdf-print-*");

    /// <summary>在指定同步上下文下启动任务后立刻还原调用线程上下文，避免上下文泄漏到后续测试。</summary>
    private static T StartOnContext<T>(SynchronizationContext context, Func<T> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class DelegateProgress : IProgress<PrintProgress>
    {
        private readonly Action<PrintProgress> _onReport;

        public DelegateProgress(Action<PrintProgress>? onReport = null)
            => _onReport = onReport ?? (_ => { });

        public List<PrintProgress> Reports { get; } = [];

        public void Report(PrintProgress value)
        {
            Reports.Add(value);
            _onReport(value);
        }
    }

    /// <summary>自泵同步上下文：Post 在专属线程执行，模拟 UI Dispatcher 的线程亲缘（不创建 WPF Application）。</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _pump;
        private int _postCount;

        public RecordingSynchronizationContext()
        {
            _pump = new Thread(Pump) { IsBackground = true, Name = "aipdf-print-ui-context" };
            _pump.Start();
        }

        public int PostCount => Volatile.Read(ref _postCount);

        public int ThreadId => _pump.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        private void Pump()
        {
            SynchronizationContext.SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try { callback(state); }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _pump.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }
    }

    // ---------------------------------------------------------------- 门禁（AC-30.2）

    [Fact]
    public void Gate_NotLoaded_IsNotPrintable()
    {
        var decision = CreateService().EvaluateGate(null, isDocumentLoaded: false, isDocumentEncrypted: false);
        Assert.False(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.NotLoaded, decision.Status);
    }

    [Fact]
    public void Gate_LoadingInProgress_IsNotPrintable()
    {
        var decision = CreateService().EvaluateGate(_docPath, isDocumentLoaded: false, isDocumentEncrypted: false);
        Assert.False(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.NotLoaded, decision.Status);
    }

    [Fact]
    public void Gate_EncryptedWithoutStoredPassword_IsLocked()
    {
        var decision = CreateService(new SessionCredentialStore())
            .EvaluateGate(_docPath, isDocumentLoaded: true, isDocumentEncrypted: true);

        Assert.False(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.Locked, decision.Status);
    }

    [Fact]
    public void Gate_EncryptedWithStoredPassword_IsPrintable()
    {
        var credentials = new SessionCredentialStore();
        credentials.Store(_docPath, CorpusPassword);

        var decision = CreateService(credentials)
            .EvaluateGate(_docPath, isDocumentLoaded: true, isDocumentEncrypted: true);

        Assert.True(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.Printable, decision.Status);
    }

    [Fact]
    public void Gate_PlainLoaded_IsPrintable()
    {
        var decision = CreateService().EvaluateGate(_docPath, isDocumentLoaded: true, isDocumentEncrypted: false);
        Assert.True(decision.IsPrintable);
    }

    // ---------------------------------------------------------------- 范围 / 就绪 / 两阶段

    [Fact]
    public async Task Prepare_EmptyRange_ReturnsEmptyRange_AndLeavesNoArtifacts()
    {
        using var result = await CreateService().PrepareAsync(_docPath, []);

        Assert.Equal(PrintPrepareStatus.EmptyRange, result.Status);
        Assert.Null(result.Paginator);
        Assert.Empty(PrintArtifactDirs(_printRoot));
    }

    [Fact]
    public async Task Prepare_SelectedPages_ReturnsReadyPaginator_WithMatchingPageCount()
    {
        var factoryCalls = 0;
        var service = CreateService(factory: () => { factoryCalls++; return new PdfiumRenderService(); });

        var pages = PrintPageRange.Parse("1-3,5", PageCount).Pages;
        using var result = await service.PrepareAsync(_docPath, pages);

        Assert.True(result.IsReady);
        Assert.Equal(1, factoryCalls); // 每次作业仅自建一个专用实例
        var paginator = Assert.IsType<PrintPaginator>(result.Paginator);
        Assert.Equal(4, paginator.PageCount);

        // 阶段②产物可用；此时专用句柄已在作业 finally 内关闭（证明 GetPage 无原生调用）。
        var first = paginator.GetPage(0);
        Assert.NotSame(DocumentPage.Missing, first);
        Assert.Equal(200.0 * 96 / 72, first.Size.Width, 2);
        var last = paginator.GetPage(3);
        Assert.NotSame(DocumentPage.Missing, last);
    }

    [Fact]
    public async Task Prepare_UsesPrintRenderFlags_AndDoesNotTouchScreenLru()
    {
        PdfiumRenderService? captured = null;
        var service = CreateService(factory: () => captured = new PdfiumRenderService());

        using var result = await service.PrepareAsync(_docPath, AllPages);

        Assert.True(result.IsReady);
        Assert.NotNull(captured);
        Assert.NotEqual(PdfiumRenderService.PrintRenderFlags, PdfiumRenderService.ScreenRenderFlags);

        // 非 ScreenRenderFlags → cacheable=false：既不查缓存也不写缓存，Hits/Misses 恒为 0。
        var stats = captured!.GetLruStats(_docPath);
        Assert.Equal(0, stats.Hits);
        Assert.Equal(0, stats.Misses);
        Assert.Equal(0, stats.EntryCount);
    }

    [Fact]
    public async Task Prepare_RespectsSettingsPrintDpi()
    {
        var service = CreateService(seed: new AppSettings { PrintDpi = 300 });

        Assert.Equal(300, service.ResolvePrintDpi());

        using var result = await service.PrepareAsync(_docPath, [0]);
        var paginator = result.Paginator!;
        Assert.Equal(300, paginator.RenderDpi);

        // 200pt @300dpi = round(200*300/72) = 833px
        var (width, height) = paginator.FirstPagePixelSize;
        Assert.Equal(833, width);
        Assert.Equal(1000, height);
    }

    [Fact]
    public void ResolvePrintDpi_DefaultIs150_AndClampedBySettings()
    {
        Assert.Equal(150, CreateService().ResolvePrintDpi());

        var clamped = CreateService(seed: new AppSettings { PrintDpi = 9999 });
        Assert.Equal(600, clamped.ResolvePrintDpi());
    }

    // ---------------------------------------------------------------- 加密文档（打开期门禁）

    [Fact]
    public async Task Prepare_EncryptedWithoutPassword_IsNotPrintable()
    {
        var encryptedPath = AIPDF.Windows.Tests.Rendering.Corpus.Encrypted;

        using var result = await CreateService(new SessionCredentialStore())
            .PrepareAsync(encryptedPath, [0]);

        Assert.Equal(PrintPrepareStatus.NotPrintable, result.Status);
        Assert.Empty(PrintArtifactDirs(_printRoot));
    }

    [Fact]
    public async Task Prepare_EncryptedWithStoredPassword_IsReady()
    {
        var encryptedPath = AIPDF.Windows.Tests.Rendering.Corpus.Encrypted;
        var credentials = new SessionCredentialStore();
        credentials.Store(encryptedPath, CorpusPassword);

        using var result = await CreateService(credentials)
            .PrepareAsync(encryptedPath, [0, 1]);

        Assert.True(result.IsReady);
        Assert.Equal(2, result.Paginator!.PageCount);
    }

    // ---------------------------------------------------------------- 有界内存 / 临时文件

    [Fact]
    public async Task Prepare_BoundedMemory_WindowIsThreePages()
    {
        using var result = await CreateService().PrepareAsync(_docPath, AllPages);
        Assert.True(result.IsReady);

        var paginator = result.Paginator!;
        Assert.Equal(PrintArtifactStore.DefaultWindowSize, paginator.WindowSize);

        for (var i = 0; i < PageCount; i++)
        {
            Assert.NotSame(DocumentPage.Missing, paginator.GetPage(i));
            Assert.True(paginator.ResidentPageCount <= paginator.WindowSize,
                $"驻留页数 {paginator.ResidentPageCount} 超过窗口 {paginator.WindowSize}");
        }

        Assert.True(paginator.PeakResidentPages <= paginator.WindowSize,
            $"内存窗口峰值 {paginator.PeakResidentPages} 超过 K={paginator.WindowSize}");
    }

    [Fact]
    public async Task Prepare_TempArtifactsDeleted_OnPaginatorDispose()
    {
        var result = await CreateService().PrepareAsync(_docPath, AllPages);

        Assert.True(result.IsReady);
        Assert.Single(PrintArtifactDirs(_printRoot));

        result.Dispose();
        Assert.Empty(PrintArtifactDirs(_printRoot));
    }

    [Fact]
    public async Task Prepare_Cancel_CleansTempArtifacts_AndStopsRendering()
    {
        using var cts = new CancellationTokenSource();
        var progress = new DelegateProgress(p =>
        {
            if (p.CompletedPages == 2) cts.Cancel();
        });

        using var result = await CreateService()
            .PrepareAsync(_docPath, AllPages, progress, cts.Token);

        Assert.Equal(PrintPrepareStatus.Canceled, result.Status);
        Assert.Null(result.Paginator);
        Assert.Equal(2, progress.Reports.Count);           // 取消后不再渲染后续页
        Assert.Empty(PrintArtifactDirs(_printRoot));       // 半成品临时目录已删除
    }

    [Fact]
    public async Task Prepare_ExceptionDuringPreRender_CleansTempArtifacts_AndReturnsFailed()
    {
        var progress = new DelegateProgress(p =>
        {
            if (p.CompletedPages == 2) throw new InvalidOperationException("模拟预渲染中断");
        });

        using var result = await CreateService()
            .PrepareAsync(_docPath, AllPages, progress);

        Assert.Equal(PrintPrepareStatus.Failed, result.Status);
        Assert.Contains("模拟预渲染中断", result.Error);
        Assert.Empty(PrintArtifactDirs(_printRoot));
    }

    // ---------------------------------------------------------------- 句柄隔离（B2）

    [Fact]
    public async Task PrintJob_DoesNotCloseReaderHandle_ReaderStillRenders()
    {
        using var reader = new PdfiumRenderService();
        var readerDocId = reader.OpenDocument(_docPath);
        Assert.Equal(PageCount, reader.GetPageCount(readerDocId));

        var before = await reader.RenderPageAsync(readerDocId, 0, dpi: 96);
        Assert.True(before.PixelWidth > 0);

        PdfiumRenderService? printRenderer = null;
        var service = CreateService(factory: () => printRenderer = new PdfiumRenderService());

        using (var result = await service.PrepareAsync(_docPath, AllPages))
        {
            Assert.True(result.IsReady);
            Assert.NotNull(printRenderer);
            Assert.NotSame(reader, printRenderer); // 专用实例，绝不共用阅读器句柄
        }

        // 打印作业结束（专用实例已 Dispose）后，阅读器句柄与渲染能力完好。
        Assert.Equal(PageCount, reader.GetPageCount(readerDocId));
        var after = await reader.RenderPageAsync(readerDocId, 0, dpi: 96);
        Assert.True(after.PixelWidth > 0);
    }

    // ---------------------------------------------------------------- 线程封送（F3-BLK-1）

    [Fact]
    public async Task Progress_IsMarshaledToCapturedUiContext()
    {
        using var uiContext = new RecordingSynchronizationContext();
        var threadIds = new ConcurrentQueue<int>();
        var countdown = new CountdownEvent(PageCount);

        var progress = StartOnContext(uiContext, () => new Progress<PrintProgress>(_ =>
        {
            threadIds.Enqueue(Environment.CurrentManagedThreadId);
            countdown.Signal();
        }));

        using var result = await CreateService().PrepareAsync(_docPath, AllPages, progress);

        Assert.True(result.IsReady);
        Assert.True(countdown.Wait(TimeSpan.FromSeconds(10)), "进度回调未全部送达");
        Assert.All(threadIds, id => Assert.Equal(uiContext.ThreadId, id));
    }

    [Fact]
    public async Task Paginator_IsConstructedOnCallerSynchronizationContext()
    {
        using var uiContext = new RecordingSynchronizationContext();

        var task = StartOnContext(uiContext, () => CreateService().PrepareAsync(_docPath, AllPages));

        using var result = await task;
        Assert.True(result.IsReady);
        // 阶段②在调用方 await 上下文（生产 = UI 线程）构造分页器，而非后台渲染线程。
        Assert.Equal(uiContext.ThreadId, result.Paginator!.ConstructionThreadId);
    }
}
