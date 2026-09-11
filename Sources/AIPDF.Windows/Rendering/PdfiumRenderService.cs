// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V1 阅读器渲染服务地基（T1）。
//
// 纪律【R12】：PDFium 非线程安全（fpdfview.h: "None of the PDFium APIs are thread-safe"）。
// 任何 FPDF_* 调用必须持有 PdfiumLock.Global。T2 文本层服务复用同一把锁。
//
// V2 T4 增补（BLK-2/BLK-3）：句柄代际戳 + 在途原生调用计数 + Close-first 重载 drain。
//   - 就地保存后必须 close→open 换新句柄与空 LRU，否则同路径去重返回旧（无内嵌注释）句柄；
//   - 关闭旧句柄前必须等待该文档在途原生调用归零（RenderPageCore / 大纲作用域），
//     否则 FPDF_LoadPage/FPDF_RenderPageBitmap 读取已释放句柄 → use-after-free（进程级不可捕获）。
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;

namespace AIPDF.Windows.Rendering;

/// <summary>进程级 PDFium 全局单锁。所有 FPDF_* 调用序列必须在 lock 内执行。</summary>
public static class PdfiumLock
{
    /// <summary>共享锁对象：T1 渲染服务与 T2 文本层服务共用，保证进程内 PDFium 串行。</summary>
    public static readonly object Global = new();

    private static bool _initialized;

    /// <summary>FPDF_InitLibrary 幂等初始化（锁内）。</summary>
    public static void EnsureInitialized()
    {
        lock (Global)
        {
            if (_initialized) return;
            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }
}

/// <summary>文档打开失败的结构化异常。ErrorCode 为 PDFium FPDF_GetLastError 值。</summary>
public sealed class PdfiumOpenException : Exception
{
    public ulong ErrorCode { get; }
    public string Path { get; }

