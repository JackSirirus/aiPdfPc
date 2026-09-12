// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T3 打印服务：两阶段分页（FR-30 / AC-30.1 / AC-30.2）。
//
// 决策记录（计划「打印分页与打印服务 PrintService」，尖刺 #3）：
//   ① 后台预渲染选定页到【有界临时文件】——显式打印旗标
//      RenderPageAsync(docId, page, dpi, 100, PrintRenderFlags)（FPDF_ANNOT | FPDF_PRINTING，非
//      ScreenRenderFlags → 不进屏幕 LRU，绝不污染阅读器位图缓存）；内存窗口 K=3 页（解码缓存上限）；
//      逐页检查 CancellationToken 并上报进度。
//   ② 在调用方同步上下文（生产 = UI 线程）构造 WPF DocumentPaginator：PageCount = 预渲染页数，
//      GetPage 只读取已落盘的临时产物（无原生调用、无 PDFium 渲染、不阻塞）。
//   不采用：GetPage 内同步渲染（PrintDocument 同步拉页 → UI 线程做原生调用）；
//   不预渲染进内存（A4@150 ≈ 8.7MB/页 → 100 页 ≈ 870MB，且打印位图非可缓存）。
//
// 句柄所有权（B2）：打印作业自建自销一个【专用】PdfiumRenderService（打开→渲染→作业结束 Dispose），
//   绝不共享/关闭阅读器句柄；作业期间与结束后阅读器均可继续渲染。
// 线程纪律（F3-BLK-1）：所有 FPDF_* 都在 PdfiumRenderService 的 PdfiumLock.Global 内（本文件不直接
//   调用 FPDF_*）；进度经调用方传入的 IProgress<T> 上报（生产用 UI 线程创建的 Progress<T> 自动回封送），
//   分页器在调用方 await 上下文上构造——服务自身不触碰任何 UI 绑定状态。
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Services;

/// <summary>打印可打印性门禁状态（AC-30.2）。</summary>
public enum PrintGateStatus
{
    /// <summary>可打印。</summary>
    Printable,

    /// <summary>文档未加载/加载中/无文档。</summary>
    NotLoaded,

    /// <summary>文档已加密且会话内未解锁（无 SessionCredentialStore 密码）。</summary>
    Locked,
}

/// <summary>打印门禁决策结果（不可打印时携带中文原因，UI 据此禁用入口/提示）。</summary>
public sealed class PrintGateDecision
{
    private PrintGateDecision(PrintGateStatus status, string? message)
    {
        Status = status;
        Message = message;
    }

    /// <summary>是否可打印。</summary>
    public bool IsPrintable => Status == PrintGateStatus.Printable;

    /// <summary>门禁状态。</summary>
    public PrintGateStatus Status { get; }

    /// <summary>不可打印原因（中文）；可打印时为 null。</summary>
    public string? Message { get; }

    /// <summary>可打印（单例）。</summary>
    public static readonly PrintGateDecision Printable = new(PrintGateStatus.Printable, null);

    public static PrintGateDecision NotLoaded(string message) => new(PrintGateStatus.NotLoaded, message);

    public static PrintGateDecision Locked(string message) => new(PrintGateStatus.Locked, message);
}

/// <summary>预渲染进度（<see cref="IProgress{T}"/> 载荷）。CompletedPages 从 1 递增到 TotalPages。</summary>
public sealed record PrintProgress(int CompletedPages, int TotalPages, int CurrentPageIndex);

/// <summary>打印准备（阶段①）结果状态。</summary>
public enum PrintPrepareStatus
{
    /// <summary>已就绪：<see cref="PrintPrepareResult.Paginator"/> 可交给 PrintDocument。</summary>
    Ready,

    /// <summary>门禁不通过（未加载/加密未解锁/打开失败）。</summary>
    NotPrintable,

    /// <summary>未选择任何页。</summary>
    EmptyRange,

    /// <summary>用户取消。</summary>
    Canceled,

    /// <summary>渲染/落盘失败。</summary>
    Failed,
}

/// <summary>
/// 打印准备结果。就绪时携带 <see cref="PrintPaginator"/>（持有临时产物，由 <see cref="Dispose"/> 清理）；
/// 其余状态携带中文原因。无论成功与否，打印作业的专用渲染句柄都已在作业内关闭。
/// </summary>
public sealed class PrintPrepareResult : IDisposable
{
    private PrintPrepareResult(PrintPrepareStatus status, string? error, PrintPaginator? paginator)
    {
        Status = status;
        Error = error;
        Paginator = paginator;
    }

