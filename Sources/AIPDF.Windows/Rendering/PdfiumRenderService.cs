// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V1 阅读器渲染服务地基（T1）。
//
// 纪律【R12】：PDFium 非线程安全（fpdfview.h: "None of the PDFium APIs are thread-safe"）。
// 任何 FPDF_* 调用必须持有 PdfiumLock.Global。T2 文本层服务复用同一把锁。
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

    /// <summary>WPF/SlimDX 纹理单边上限；超过则 clamp 并记录（R9）。</summary>
    public const int MaxTextureEdge = 4096;

    private readonly object _stateLock = new();
    private readonly Dictionary<string, DocumentEntry> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _byteBudget;
    private readonly int _maxTextureEdge;

    // LRU 可观测计数（Interlocked，无锁更新）
    private long _hits;
    private long _misses;
    private long _evictions;

    private bool _disposed;

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

    private sealed class DocumentEntry : IDisposable
    {
        public required string Path;
        public FpdfDocumentT Handle = null!;
        public GCHandle Pin;                    // 文件字节固定，句柄生命周期内有效
        public byte[] Bytes = null!;
        public int PageCount;
        public PageSize[] PageSizes = null!;    // 打开时一次解析
        public readonly Dictionary<(int Page, int Dpi, int ZoomPct), BitmapSource> Cache = new();
        public LinkedList<(int Page, int Dpi, int ZoomPct)> Lru = new();
        public long CachedBytes;
        public bool Disposed;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (Handle is not null)
            {
                lock (PdfiumLock.Global) fpdfview.FPDF_CloseDocument(Handle);
                Handle = null!;
            }
            if (Pin.IsAllocated) Pin.Free();
            Cache.Clear();
            Lru.Clear();
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

        // 打开时一次性解析全部页尺寸（后续零解析）
        int pageCount;
        PageSize[] sizes;
        lock (PdfiumLock.Global)
        {
            pageCount = fpdfview.FPDF_GetPageCount(handle);
            sizes = new PageSize[pageCount];
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

    /// <summary>关闭文档并释放句柄与缓存。未打开的路径静默返回。</summary>
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

    // ------------------------------------------------------------------ 渲染

    /// <summary>
    /// 后台线程渲染一页并缓存（LRU 字节预算）。返回冻结的 BitmapSource（可跨线程绑定）。
    /// </summary>
    /// <param name="docId">OpenDocument 返回的文档标识。</param>
    /// <param name="pageIndex">页索引（0 起）。</param>
    /// <param name="dpi">渲染 DPI（如 96/150）。</param>
    /// <param name="zoomPct">缩放百分比（100 = 原大），与 dpi 相乘决定像素尺寸。</param>
    public Task<BitmapSource> RenderPageAsync(string docId, int pageIndex, double dpi = 96, int zoomPct = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        DocumentEntry entry = GetEntryOrThrow(docId);
        if (pageIndex < 0 || pageIndex >= entry.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"页索引越界: {pageIndex}/{entry.PageCount}");
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        if (zoomPct <= 0) throw new ArgumentOutOfRangeException(nameof(zoomPct));

        return Task.Run(() => RenderPageCore(entry, pageIndex, dpi, zoomPct));
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

    private BitmapSource RenderPageCore(DocumentEntry entry, int pageIndex, double dpi, int zoomPct)
    {
        var key = (pageIndex, (int)Math.Round(dpi), zoomPct);

        // 缓存查询（状态锁，非 PDFium 锁）
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

        // 目标像素尺寸：宽×高×4 字节（BGRA），按实际 dpi×zoom 计
        var size = entry.PageSizes[pageIndex];
        var scale = dpi * zoomPct / 100.0 / 72.0;
        var pxW = (int)Math.Round(size.Width * scale);
        var pxH = (int)Math.Round(size.Height * scale);
        if (pxW <= 0) pxW = 1;
        if (pxH <= 0) pxH = 1;

        // 超纹理限幅【R9】：clamp 到上限并记录（V1 取限幅，不分块）
        var clamped = false;
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
                        fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, pxW, pxH, 0, 0);
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

        var bytes = (long)pxW * pxH * 4;
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
