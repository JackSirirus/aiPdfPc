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

        /// <summary>
        /// 打开时一次读取的权限位（<c>FPDF_GetDocPermissions</c>）。非加密文档为 0xFFFFFFFC（全位）；
        /// 权限加密文档缺任一位即受限。仅供 T15 整篇翻译「受限文档确认」判定，不改变渲染语义。
        /// </summary>
        public uint Permissions;
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

        // ---- 预览表单填充环境（Q5/B2，T2）----
        // 仅“预览专用实例”经 EnableFormFill 启用；阅读器 ScreenRenderFlags 路径完全不触碰，
        // 保证既有渲染逐字节不变。环境引用本句柄，退出必须先于 FPDF_CloseDocument。
        public FPDF_FORMFILLINFO? FormInfo;
        public FpdfFormHandleT? FormHandle;
        internal FormFillDelegateHolder? FormDelegates;

        public void Dispose()
        {
            if (Disposed) return;
            lock (Gate)
            {
                Disposed = true;
                Closing = true;
                while (InFlight > 0) Monitor.Wait(Gate);
            }

            // 退出表单环境（引用文档句柄），再释放原生 FPDF_FORMFILLINFO 与保活委托。
            if (FormHandle is not null)
            {
                lock (PdfiumLock.Global) fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(FormHandle);
                FormHandle = null;
            }
            FormInfo?.Dispose();
            FormInfo = null;
            FormDelegates = null;

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

        if (TryGetRegistered(fullPath, out var existing)) return existing;

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

        return RegisterDocument(fullPath, bytes, password);
    }

    /// <summary>
    /// 【T2 预览内部入口】共享读打开（FileShare.ReadWrite，语义沿用已删除的旧 PdfPreviewDocument.OpenFile；
    /// 旧类型随 T7 单栈收敛删除，现为 PdfiumPreviewDocument）：
    /// 允许预览刚被引擎写入/被其他进程以 ReadWrite 持有的文件，避免 <see cref="OpenDocument"/>
    /// 的 File.ReadAllBytes（FileShare.Read）回归失败。仅服务预览/打印；阅读器既有路径不变。
    /// </summary>
    internal string OpenDocumentSharedRead(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);

        if (TryGetRegistered(fullPath, out var existing)) return existing;

        byte[] bytes;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (Exception ex)
        {
            throw new PdfiumOpenException(fullPath, 0, $"文件读取失败: {fullPath} ({ex.Message})");
        }

        return RegisterDocument(fullPath, bytes, password);
    }

    /// <summary>
    /// 【T2 预览内部入口】从内存字节打开（复用 <c>FPDF_LoadMemDocument64</c> 路径）。
    /// 字节在句柄生命周期内固定（GCHandle.Pinned）；docId 为唯一 <c>mem://</c> 标识。
    /// </summary>
    internal string OpenDocumentFromBytes(byte[] pdfBytes, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
            throw new PdfiumOpenException("mem://empty", 0, "PDF 字节为空");

        return RegisterDocument("mem://" + Guid.NewGuid().ToString("N"), pdfBytes, password);
    }

    /// <summary>已注册键的快速判定（锁内；含 _disposed 守卫）；未注册返回 false 且 docId=key。</summary>
    private bool TryGetRegistered(string key, out string docId)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_documents.TryGetValue(key, out var existing))
            {
                docId = existing.Path;
                return true;
            }
        }

        docId = key;
        return false;
    }

    /// <summary>
    /// 加载字节 → 持久句柄 → 一次性解析全部页尺寸/旋转 → 注册（双检防并发重复）。
    /// path / 共享读 / 内存三种入口复用；失败路径确保 pin.Free()，无句柄泄漏。
    /// </summary>
    private string RegisterDocument(string key, byte[] bytes, string? password)
    {
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
            throw new PdfiumOpenException(key, err);
        }

        // 打开时一次性解析全部页尺寸与旋转（后续零解析）
        int pageCount;
        uint permissions;
        PageSize[] sizes;
        int[] rotations;
        lock (PdfiumLock.Global)
        {
            pageCount = fpdfview.FPDF_GetPageCount(handle);
            // 权限位（T15）：非加密文档返回 0xFFFFFFFC；权限加密文档缺位 → 受限。一次读取，不随页变化。
            permissions = unchecked((uint)fpdfview.FPDF_GetDocPermissions(handle));
            sizes = new PageSize[pageCount];
            rotations = new int[pageCount];
            for (var i = 0; i < pageCount; i++)
            {
                var page = fpdfview.FPDF_LoadPage(handle, i);
                if (page is null || page.__Instance == IntPtr.Zero)
                {
                    fpdfview.FPDF_CloseDocument(handle);
                    pin.Free();
                    throw new PdfiumOpenException(key, fpdfview.FPDF_GetLastError(), $"第 {i} 页加载失败: {key}");
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
            Path = key,
            Handle = handle,
            Pin = pin,
            Bytes = bytes,
            PageCount = pageCount,
            Permissions = permissions,
            PageSizes = sizes,
            PageRotations = rotations,
            Generation = Interlocked.Increment(ref _generationCounter),
        };

        lock (_stateLock)
        {
            // 双检：并发打开同一路径时保留先到者
            if (_documents.TryGetValue(key, out var winner))
            {
                entry.Dispose();
                return winner.Path;
            }
            _documents[key] = entry;
            return key;
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
    /// 权限位掩码（<c>FPDF_GetDocPermissions</c>）。与 backend <c>translate/output.py</c> 的
    /// <c>_PERMISSION_BITS</c> 对齐：print|modify|copy|annotate|form|accessibility|assemble|print_hq。
    /// 非加密文档返回 0xFFFFFFFC（含全部位）；缺任一位即权限受限（可打开但禁止抽取/复制等）。
    /// </summary>
    public const uint PermissionRelevantBits = 0xF3C;

    /// <summary>文档权限位；未打开返回 <see cref="uint.MaxValue"/>（视作不受限）。</summary>
    public uint GetDocumentPermissions(string docId)
    {
        lock (_stateLock)
            return _documents.TryGetValue(docId, out var e) && !e.Disposed ? e.Permissions : uint.MaxValue;
    }

    /// <summary>
    /// 文档是否为「权限加密（受限）」。未打开返回 false；判定 = 权限位缺任一相关位。
    /// 与引擎回填报告 <c>source_protection.restricted</c> 同源，保证翻译前确认与译后披露一致。
    /// </summary>
    public bool IsPermissionRestricted(string docId)
    {
        lock (_stateLock)
        {
            if (!_documents.TryGetValue(docId, out var e) || e.Disposed) return false;
            return IsPermissionMaskRestricted(e.Permissions);
        }
    }

    /// <summary>权限位是否受限（纯函数，单测直接覆盖）。</summary>
    public static bool IsPermissionMaskRestricted(uint permissions)
        => (permissions & PermissionRelevantBits) != PermissionRelevantBits;

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

    /// <summary>FPDF_ANNOT_WIDGET：交互表单字段（不计入「批注」，FR-22 警告只针对批注）。</summary>
    private const int AnnotSubtypeWidget = 20;

    /// <summary>
    /// 文档内已内嵌注释总数（含 Highlight / FreeText 等，不含 Widget 表单字段）。
    /// 用于 V2 T10 FR-22：redact 警告判定「文档存在批注（无论是否已保存）」（AC-22.1/22.2）。
    /// 仅经 <see cref="TryOpenDocumentScope"/> 计入在途，不修改文档，不触发保存。
    /// 未打开返回 0。所有 FPDF_* 调用持 <see cref="PdfiumLock.Global"/>（R12）。
    /// </summary>
    public int CountAnnotations(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        var scope = TryOpenDocumentScope(docId);
        if (scope is null) return 0;

        try
        {
            var total = 0;
            lock (PdfiumLock.Global)
            {
                var pageCount = fpdfview.FPDF_GetPageCount(scope.Handle);
                for (var i = 0; i < pageCount; i++)
                {
                    var page = fpdfview.FPDF_LoadPage(scope.Handle, i);
                    if (page is null || page.__Instance == IntPtr.Zero) continue;
                    try
                    {
                        var count = fpdf_annot.FPDFPageGetAnnotCount(page);
                        for (var j = 0; j < count; j++)
                        {
                            var annot = fpdf_annot.FPDFPageGetAnnot(page, j);
                            if (annot is null || annot.__Instance == IntPtr.Zero) continue;
                            if (fpdf_annot.FPDFAnnotGetSubtype(annot) == AnnotSubtypeWidget) continue;
                            total++;
                        }
                    }
                    finally
                    {
                        fpdfview.FPDF_ClosePage(page);
                    }
                }
            }

            return total;
        }
        finally
        {
            scope.Dispose();
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

    // ------------------------------------------------------------------ 预览表单填充（T2 / Q5）

    /// <summary>
    /// 【T2 预览内部入口】在指定文档条目上创建表单填充环境（FPDFDOC_InitFormFillEnvironment）。
    /// 仅预览专用实例调用；阅读器路径不调用。<see cref="DocumentEntry.FormDelegates"/> 为实例字段保活
    /// （CppSharp 只写函数指针，不持托管引用，委托被 GC 后原生回调 = 崩溃）。
    /// 幂等；所有 FPDF_* 在 <see cref="PdfiumLock.Global"/> 内，并经在途计数防 Close-first 竞态。
    /// 失败抛 <see cref="InvalidOperationException"/>（Q5：不得静默跳过 Widget）。
    /// </summary>
    internal void EnableFormFill(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        DocumentEntry entry = GetEntryOrThrow(docId);

        lock (entry.Gate)
        {
            if (entry.Closing || entry.Disposed) throw new DocumentReloadingException(entry.Path);
            if (entry.FormHandle is not null) return;
            entry.InFlight++;
        }

        try
        {
            lock (PdfiumLock.Global)
            {
                var delegates = new FormFillDelegateHolder();
                var info = new FPDF_FORMFILLINFO { Version = 2 };
                delegates.Attach(info);

                var handle = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(entry.Handle, info);
                if (handle is null || handle.__Instance == IntPtr.Zero)
                {
                    info.Dispose();
                    throw new InvalidOperationException($"表单填充环境初始化失败: {entry.Path}");
                }

                entry.FormDelegates = delegates;
                entry.FormInfo = info;
                entry.FormHandle = handle;
            }
        }
        finally
        {
            ExitEntry(entry);
        }
    }

    /// <summary>
    /// 【T2 预览内部入口】按显式像素尺寸渲染一页并绘制表单 Widget。
    /// 与阅读器 <see cref="RenderPageAsync(string,int,double,int)"/> 的区别：
    ///   1) 像素尺寸由调用方给定（保持 <c>PdfCoordinateMapper.PixelSize</c> 契约，避免舍入漂移）；
    ///   2) 若该文档条目已 <see cref="EnableFormFill"/>，在同一 bitmap/page 上追加 <c>FPDF_FFLDraw</c>
    ///      （Q5：旧栈 WithFormFill=true 的等价物）；未启用则退化为纯 <see cref="ScreenRenderFlags"/> 渲染。
    /// 不进 LRU（表单位图与屏幕位图键无法区分，避免污染；预览按需渲染）。
    /// 返回冻结 <see cref="BitmapSource"/>，可跨线程绑定（F3-BLK-1）。
    /// </summary>
    internal Task<BitmapSource> RenderPagePreviewAsync(string docId, int pageIndex, int pixelWidth, int pixelHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        if (pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelHeight));

        DocumentEntry entry = GetEntryOrThrow(docId);
        if (pageIndex < 0 || pageIndex >= entry.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"页索引越界: {pageIndex}/{entry.PageCount}");

        return Task.Run(() => RenderPagePreviewCore(entry, pageIndex, pixelWidth, pixelHeight));
    }

    private BitmapSource RenderPagePreviewCore(DocumentEntry entry, int pageIndex, int pxW, int pxH)
    {
        EnterEntry(entry);
        try
        {
            RenderCoreStartedForTest?.Invoke();
            // 超纹理限幅【R9】：与 RenderBitmap 同规则
            if (pxW > _maxTextureEdge || pxH > _maxTextureEdge)
            {
                var ratio = Math.Min((double)_maxTextureEdge / pxW, (double)_maxTextureEdge / pxH);
                pxW = Math.Max(1, (int)Math.Round(pxW * ratio));
                pxH = Math.Max(1, (int)Math.Round(pxH * ratio));
                lock (_stateLock) _clampedCount++;
            }

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
                            fpdfview.FPDFBitmapFillRect(bmp, 0, 0, pxW, pxH, 0xFFFFFFFFUL);
                            var form = entry.FormHandle;
                            if (form is not null) fpdf_formfill.FORM_OnAfterLoadPage(page, form);
                            fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, pxW, pxH, 0, ScreenRenderFlags);
                            if (form is not null)
                            {
                                // Q5：同一 bitmap/page 上绘制 Widget（表单填充环境）
                                fpdf_formfill.FPDF_FFLDraw(form, bmp, page, 0, 0, pxW, pxH, 0, ScreenRenderFlags);
                                fpdf_formfill.FORM_OnBeforeClosePage(page, form);
                            }
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

            var bitmap = BitmapSource.Create(pxW, pxH, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            ExitEntry(entry);
        }
    }

    /// <summary>
    /// 表单填充回调保活持有者：委托作为实例字段持有，防 GC 回收后原生回调崩溃。
    /// 仅持有无需 unsafe 指针的回调（指针参数回调留空；PDFium 对版本 2 的扩展回调按需调用）。
    /// </summary>
    internal sealed class FormFillDelegateHolder
    {
        private readonly PDFiumCore.Delegates.Action___IntPtr _release = static _ => { };
        private readonly PDFiumCore.Delegates.Action___IntPtr___IntPtr_double_double_double_double _invalidate =
            static (_, _, _, _, _, _) => { };
        private readonly PDFiumCore.Delegates.Action___IntPtr_int _setCursor = static (_, _) => { };
        private readonly PDFiumCore.Delegates.Action___IntPtr_int _killTimer = static (_, _) => { };
        private readonly PDFiumCore.Delegates.Action___IntPtr _onChange = static _ => { };
        private readonly PDFiumCore.Delegates.Func_int___IntPtr___IntPtr _getRotation = static (_, _) => 0;
        private readonly PDFiumCore.Delegates.Func___IntPtr___IntPtr___IntPtr_int _getPage = static (_, _, _) => IntPtr.Zero;
        private readonly PDFiumCore.Delegates.Func___IntPtr___IntPtr___IntPtr _getCurrentPage = static (_, _) => IntPtr.Zero;
        private readonly PDFiumCore.Delegates.Action___IntPtr___IntPtr_int _setCurrentPage = static (_, _, _) => { };

        internal void Attach(FPDF_FORMFILLINFO info)
        {
            info.Version = 2;
            info.Release = _release;
            info.FFI_Invalidate = _invalidate;
            info.FFI_SetCursor = _setCursor;
            info.FFI_KillTimer = _killTimer;
            info.FFI_OnChange = _onChange;
            info.FFI_GetRotation = _getRotation;
            info.FFI_GetPage = _getPage;
            info.FFI_GetCurrentPage = _getCurrentPage;
            info.FFI_SetCurrentPage = _setCurrentPage;
        }
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