    /// <summary>结果状态。</summary>
    public PrintPrepareStatus Status { get; }

    /// <summary>失败/取消/不可打印原因（中文）；就绪时为 null。</summary>
    public string? Error { get; }

    /// <summary>就绪时的分页器；其余状态为 null。</summary>
    public PrintPaginator? Paginator { get; }

    /// <summary>是否就绪。</summary>
    public bool IsReady => Status == PrintPrepareStatus.Ready;

    internal static PrintPrepareResult Ready(PrintPaginator paginator) =>
        new(PrintPrepareStatus.Ready, null, paginator);

    internal static PrintPrepareResult NotPrintable(string error) =>
        new(PrintPrepareStatus.NotPrintable, error, null);

    internal static PrintPrepareResult EmptyRange(string error) =>
        new(PrintPrepareStatus.EmptyRange, error, null);

    internal static PrintPrepareResult Canceled() =>
        new(PrintPrepareStatus.Canceled, null, null);

    internal static PrintPrepareResult Failed(string error) =>
        new(PrintPrepareStatus.Failed, error, null);

    /// <summary>释放分页器及其临时产物（无分页器时无操作）。</summary>
    public void Dispose() => Paginator?.Dispose();
}

/// <summary>
/// 打印产物存储：把预渲染页落盘为 PNG（每页一个文件），并以 K 页上限的解码缓存提供服务。
/// 【内存有界】解码缓存不超过 <see cref="WindowSize"/>（默认 K=3）页；磁盘占用 = 选定页数（作业结束后删除）。
/// 【无原生依赖】<see cref="LoadPage"/> 仅读 PNG 文件（无 PDFium 调用），可在原生句柄关闭后继续工作。
/// </summary>
internal sealed class PrintArtifactStore : IDisposable
{
    /// <summary>默认内存窗口（K 页）。</summary>
    public const int DefaultWindowSize = 3;

    private readonly object _gate = new();
    private readonly int _windowSize;
    private readonly List<ArtifactEntry> _entries = [];
    private readonly Dictionary<int, BitmapSource> _residentBitmaps = new();
    private readonly Dictionary<int, LinkedListNode<int>> _residentNodes = new();
    private readonly LinkedList<int> _lru = new(); // 队尾 = 最近使用
    private int _peakResident;
    private bool _disposed;

    internal PrintArtifactStore(string tempRoot, int windowSize = DefaultWindowSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        _windowSize = Math.Max(1, windowSize);
        DirectoryPath = Path.Combine(tempRoot, "aipdf-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>本次作业的临时目录（内含每页 PNG；Dispose 时整目录删除）。</summary>
    public string DirectoryPath { get; }

    /// <summary>已落盘页数。</summary>
    public int PageCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>当前驻留内存的解码页数（≤ WindowSize）。</summary>
    public int ResidentPageCount
    {
        get { lock (_gate) return _residentBitmaps.Count; }
    }

    /// <summary>驻留内存页数的历史峰值（测试断言内存有界用）。</summary>
    public int PeakResidentPages
    {
        get { lock (_gate) return _peakResident; }
    }

    /// <summary>内存窗口大小（K）。</summary>
    public int WindowSize => _windowSize;

    /// <summary>首页 DIP 尺寸（pt→96/72）。</summary>
    public Size FirstPageSizeDip
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0 ? _entries[0].SizeDip : new Size(0, 0);
            }
        }
    }

    /// <summary>首页渲染像素尺寸（验证打印 DPI 生效用）。</summary>
    public (int Width, int Height) FirstPagePixelSize
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0 ? _entries[0].PixelSize : (0, 0);
            }
        }
    }

    /// <summary>按顺序追加一页（PNG 编码 + 落盘）。编码在调用方后台线程执行，此处不做 UI 工作。</summary>
    public void AddPage(BitmapSource bitmap, Size sizeDip)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        int index;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            index = _entries.Count;
        }

        var file = Path.Combine(DirectoryPath, $"page-{index:D6}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.Add(new ArtifactEntry(file, sizeDip, (bitmap.PixelWidth, bitmap.PixelHeight)));
        }
    }

    /// <summary>
    /// 读取第 <paramref name="index"/> 页的解码位图（首次解码后进入 K 页窗口缓存）。
    /// 【无原生调用】只读 PNG 文件；越界/已释放抛异常（调用方分页器已先行判定越界）。
    /// </summary>
    public (BitmapSource Bitmap, Size SizeDip) LoadPage(int index)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= _entries.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "打印页索引越界");

            var entry = _entries[index];

            if (_residentBitmaps.TryGetValue(index, out var cached))
            {
                TouchLru(index);
                return (cached, entry.SizeDip);
            }

            var bitmap = DecodePng(entry.FilePath);
            while (_residentBitmaps.Count >= _windowSize) EvictOldest();
            _residentBitmaps[index] = bitmap;
            _residentNodes[index] = _lru.AddLast(index);
            if (_residentBitmaps.Count > _peakResident) _peakResident = _residentBitmaps.Count;
            return (bitmap, entry.SizeDip);
        }
    }

    /// <summary>删除临时目录（尽力而为；取消/异常/作业结束均调用）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _residentBitmaps.Clear();
            _residentNodes.Clear();
            _lru.Clear();
            _entries.Clear();
        }

        try
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响主流程（系统临时目录最终会被清理）
        }
    }

    private void TouchLru(int index)
    {
        if (_residentNodes.TryGetValue(index, out var node) && node.List is not null)
        {
            _lru.Remove(node);
            _lru.AddLast(node);
        }
    }

    private void EvictOldest()
    {
        var first = _lru.First;
        if (first is null) return;
        _lru.RemoveFirst();
        _residentBitmaps.Remove(first.Value);
        _residentNodes.Remove(first.Value);
    }

    private static BitmapSource DecodePng(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private sealed record ArtifactEntry(string FilePath, Size SizeDip, (int Width, int Height) PixelSize);
}