    public PdfiumOpenException(string path, ulong errorCode, string? message = null)
        : base(message ?? $"PDF 打开失败: {path} (FPDF_GetLastError={errorCode})")
    {
        Path = path;
        ErrorCode = errorCode;
    }
}

/// <summary>页尺寸（PDF 点，1pt = 1/72 inch）。</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>
/// 文档正在 Close-first 重载（BLK-3）：句柄即将关闭，新的渲染/解析请求被拒绝。
/// 调用方应丢弃该结果（宿主重载后会重新发起渲染）；不是用户可见错误。
/// </summary>
public sealed class DocumentReloadingException : InvalidOperationException
{
    public DocumentReloadingException(string path)
        : base($"文档正在重载，请求已作废: {path}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// FPDF_RenderPageBitmap 的旧式整数旗标（fpdfview.h 宏，PDFiumCore 以 int 暴露，未提供枚举）。
/// 仅列出本项目使用的两位；值取自 PDFium 公共头文件，勿凭记忆改写。
/// </summary>
public static class PdfiumRenderFlags
{
    /// <summary>FPDF_ANNOT = 0x0001：绘制页面注释（内嵌高亮/FreeText 等）。</summary>
    public const int Annot = 0x0001;

    /// <summary>FPDF_PRINTING = 0x0800：按打印语义绘制（Print 位为假的注释不上屏）。</summary>
    public const int Printing = 0x0800;

    /// <summary>空白基线（当前 V1 行为：不绘制任何注释）。仅测试对照用。</summary>
    public const int None = 0;
}

/// <summary>LRU 可观测计数（B9 预留）。</summary>
public sealed record LruStats(long Hits, long Misses, long Evictions, int EntryCount, long EstimatedBytes);

/// <summary>
/// PDFium 渲染服务：持久文档句柄 + 页尺寸全局缓存 + 全局单锁 + 字节预算 LRU + 后台线程渲染。
/// 密码仅用于打开时传入 PDFium，不存储（V1 降级点③）。
/// </summary>
public sealed class PdfiumRenderService : IDisposable
{
    /// <summary>默认位图字节预算：256MB（BGRA 估算）。</summary>
    public const long DefaultByteBudget = 256L * 1024 * 1024;

    /// <summary>
    /// 屏幕渲染旗标。BLK-5 决策：<c>FPDF_PRINTING</c> 是打印语义——Print 位为假的注释在打印模式下
    /// 不上屏，用于屏幕会让用户在打开文档时看不到此类内嵌注释（视觉回归）。故屏上仅 <c>FPDF_ANNOT</c>；
    /// <c>FPDF_PRINTING</c> 预留打印路径（<see cref="PrintRenderFlags"/>）。验证证据见
    /// .omo/evidence/v2-task1-printflag.txt（Print=false 高亮在 ANNOT 下可见、在 ANNOT|PRINTING 下不可见）。
    /// </summary>
    public const int ScreenRenderFlags = PdfiumRenderFlags.Annot;

    /// <summary>打印渲染旗标：PRD 4.1.3 的 <c>FPDF_ANNOT | FPDF_PRINTING</c>（打印语义，含 Print 位过滤）。</summary>
    public const int PrintRenderFlags = PdfiumRenderFlags.Annot | PdfiumRenderFlags.Printing;

    /// <summary>WPF/SlimDX 纹理单边上限；超过则 clamp 并记录（R9）。</summary>
    public const int MaxTextureEdge = 4096;

    /// <summary>
    /// PDFium <c>FPDF_ERR_PASSWORD</c>（fpdfview.h 宏值 4）：文档需要密码、或提供的密码不正确。
    /// T9 据此判定【ST-04】加密文档密码解锁入口；其他错误码（格式/文件/安全）不走密码流程。
    /// </summary>
    public const ulong PdfiumErrorPassword = 4;

    private readonly object _stateLock = new();
    private readonly Dictionary<string, DocumentEntry> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _byteBudget;
    private readonly int _maxTextureEdge;

    /// <summary>文档打开代际戳计数器（每次 OpenDocument 成功分配，Close-first 重载后必变）。</summary>
    private long _generationCounter;

    // LRU 可观测计数（Interlocked，无锁更新）
    private long _hits;
    private long _misses;
    private long _evictions;

    private bool _disposed;

    /// <summary>
    /// 测试钩子：RenderPageCore 登记在途计数后、进入原生渲染前调用（用于验证 Close-first 重载
    /// 会等待在途归零；生产恒为 null）。
    /// </summary>
    internal static Action? RenderCoreStartedForTest;

    public PdfiumRenderService(long byteBudget = DefaultByteBudget, int maxTextureEdge = MaxTextureEdge)
    {
        if (byteBudget <= 0) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        PdfiumLock.EnsureInitialized();
        _byteBudget = byteBudget;
        _maxTextureEdge = maxTextureEdge;
    }

    /// <summary>共享 PDFium 全局锁（T2 复用）。</summary>
    public object PdfiumSync => PdfiumLock.Global;

    // ------------------------------------------------------------------ 文档生命周期

    internal sealed class DocumentEntry : IDisposable
    {
        public required string Path;
        public FpdfDocumentT Handle = null!;
        public GCHandle Pin;                    // 文件字节固定，句柄生命周期内有效
        public byte[] Bytes = null!;
        public int PageCount;
        public PageSize[] PageSizes = null!;    // 打开时一次解析
        public int[] PageRotations = null!;     // /Rotate 度数（0/90/180/270），打开时一次解析（M-11 判定用）
        public readonly Dictionary<(int Page, int Dpi, int ZoomPct), BitmapSource> Cache = new();
        public LinkedList<(int Page, int Dpi, int ZoomPct)> Lru = new();
        public long CachedBytes;
        public bool Disposed;

        // ---- BLK-3：句柄生命周期与在途原生调用的排空 ----
        // Gate 保护 InFlight/Closing；原生调用开始前在 Gate 内计数，结束后递减并唤醒等待者。
        // Dispose 置 Closing 后阻塞等待 InFlight 归零，再在 PdfiumLock.Global 内关闭句柄。
        // InFlight 计数期间不持有 Gate，故与原生全局锁无嵌套，无死锁。
        public readonly object Gate = new();
        public int InFlight;
        public bool Closing;

        /// <summary>本次打开的代际戳（Close-first 重载后必变，供测试/诊断断言句柄已换代）。</summary>
        public long Generation;

        public void Dispose()
        {
            if (Disposed) return;
            lock (Gate)
            {
                Disposed = true;
                Closing = true;
                while (InFlight > 0) Monitor.Wait(Gate);
            }

            if (Handle is not null)
            {
                lock (PdfiumLock.Global) fpdfview.FPDF_CloseDocument(Handle);
                Handle = null!;
            }
            if (Pin.IsAllocated) Pin.Free();
            Cache.Clear();
            Lru.Clear();
            CachedBytes = 0;
        }
    }

    /// <summary>
    /// 打开文档（持久句柄）。同一路径重复打开返回同一句柄（去重【D14】）。
    /// 密码一次性传给 PDFium，不存储。失败抛 <see cref="PdfiumOpenException"/>。
    /// </summary>
    /// <returns>文档标识（规范化路径）。</returns>
    public string OpenDocument(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_documents.TryGetValue(fullPath, out var existing)) return existing.Path;
        }

        if (!File.Exists(fullPath))
            throw new PdfiumOpenException(fullPath, 0, $"文件不存在: {fullPath}");

        // 读入字节并固定：PDFium 句柄引用底层内存，句柄存活期间字节不可移动/释放
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex)
        {
            throw new PdfiumOpenException(fullPath, 0, $"文件读取失败: {fullPath} ({ex.Message})");
        }

        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        FpdfDocumentT handle;
        lock (PdfiumLock.Global)
        {
            handle = fpdfview.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (ulong)bytes.Length, password);
        }

