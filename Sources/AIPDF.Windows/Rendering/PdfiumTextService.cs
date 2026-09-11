// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V1 阅读器文本层服务（T2）：FPDFText_* 封装——页文本提取、命中矩形、原生命中测试。
//
// 锁纪律【R12】：PDFium 非线程安全，本类全部 FPDF_* 调用（含 CppSharp 封装与原生
// DllImport）都在 PdfiumLock.Global（T1 落地的进程级共享锁）内执行。
//
// 封送纪律（对照 tmp/pdfium-spike 实测结论，勿凭记忆改写）：
//  - 封送缺陷②：PDFiumCore 154 的 CppSharp 公共封装把 UInt16& 参数封送为空临时缓冲
//    （任何搜索词都"命中"下标 0），因此所有携带字符串/缓冲的 FPDFText_* 函数一律走
//    手写 DllImport（绑定同一个已加载的 pdfium.dll，标准 C 导出名）。
//    受影响：FPDFText_GetText / FindStart / FindNext / GetSchResultIndex / GetSchCount。
//  - FindNext 返回值语义（spike 诊断探针实测）：非 0 = 找到匹配、0 = 耗尽；
//    匹配起始下标取 FPDFText_GetSchResultIndex，且必须校验递增防止死循环。
//  - FPDFText_GetText 输出缓冲按 CountChars + 2 分配（两种 NUL 语义都安全）。
using System.IO;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace AIPDF.Windows.Rendering;

/// <summary>
/// 手写 P/Invoke：仅限 CppSharp 封送有缺陷的 UInt16*（FPDF_WCHAR）与 double* 参数函数。
/// 与 spike 的 PdfiumNative 同模式，绑定同一个 pdfium.dll。数组参数由 P/Invoke 封送器
/// 自动固定为指针，无需 /unsafe 编译选项。
/// </summary>
internal static class PdfiumTextNative
{
    private const string Lib = "pdfium";