/// <summary>
/// 两阶段打印分页器：PageCount = 预渲染页数；<see cref="GetPage"/> 仅从临时产物构造页面
/// （无原生调用、无 PDFium 渲染）。在调用方同步上下文（生产 = UI 线程）构造；
/// <see cref="Dispose"/> 删除临时产物。
/// </summary>
public sealed class PrintPaginator : DocumentPaginator, IDisposable
{
    private readonly PrintArtifactStore _store;
    private Size _pageSize;
    private bool _disposed;

    internal PrintPaginator(PrintArtifactStore store, Size pageSize)
    {
        _store = store;
        _pageSize = pageSize;
        ConstructionThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>分页器构造线程（测试断言其发生在调用方 await 上下文 = 生产 UI 线程）。</summary>
    internal int ConstructionThreadId { get; }

    /// <summary>预渲染使用的打印 DPI（供 UI 提示/诊断）。</summary>
    internal int RenderDpi { get; init; }

    /// <summary>内存窗口峰值（测试断言内存有界）。</summary>
    internal int PeakResidentPages => _store.PeakResidentPages;

    /// <summary>当前驻留内存的解码页数。</summary>
    internal int ResidentPageCount => _store.ResidentPageCount;

    /// <summary>内存窗口大小（K）。</summary>
    internal int WindowSize => _store.WindowSize;

    /// <summary>首页渲染像素尺寸（验证 DPI 生效）。</summary>
    internal (int Width, int Height) FirstPagePixelSize => _store.FirstPagePixelSize;

    /// <summary>临时产物目录（诊断用）。</summary>
    internal string ArtifactDirectory => _store.DirectoryPath;

    public override bool IsPageCountValid => true;

    public override int PageCount => _store.PageCount;

    public override Size PageSize
    {
        get => _pageSize;
        set => _pageSize = value;
    }

    public override IDocumentPaginatorSource? Source => null;

    /// <summary>
    /// 由已预渲染的临时产物构造一页：解码 PNG（非原生）+ 绘制进 DrawingVisual。
    /// 绝不做 PDFium 渲染或任何阻塞式原生调用；越界返回 <see cref="DocumentPage.Missing"/>。
    /// </summary>
    public override DocumentPage GetPage(int pageNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pageNumber < 0 || pageNumber >= _store.PageCount) return DocumentPage.Missing;

        var (bitmap, sizeDip) = _store.LoadPage(pageNumber);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, new Rect(0, 0, sizeDip.Width, sizeDip.Height));
        }

        return new DocumentPage(visual, sizeDip, new Rect(sizeDip), new Rect(0, 0, sizeDip.Width, sizeDip.Height));
    }

    /// <summary>删除临时产物（打印结束/取消/异常后由宿主在 finally 调用）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
    }
}