        if (handle is null || handle.__Instance == IntPtr.Zero)
        {
            pin.Free();
            ulong err;
            lock (PdfiumLock.Global) err = fpdfview.FPDF_GetLastError();
            throw new PdfiumOpenException(fullPath, err);
        }

        // 打开时一次性解析全部页尺寸与旋转（后续零解析）
        int pageCount;
        PageSize[] sizes;
        int[] rotations;
        lock (PdfiumLock.Global)
        {
            pageCount = fpdfview.FPDF_GetPageCount(handle);
            sizes = new PageSize[pageCount];
            rotations = new int[pageCount];
            for (var i = 0; i < pageCount; i++)
            {
                var page = fpdfview.FPDF_LoadPage(handle, i);
                if (page is null || page.__Instance == IntPtr.Zero)
                {
                    fpdfview.FPDF_CloseDocument(handle);
                    pin.Free();
                    throw new PdfiumOpenException(fullPath, fpdfview.FPDF_GetLastError(), $"第 {i} 页加载失败: {fullPath}");
                }
                try
                {
                    sizes[i] = new PageSize(fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page));
                    // FPDFPage_GetRotation 返回 0..3（顺时针 0/90/180/270），-1 = 错误
                    var rot = fpdf_edit.FPDFPageGetRotation(page);
                    rotations[i] = rot is >= 0 and <= 3 ? rot * 90 : 0;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }

        var entry = new DocumentEntry
        {
            Path = fullPath,
            Handle = handle,
            Pin = pin,
            Bytes = bytes,
            PageCount = pageCount,
            PageSizes = sizes,
            PageRotations = rotations,
            Generation = Interlocked.Increment(ref _generationCounter),
        };

        lock (_stateLock)
        {
            // 双检：并发打开同一路径时保留先到者
            if (_documents.TryGetValue(fullPath, out var winner))
            {
                entry.Dispose();
                return winner.Path;
            }
            _documents[fullPath] = entry;
            return fullPath;
        }
    }

    /// <summary>
    /// 关闭文档并释放句柄与缓存。未打开的路径静默返回。
    /// 【BLK-3】阻塞等待该文档在途原生调用归零后再释放句柄；调用方应在 UI 线程或后台均可。
    /// </summary>
    public void CloseDocument(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        DocumentEntry? entry;
        lock (_stateLock)
        {
            if (_documents.Remove(docId, out entry)) { }
        }
        entry?.Dispose();
    }

    /// <summary>文档页数；未打开返回 -1。</summary>
    public int GetPageCount(string docId)
    {
        lock (_stateLock)
            return _documents.TryGetValue(docId, out var e) ? e.PageCount : -1;
    }