    [DllImport(Lib, EntryPoint = "FPDFText_GetText", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetText(IntPtr text_page, int start_index, int count, ushort[] result);

    [DllImport(Lib, EntryPoint = "FPDFText_FindStart", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFText_FindStart(IntPtr text_page, ushort[] findwhat, ulong flags, int start_index);

    [DllImport(Lib, EntryPoint = "FPDFText_FindNext", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_FindNext(IntPtr handle);

    [DllImport(Lib, EntryPoint = "FPDFText_FindClose", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFText_FindClose(IntPtr handle);

    [DllImport(Lib, EntryPoint = "FPDFText_GetSchResultIndex", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetSchResultIndex(IntPtr handle);

    [DllImport(Lib, EntryPoint = "FPDFText_GetSchCount", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetSchCount(IntPtr handle);

    [DllImport(Lib, EntryPoint = "FPDFText_CountRects", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountRects(IntPtr text_page, int start_index, int count);

    [DllImport(Lib, EntryPoint = "FPDFText_GetRect", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetRect(IntPtr text_page, int rect_index, out double left, out double top, out double right, out double bottom);

    // 选区前置缺口【H-5】：鼠标点 → 字符下标。坐标 = PDF 页坐标（左下原点、pt、未旋转空间）。
    // 返回命中字符下标；-1 表示容差内无字符。无字符串/缓冲参数，但为与文件头「携带缓冲的 FPDFText_* 一律手写」纪律一致，仍走手写导出。
    [DllImport(Lib, EntryPoint = "FPDFText_GetCharIndexAtPos", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharIndexAtPos(IntPtr text_page, double x, double y, double x_tolerance, double y_tolerance);

    // 字符矩形【H-5】：注意参数序为 (left, right, bottom, top)，与 FPDFText_GetRect（left, top, right, bottom）不同！
    // 输出为 PDF 页坐标（左下原点、pt），消费方负责换算左上原点（见 CharBoxToPdfRect）。
    [DllImport(Lib, EntryPoint = "FPDFText_GetCharBox", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharBox(IntPtr text_page, int index, out double left, out double right, out double bottom, out double top);
}

/// <summary>矩形（左上原点、pt 单位），与 PdfCoordinateMapper 的坐标口径一致。</summary>
public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>FPDF_WCHAR（UTF-16）缓冲助手，与 spike 的 Utf16 同模式。</summary>
internal static class Utf16Buffer
{
    public static ushort[] ToBuffer(string s)
    {
        var buf = new ushort[s.Length + 1];
        for (var i = 0; i < s.Length; i++) buf[i] = s[i];
        buf[s.Length] = 0;
        return buf;
    }

    public static string FromBuffer(ushort[] buf, int count)
    {
        var sb = new System.Text.StringBuilder(count);
        for (var i = 0; i < count && i < buf.Length && buf[i] != 0; i++) sb.Append((char)buf[i]);
        return sb.ToString();
    }
}

/// <summary>
/// PDFium 文本层服务：持久文档句柄（独立于渲染服务的字节固定副本）之上的 FPDFText_* 封装。
/// 提供：页文本提取、字符区间矩形（CountRects/GetRect 原生按行合并）、原生命中测试（Find*）。
/// 与 T1 渲染服务共用 PdfiumLock.Global；与渲染服务各自持句柄是 V1 的刻意取舍
/// （T1 句柄表未暴露访问器，合并句柄推迟到单栈收敛评估，见 notepad）。
/// </summary>
public sealed class PdfiumTextService
{
    /// <summary>累计页文本提取次数（可观测：索引会话内复用验证、性能分析）。</summary>
    private long _extractionCount;

    /// <summary>提取调用总次数（Interlocked 读，线程安全）。</summary>
    public long ExtractionCount => Interlocked.Read(ref _extractionCount);

    public PdfiumTextService()
    {
        PdfiumLock.EnsureInitialized();
    }

    // ------------------------------------------------------------------ 文档生命周期

    /// <summary>
    /// 持久文本层文档句柄：文件字节固定（pinned）供 PDFium 引用，Dispose 时关闭并解锁。
    /// 调用方负责 Dispose（SearchIndexService 不接管生命周期）。
    /// </summary>
    public sealed class TextDocument : IDisposable
    {
        public FpdfDocumentT Handle { get; }
        public int PageCount { get; }
        public string Path { get; }

        private GCHandle _pin;
        private bool _disposed;

        internal TextDocument(FpdfDocumentT handle, int pageCount, string path, GCHandle pin)
        {
            Handle = handle;
            PageCount = pageCount;
            Path = path;
            _pin = pin;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Handle is not null)
            {
                lock (PdfiumLock.Global) fpdfview.FPDF_CloseDocument(Handle);
            }
            if (_pin.IsAllocated) _pin.Free();
            GC.SuppressFinalize(this);
        }

        internal void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// 打开磁盘 PDF 的持久文本层句柄。密码一次性传入 PDFium，不存储（V1 降级点③）。
    /// 失败抛 <see cref="PdfiumOpenException"/>（复用 T1 的结构化异常，带 FPDF_GetLastError）。
    /// </summary>
    public TextDocument OpenDocument(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new PdfiumOpenException(fullPath, 0, $"文件不存在: {fullPath}");

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

        int pageCount;
        lock (PdfiumLock.Global) pageCount = fpdfview.FPDF_GetPageCount(handle);

        return new TextDocument(handle, pageCount, fullPath, pin);
    }

    // ------------------------------------------------------------------ 页文本提取

    /// <summary>提取整页文本（UTF-16，含 PDFium 行分隔符 \r\n）。无文本层页返回空串。</summary>
    public string ExtractPageText(TextDocument doc, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);

        lock (PdfiumLock.Global)
        {
            Interlocked.Increment(ref _extractionCount);
            var page = fpdfview.FPDF_LoadPage(doc.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
                throw new InvalidOperationException($"页加载失败: {doc.Path} 第 {pageIndex} 页");
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException($"FPDFText_LoadPage 失败: {doc.Path} 第 {pageIndex} 页");
                try
                {
                    var n = fpdf_text.FPDFTextCountChars(textPage);
                    if (n <= 0) return string.Empty;
                    // 缓冲 n+2：无论 CountChars 是否含隐式 NUL 都安全（spike 同模式）
                    var buf = new ushort[n + 2];
                    var copied = PdfiumTextNative.FPDFText_GetText(textPage.__Instance, 0, n, buf);
                    return copied <= 0 ? string.Empty : Utf16Buffer.FromBuffer(buf, copied);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>页字符数（FPDFText_CountChars）。无文本层页返回 0。</summary>
    public int CountPageChars(TextDocument doc, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);

        lock (PdfiumLock.Global)
        {
            var page = fpdfview.FPDF_LoadPage(doc.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
                throw new InvalidOperationException($"页加载失败: {doc.Path} 第 {pageIndex} 页");
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException($"FPDFText_LoadPage 失败: {doc.Path} 第 {pageIndex} 页");
                try
                {
                    return fpdf_text.FPDFTextCountChars(textPage);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    // ------------------------------------------------------------------ 命中矩形

    /// <summary>
    /// 字符区间 [charStart, charStart + charCount) 的矩形，按行合并（FPDFText_CountRects +
    /// FPDFText_GetRect，原生按行合并，多行命中返回多个矩形）。返回左上原点 pt 坐标
    /// （PDF 左下原点换算：displayTop = pageHeight - pdfTop）。
    /// 已知限制：/Rotate 旋转页的矩形为未旋转页面空间，旋转校正由消费方
    /// （PdfCoordinateMapper，PRD 4.2.2）处理。
    /// </summary>
    public IReadOnlyList<PdfRect> GetTextRects(TextDocument doc, int pageIndex, int charStart, int charCount)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);
        if (charCount <= 0) return Array.Empty<PdfRect>();

        lock (PdfiumLock.Global)
        {
            var page = fpdfview.FPDF_LoadPage(doc.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
                throw new InvalidOperationException($"页加载失败: {doc.Path} 第 {pageIndex} 页");
            try
            {
                var pageHeight = fpdfview.FPDF_GetPageHeightF(page);
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException($"FPDFText_LoadPage 失败: {doc.Path} 第 {pageIndex} 页");
                try
                {
                    var rectCount = PdfiumTextNative.FPDFText_CountRects(
                        textPage.__Instance, charStart, charCount);
                    if (rectCount <= 0) return Array.Empty<PdfRect>();

                    var rects = new List<PdfRect>(rectCount);
                    for (var r = 0; r < rectCount; r++)
                    {
                        var ok = PdfiumTextNative.FPDFText_GetRect(
                            textPage.__Instance, r, out var l, out var t, out var rt, out var b);
                        if (ok == 0) continue;
                        // PDF 空间（左下原点，top > bottom）→ 左上原点（top < bottom）
                        var displayTop = pageHeight - Math.Max(t, b);
                        var displayBottom = pageHeight - Math.Min(t, b);
                        rects.Add(new PdfRect(
                            Math.Min(l, rt), displayTop, Math.Max(l, rt), displayBottom));
                    }
                    return rects;
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    // ------------------------------------------------------------------ 选区命中（H-5，T2）

    /// <summary>
    /// 页面坐标（PDF 左下原点、pt、未旋转空间）→ 最近字符下标；容差内无字符返回 -1。
    /// 缺省容差 8pt（点击靠近字符边缘仍可命中）。仅在 <see cref="PdfiumLock.Global"/> 内调用原生（R12）。
    /// 已知限制：/Rotate 旋转页坐标为未旋转页面空间；旋转校正由消费方禁用（M-11）。
    /// </summary>
    public int GetCharIndexAtPoint(TextDocument doc, int pageIndex, double xPt, double yPt, double tolerance = 8.0)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);
        if (tolerance < 0) tolerance = 0;
        if (double.IsNaN(xPt) || double.IsNaN(yPt)) return -1;

        lock (PdfiumLock.Global)
        {
            var page = fpdfview.FPDF_LoadPage(doc.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
                throw new InvalidOperationException($"页加载失败: {doc.Path} 第 {pageIndex} 页");
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException($"FPDFText_LoadPage 失败: {doc.Path} 第 {pageIndex} 页");
                try
                {
                    return PdfiumTextNative.FPDFText_GetCharIndexAtPos(
                        textPage.__Instance, xPt, yPt, tolerance, tolerance);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// 单字符矩形（左上原点、pt），换算自 FPDFText_GetCharBox 的 PDF 左下原点坐标。
    /// 下标越界/无字符返回 null（不抛，命中测试应容错）。
    /// </summary>
    public PdfRect? GetCharPageRect(TextDocument doc, int pageIndex, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);

        lock (PdfiumLock.Global)
        {
            var page = fpdfview.FPDF_LoadPage(doc.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
                throw new InvalidOperationException($"页加载失败: {doc.Path} 第 {pageIndex} 页");
            try
            {
                var pageHeight = fpdfview.FPDF_GetPageHeightF(page);
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException($"FPDFText_LoadPage 失败: {doc.Path} 第 {pageIndex} 页");
                try
                {
                    var ok = PdfiumTextNative.FPDFText_GetCharBox(
                        textPage.__Instance, charIndex, out var l, out var r, out var b, out var t);
                    return ok == 0 ? null : CharBoxToPdfRect(l, r, b, t, pageHeight);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// FPDFText_GetCharBox 原始输出（PDF 左下原点）→ 左上原点矩形（与 <see cref="PdfRect"/> 口径一致）。
    /// 纯函数（可单测，无需原生）：参数序为 (left, right, bottom, top)。
    /// </summary>
    public static PdfRect CharBoxToPdfRect(double left, double right, double bottom, double top, double pageHeightPt)
        => new(
            Math.Min(left, right),
            pageHeightPt - Math.Max(top, bottom),
            Math.Max(left, right),
            pageHeightPt - Math.Min(top, bottom));

    /// <summary>提取字符区间 [charStart, charStart + charCount) 的文本（UTF-16）。空区间/越界返回空串。</summary>
    public string GetTextRange(TextDocument doc, int pageIndex, int charStart, int charCount)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);
        if (charStart < 0 || charCount <= 0) return string.Empty;

        var full = ExtractPageText(doc, pageIndex);
        if (charStart >= full.Length) return string.Empty;
        var count = Math.Min(charCount, full.Length - charStart);
        return count <= 0 ? string.Empty : full.Substring(charStart, count);
    }

    // ------------------------------------------------------------------ 原生命中测试

    /// <summary>
    /// 原生命中测试（FPDFText_FindStart/FindNext，flags=0 大小写不敏感，PRD 4.2.3）。
    /// 返回全部匹配的起始字符下标（递增）。FindNext 语义与防死循环护栏对照 spike：
    /// 非 0 = 命中；下标取 GetSchResultIndex，且要求严格递增。
    /// </summary>
    public IReadOnlyList<int> FindInPage(TextDocument doc, int pageIndex, string term)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.ThrowIfDisposed();
        ValidatePageIndex(doc, pageIndex);
        if (string.IsNullOrEmpty(term)) return Array.Empty<int>();

        lock (PdfiumLock.Global)
        {
            var findBuf = Utf16Buffer.ToBuffer(term);
            var page = fpdfview.FPDF_LoadPage(doc.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
                throw new InvalidOperationException($"页加载失败: {doc.Path} 第 {pageIndex} 页");
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException($"FPDFText_LoadPage 失败: {doc.Path} 第 {pageIndex} 页");
                try
                {
                    var search = PdfiumTextNative.FPDFText_FindStart(textPage.__Instance, findBuf, 0, 0);
                    if (search == IntPtr.Zero) return Array.Empty<int>();
                    try
                    {
                        var hits = new List<int>();
                        var last = -1;
                        while (PdfiumTextNative.FPDFText_FindNext(search) > 0)
                        {
                            var pos = PdfiumTextNative.FPDFText_GetSchResultIndex(search);
                            if (pos <= last) break; // 安全护栏：下标必须严格递增（spike 实测语义）
                            last = pos;
                            hits.Add(pos);
                        }
                        return hits;
                    }
                    finally
                    {
                        PdfiumTextNative.FPDFText_FindClose(search);
                    }
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    // ------------------------------------------------------------------ 校验

    private static void ValidatePageIndex(TextDocument doc, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"页码超出范围：0–{doc.PageCount - 1}。");
    }
}