/// <summary>
/// 打印服务（FR-30）。职责：
///   1. <see cref="EvaluateGate"/>：AC-30.2 门禁（未加载 / 加密未解锁 → 不可打印）；
///   2. <see cref="PrepareAsync"/>：后台预渲染选定页到有界临时文件（阶段①），
///      再在调用方上下文构造 <see cref="PrintPaginator"/>（阶段②）；
///   3. 打印 DPI 取 <see cref="SettingsStore"/>（默认 150），像素边由渲染服务 MaxTextureEdge 钳制。
/// 【句柄】每次作业自建自销一个专用 <see cref="PdfiumRenderService"/>（B2），不触碰阅读器句柄；
/// 作业结束（含取消/异常）后该专用句柄必被 Dispose。
/// </summary>
public sealed class PrintService
{
    private const string MessageNotLoaded = "当前没有可打印的文档";
    private const string MessageLocked = "文档已加密且未解锁，无法打印";
    private const string MessageEmptyRange = "请选择至少一页进行打印";
    private const string MessagePageOutOfRange = "打印页码超出文档范围";
    private const string MessageDocumentUnavailable = "文档无法打开，暂时无法打印";

    /// <summary>点（pt）→ DIP（96/72）换算系数。</summary>
    private const double PointToDip = 96.0 / 72.0;

    private readonly SettingsStore _settings;
    private readonly SessionCredentialStore? _credentials;
    private readonly Func<PdfiumRenderService> _renderServiceFactory;
    private readonly string _tempRoot;

    /// <param name="settings">设置存储（读取 <see cref="AppSettings.PrintDpi"/>）；null → 默认生产存储。</param>
    /// <param name="credentials">会话密码驻留（判断加密文档是否已解锁）；null → 视为无密码。</param>
    /// <param name="renderServiceFactory">专用渲染实例工厂（默认 <c>new PdfiumRenderService()</c>；测试注入以观测 LRU）。</param>
    /// <param name="tempRoot">打印临时产物根目录；null → 系统临时目录。</param>
    public PrintService(
        SettingsStore? settings = null,
        SessionCredentialStore? credentials = null,
        Func<PdfiumRenderService>? renderServiceFactory = null,
        string? tempRoot = null)
    {
        _settings = settings ?? new SettingsStore();
        _credentials = credentials;
        _renderServiceFactory = renderServiceFactory ?? (() => new PdfiumRenderService());
        _tempRoot = string.IsNullOrWhiteSpace(tempRoot) ? Path.GetTempPath() : tempRoot;
    }

    /// <summary>解析打印渲染 DPI（设置已钳制 [72,600]；≤0 回退默认 150）。</summary>
    public int ResolvePrintDpi()
    {
        var dpi = _settings.Load().PrintDpi;
        return dpi > 0 ? dpi : AppSettings.PrintDpiDefault;
    }

    /// <summary>
    /// AC-30.2 门禁：未加载/加载中 → <see cref="PrintGateStatus.NotLoaded"/>；
    /// 已加密且 <see cref="SessionCredentialStore"/> 无该文档密码 → <see cref="PrintGateStatus.Locked"/>。
    /// </summary>
    /// <param name="documentPath">当前文档路径（可为 null）。</param>
    /// <param name="isDocumentLoaded">阅读器是否已完成加载（加载中为 false）。</param>
    /// <param name="isDocumentEncrypted">当前文档是否为加密文档（由阅读器告知）。</param>
    public PrintGateDecision EvaluateGate(string? documentPath, bool isDocumentLoaded, bool isDocumentEncrypted)
    {
        if (!isDocumentLoaded || string.IsNullOrWhiteSpace(documentPath))
            return PrintGateDecision.NotLoaded(MessageNotLoaded);

        if (isDocumentEncrypted && _credentials?.Get(documentPath) is null)
            return PrintGateDecision.Locked(MessageLocked);

        return PrintGateDecision.Printable;
    }