    /// <summary>
    /// 内部访问器：取得持久文档句柄（T4 大纲解析等衍生服务复用同一持久句柄，不重复打开文件）。
    /// 调用方所有 FPDF_* 调用必须自持 <see cref="PdfiumLock.Global"/>（句柄生命周期由本服务管理）。
    /// 【注意】此访问器不登记在途计数；需要防 Close-first 重载竞态的调用方改用
    /// <see cref="TryOpenDocumentScope"/>（返回 RAII 作用域）。
    /// </summary>
    internal FpdfDocumentT? TryGetDocumentHandle(string docId)
    {
        lock (_stateLock)
            return _documents.TryGetValue(docId, out var e) && !e.Disposed ? e.Handle : null;
    }

    /// <summary>页尺寸（点）。未打开/越界返回 null。</summary>
    public PageSize? GetPageSize(string docId, int pageIndex)
    {
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out var e)) return null;
            if (pageIndex < 0 || pageIndex >= e.PageCount) return null;
            return e.PageSizes[pageIndex];
        }
    }

    /// <summary>
    /// 页旋转度数（0/90/180/270）。未打开/越界返回 0。
    /// 【M-11 决策】非 0 即旋转页：V2 禁止在其上选区/批注（PdfCoordinateMapper 无 derotation，见 ReaderViewModel）。
    /// </summary>
    public int GetPageRotation(string docId, int pageIndex)
    {
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out var e)) return 0;
            if (pageIndex < 0 || pageIndex >= e.PageCount) return 0;
            return e.PageRotations[pageIndex];
        }
    }

    /// <summary>LRU 可观测统计。</summary>
    public LruStats GetLruStats(string docId)
    {
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out var e))
                return new LruStats(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses),
                    Interlocked.Read(ref _evictions), 0, 0);
            return new LruStats(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses),
                Interlocked.Read(ref _evictions), e.Cache.Count, e.CachedBytes);
        }
    }

    /// <summary>
    /// 当前句柄代际戳（每次 OpenDocument 成功 +1）。Close-first 重载后该值必变，
    /// 供测试断言「句柄已换代、未复用旧句柄」（BLK-2/M-2）。未打开返回 -1。
    /// </summary>
    public long GetDocumentGeneration(string docId)
    {
        lock (_stateLock)
            return _documents.TryGetValue(docId, out var e) ? e.Generation : -1;
    }

    /// <summary>
    /// 清空某文档的位图 LRU（M-2）。就地保存后须换代缓存，否则命中旧（无内嵌注释）位图，
    /// 表现为「叠加层清了但注释不出现」。Close-first 重载走 Close+Open，缓存自然清空；
    /// 本方法供不换句柄的显式失效路径与测试断言使用。
    /// </summary>
    public void ClearDocumentCache(string docId)
    {
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out var e)) return;
            e.Cache.Clear();
            e.Lru.Clear();
            e.CachedBytes = 0;
        }
    }

    /// <summary>该文档在途原生调用计数（BLK-3 drain 可观测）。未打开返回 0。</summary>
    public int GetInFlightCount(string docId)
    {
        DocumentEntry? entry;
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out entry)) return 0;
        }
        lock (entry.Gate) return entry.InFlight;
    }

    // ------------------------------------------------------------------ 渲染

    /// <summary>
    /// 后台线程渲染一页并缓存（LRU 字节预算）。返回冻结的 BitmapSource（可跨线程绑定）。
    /// </summary>
    /// <param name="docId">OpenDocument 返回的文档标识。</param>
    /// <param name="pageIndex">页索引（0 起）。</param>
    /// <param name="dpi">渲染 DPI（如 96/150）。</param>
    /// <param name="zoomPct">缩放百分比（100 = 原大），与 dpi 相乘决定像素尺寸。</param>
    public Task<BitmapSource> RenderPageAsync(string docId, int pageIndex, double dpi = 96, int zoomPct = 100)
        => RenderPageAsync(docId, pageIndex, dpi, zoomPct, ScreenRenderFlags);

    /// <summary>
    /// 内部渲染入口：显式旗标（测试对照与未来打印路径）。仅 <see cref="ScreenRenderFlags"/> 走 LRU 缓存，
    /// 其余旗标不缓存（避免打印语义/基线对照结果污染屏幕位图缓存）。
    /// </summary>
    internal Task<BitmapSource> RenderPageAsync(string docId, int pageIndex, double dpi, int zoomPct, int renderFlags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        DocumentEntry entry = GetEntryOrThrow(docId);
        if (pageIndex < 0 || pageIndex >= entry.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"页索引越界: {pageIndex}/{entry.PageCount}");
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        if (zoomPct <= 0) throw new ArgumentOutOfRangeException(nameof(zoomPct));

        return Task.Run(() => RenderPageCore(entry, pageIndex, dpi, zoomPct, renderFlags));
    }

    private DocumentEntry GetEntryOrThrow(string docId)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_documents.TryGetValue(docId, out var e))
                throw new KeyNotFoundException($"文档未打开: {docId}");
            return e;
        }
    }

    private BitmapSource RenderPageCore(DocumentEntry entry, int pageIndex, double dpi, int zoomPct, int renderFlags)
    {
        // BLK-3：进入时登记在途计数；Close-first 重载等待归零后才关闭旧句柄。
        EnterEntry(entry);
        try
        {
            RenderCoreStartedForTest?.Invoke();
            var key = (pageIndex, (int)Math.Round(dpi), zoomPct);
            var cacheable = renderFlags == ScreenRenderFlags;

            // 缓存查询（状态锁，非 PDFium 锁）
            if (cacheable)
            {
                lock (_stateLock)
                {
                    if (entry.Cache.TryGetValue(key, out var cached))
                    {
                        Interlocked.Increment(ref _hits);
                        TouchLru(entry, key);
                        return cached;
                    }
                    Interlocked.Increment(ref _misses);
                }
            }

            var bitmap = RenderBitmap(entry, pageIndex, dpi, zoomPct, renderFlags, out var clamped);

            if (cacheable)
            {
                var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
                lock (_stateLock)
                {
                    if (entry.Cache.TryAdd(key, bitmap))
                    {
                        entry.CachedBytes += bytes;
                        entry.Lru.AddLast(key);
                        EvictOverBudget(entry);
                    }
                }

                if (clamped)
                {
                    // 限幅事件可观测（测试断言用）
                    lock (_stateLock) _clampedCount++;
                }
            }

            return bitmap;
        }
        finally
        {
            ExitEntry(entry);
        }
    }

    /// <summary>登记一次在途原生调用；关闭中的文档拒绝新请求（DocumentReloadingException）。</summary>
    private static void EnterEntry(DocumentEntry entry)
    {
        lock (entry.Gate)
        {
            if (entry.Closing || entry.Disposed)
                throw new DocumentReloadingException(entry.Path);
            entry.InFlight++;
        }
    }

    /// <summary>结束一次在途原生调用；归零时唤醒 CloseDocument 的等待者。</summary>
    private static void ExitEntry(DocumentEntry entry)
    {
        lock (entry.Gate)
        {
            entry.InFlight--;
            if (entry.InFlight == 0) Monitor.PulseAll(entry.Gate);
        }
    }

    /// <summary>
    /// 借出持久句柄的 RAII 作用域（衍生只读服务如大纲解析用）：作用域存活期计入在途，
    /// Close-first 重载会等待作用域释放后再关闭句柄（BLK-3，防 use-after-free）。
    /// 调用方须 Dispose。
    /// </summary>
    internal DocumentScope? TryOpenDocumentScope(string docId)
    {
        DocumentEntry? entry;
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out entry)) return null;
        }

        lock (entry.Gate)
        {
            if (entry.Closing || entry.Disposed) return null;
            entry.InFlight++;
        }
        return new DocumentScope(this, entry, entry.Handle);
    }

    /// <summary>句柄借用作用域（内部计数；见 <see cref="TryOpenDocumentScope"/>）。</summary>
    internal sealed class DocumentScope : IDisposable
    {
        private readonly DocumentEntry _entry;
        private bool _disposed;

        internal DocumentScope(PdfiumRenderService owner, DocumentEntry entry, FpdfDocumentT handle)
        {
            _entry = entry;
            Handle = handle;
        }

        /// <summary>借出的持久文档句柄（仅在作用域存活期有效）。</summary>
        public FpdfDocumentT Handle { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ExitEntry(_entry);
        }
    }

    /// <summary>
    /// 实际 PDFium 渲染：全局锁内写预固定缓冲，锁外构造冻结位图。
    /// 【R12】所有 FPDF_* 调用（含 <c>FPDF_RenderPageBitmap</c> 的旗标路径）都在 <see cref="PdfiumLock.Global"/> 内。
    /// </summary>
    private BitmapSource RenderBitmap(DocumentEntry entry, int pageIndex, double dpi, int zoomPct, int renderFlags, out bool clamped)
    {
        // 目标像素尺寸：宽×高×4 字节（BGRA），按实际 dpi×zoom 计
        var size = entry.PageSizes[pageIndex];
        var scale = dpi * zoomPct / 100.0 / 72.0;
        var pxW = (int)Math.Round(size.Width * scale);
        var pxH = (int)Math.Round(size.Height * scale);
        if (pxW <= 0) pxW = 1;
        if (pxH <= 0) pxH = 1;

        // 超纹理限幅【R9】：clamp 到上限并记录（V1 取限幅，不分块）
        clamped = false;
        if (pxW > _maxTextureEdge || pxH > _maxTextureEdge)
        {
            var ratio = Math.Min((double)_maxTextureEdge / pxW, (double)_maxTextureEdge / pxH);
            pxW = Math.Max(1, (int)Math.Round(pxW * ratio));
            pxH = Math.Max(1, (int)Math.Round(pxH * ratio));
            clamped = true;
            System.Diagnostics.Trace.WriteLine(
                $"[PdfiumRenderService] 纹理限幅: doc={entry.Path} page={pageIndex} -> {pxW}x{pxH}");
        }

        // PDFium 渲染（全局锁内）——渲染到临时缓冲，锁外拷贝为冻结位图
        var stride = pxW * 4;
        var buffer = new byte[(long)stride * pxH];
        var bufferPin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            lock (PdfiumLock.Global)
            {
                var page = fpdfview.FPDF_LoadPage(entry.Handle, pageIndex);
                if (page is null || page.__Instance == IntPtr.Zero)
                    throw new PdfiumOpenException(entry.Path, fpdfview.FPDF_GetLastError(), $"第 {pageIndex} 页加载失败: {entry.Path}");
                try
                {
                    var bmp = fpdfview.FPDFBitmapCreateEx(pxW, pxH, 4, bufferPin.AddrOfPinnedObject(), stride);
                    if (bmp is null || bmp.__Instance == IntPtr.Zero)
                        throw new InvalidOperationException($"FPDFBitmap_CreateEx 失败 ({pxW}x{pxH})");
                    try
                    {
                        // 白底填充（BGRA: B=255,G=255,R=255,A=255 → 0xFFFFFFFF）
                        fpdfview.FPDFBitmapFillRect(bmp, 0, 0, pxW, pxH, 0xFFFFFFFFUL);
                        fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, pxW, pxH, 0, renderFlags);
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(bmp);
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }
        finally
        {
            bufferPin.Free();
        }

        // 锁外构造冻结位图（UI 线程只做绑定，不做渲染）
        var bitmap = BitmapSource.Create(pxW, pxH, dpi * zoomPct / 100.0, dpi * zoomPct / 100.0,
            PixelFormats.Bgra32, null, buffer, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private int _clampedCount;

    /// <summary>限幅触发次数（可观测）。</summary>
    public int ClampedCount => Interlocked.CompareExchange(ref _clampedCount, 0, 0);

    private static void TouchLru(DocumentEntry entry, (int Page, int Dpi, int ZoomPct) key)
    {
        // 调用方持有 _stateLock
        if (entry.Lru.Remove(key)) entry.Lru.AddLast(key);
    }

    private void EvictOverBudget(DocumentEntry entry)
    {
        // 调用方持有 _stateLock
        while (entry.CachedBytes > _byteBudget && entry.Lru.Count > 1)
        {
            var oldest = entry.Lru.First!;
            entry.Lru.RemoveFirst();
            if (entry.Cache.Remove(oldest.Value, out var removed))
            {
                entry.CachedBytes -= (long)removed.PixelWidth * removed.PixelHeight * 4;
                Interlocked.Increment(ref _evictions);
            }
        }
    }

    // ------------------------------------------------------------------ 释放

    public void Dispose()
    {
        List<DocumentEntry> entries;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            entries = new List<DocumentEntry>(_documents.Values);
            _documents.Clear();
        }
        foreach (var e in entries) e.Dispose();
    }
}