    /// <summary>
    /// 两阶段打印准备。阶段①在后台线程预渲染选定页到有界临时文件（可取消、上报进度）；
    /// 阶段②在调用方 await 上下文（生产 = UI 线程）构造分页器。
    /// 返回就绪结果时专用渲染句柄已关闭，分页器仅依赖临时产物；失败/取消路径已清理临时文件。
    /// </summary>
    /// <param name="documentPath">文档路径。</param>
    /// <param name="pageIndices">选定页（0 基，来自 <see cref="PrintPageRange"/>）。</param>
    /// <param name="progress">进度上报；生产请用 UI 线程创建的 <see cref="Progress{T}"/>（自动回封送，F3-BLK-1）。</param>
    /// <param name="cancellationToken">取消令牌（逐页检查）。</param>
    public async Task<PrintPrepareResult> PrepareAsync(
        string documentPath,
        IReadOnlyList<int> pageIndices,
        IProgress<PrintProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
            return PrintPrepareResult.NotPrintable(MessageNotLoaded);
        if (pageIndices is null || pageIndices.Count == 0)
            return PrintPrepareResult.EmptyRange(MessageEmptyRange);

        var dpi = ResolvePrintDpi();

        // 专用实例（B2）：本作业自建自销，绝不触碰阅读器句柄。
        var renderer = _renderServiceFactory();
        PrintArtifactStore? store = null;
        try
        {
            string docId;
            try
            {
                var password = _credentials?.Get(documentPath);
                docId = renderer.OpenDocument(documentPath, password);
            }
            catch (PdfiumOpenException ex) when (ex.ErrorCode == PdfiumRenderService.PdfiumErrorPassword)
            {
                return PrintPrepareResult.NotPrintable(MessageLocked);
            }

            var pageCount = renderer.GetPageCount(docId);
            foreach (var pageIndex in pageIndices)
            {
                if (pageIndex < 0 || pageIndex >= pageCount)
                    return PrintPrepareResult.Failed(MessagePageOutOfRange);
            }

            store = new PrintArtifactStore(_tempRoot);
            try
            {
                await PreRenderAsync(renderer, docId, pageIndices, dpi, store, progress, cancellationToken)
                    .ConfigureAwait(true); // 续体回到调用方上下文 → 阶段②在 UI 线程构造分页器
            }
            catch (OperationCanceledException)
            {
                return PrintPrepareResult.Canceled();
            }
            catch (Exception ex)
            {
                return PrintPrepareResult.Failed(ex.Message);
            }

            // 阶段②：仅持有临时产物，无原生调用；所有权移交分页器（finally 不再清理）。
            var paginator = new PrintPaginator(store, store.FirstPageSizeDip) { RenderDpi = dpi };
            store = null;
            return PrintPrepareResult.Ready(paginator);
        }
        catch (OperationCanceledException)
        {
            return PrintPrepareResult.Canceled();
        }
        catch (PdfiumOpenException)
        {
            return PrintPrepareResult.NotPrintable(MessageDocumentUnavailable);
        }
        catch (Exception ex)
        {
            return PrintPrepareResult.Failed(ex.Message);
        }
        finally
        {
            store?.Dispose(); // 取消/异常：删除半成品临时目录
            renderer.Dispose(); // 作业内关闭专用句柄（成功路径亦然，B2）
        }
    }

    /// <summary>
    /// 阶段①后台预渲染：逐页（显式打印旗标，非 LRU）渲染 → 后台 PNG 编码落盘 → 上报进度。
    /// 每页开始前检查取消；内存窗口由 <see cref="PrintArtifactStore"/> 的解码缓存封顶 K 页。
    /// </summary>
    private static async Task PreRenderAsync(
        PdfiumRenderService renderer,
        string docId,
        IReadOnlyList<int> pageIndices,
        int dpi,
        PrintArtifactStore store,
        IProgress<PrintProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var position = 0; position < pageIndices.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageIndex = pageIndices[position];

            // 显式打印旗标（ANNOT | PRINTING）：非 ScreenRenderFlags → cacheable=false，不进屏幕 LRU。
            var bitmap = await renderer
                .RenderPageAsync(docId, pageIndex, dpi, 100, PdfiumRenderService.PrintRenderFlags)
                .ConfigureAwait(false);

            var sizePt = renderer.GetPageSize(docId, pageIndex) ?? new PageSize(595, 842);
            var sizeDip = new Size(sizePt.Width * PointToDip, sizePt.Height * PointToDip);

            // PNG 编码 + 落盘放后台线程：不让编码占用调用线程/UI 线程。
            await Task.Run(() => store.AddPage(bitmap, sizeDip), cancellationToken).ConfigureAwait(false);

            progress?.Report(new PrintProgress(position + 1, pageIndices.Count, pageIndex));
        }
    }
}
