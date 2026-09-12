// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T2 单栈收敛基础：以 PdfiumRenderService 为后端的预览文档。
//
// 句柄所有权（硬约束 B2）：每个实例【独占】一个 PdfiumRenderService，绝不共用阅读器或
// 其他预览的实例——OpenDocument 按路径去重且 CloseDocument 无条件移除条目，共享会导致
// 预览 Dispose 关闭阅读器句柄（use-after-free/白屏）。Dispose 只关闭本实例句柄。
//
// 旋转语义（尖刺 #1 实证，.omo/evidence/v3-task2-rotation-spike.txt）：PDFium
// FPDF_GetPageWidthF/HeightF 已旋转感知，0/90/180/270 与旧栈 PDFtoImage 可见尺寸一致，
// 故【不做 derotation】（盲目补偿会双重旋转并破坏 PdfCoordinateMapper）。
//
// 表单（Q5）：构造后即启用预览表单填充环境，Widget 由 FPDF_FFLDraw 绘制（见服务内部实现）。
using System.Windows.Media.Imaging;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Controls;

/// <summary>
/// PDFium 后端预览文档（V3 T7 单栈收敛后唯一的预览文档实现，旧 PDFtoImage 栈已移除）。
/// 消费契约与历史实现一致：页数、可见页 pt 尺寸、<see cref="CreateMapper(int,double)"/>、按 zoom 渲染；
/// 渲染改为后台线程 + 冻结 <see cref="BitmapSource"/>（控件负责封送回 UI 线程绑定，F3-BLK-1）。
/// 【线程边界】构造/打开为一次性同步原生步骤（与阅读器 LoadDocument 一致，便于同步抛错给工具页），
/// 重复且耗时的页渲染一律在后台线程执行；绝不在后台线程改 UI 绑定状态。
/// </summary>
public sealed class PdfiumPreviewDocument : IDisposable
{
    private readonly PdfiumRenderService _service; // 独占实例（B2）
    private readonly string _docId;
    private bool _disposed;

    /// <summary>从 PDF 字节构建（FPDF_LoadMemDocument64）。解析失败或密码错误时抛异常。</summary>
    public PdfiumPreviewDocument(byte[] pdfBytes, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        _service = new PdfiumRenderService();
        try
        {
            _docId = _service.OpenDocumentFromBytes(pdfBytes, password);
            _service.EnableFormFill(_docId); // Q5：显式预览表单渲染模式
        }
        catch
        {
            _service.Dispose();
            throw;
        }

        PageCount = _service.GetPageCount(_docId);
    }

    private PdfiumPreviewDocument(PdfiumRenderService service, string docId)
    {
        _service = service;
        _docId = docId;
        _service.EnableFormFill(_docId);
        PageCount = _service.GetPageCount(_docId);
    }

    /// <summary>
    /// 打开磁盘 PDF。以 FileShare.ReadWrite 共享读打开（沿用历史预览打开语义），
    /// 允许预览刚被引擎写入/被其他进程占用的文件。格式非法或密码错误时抛异常，由调用方处理。
    /// </summary>
    public static PdfiumPreviewDocument OpenFile(string filePath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var service = new PdfiumRenderService();
        try
        {
            var docId = service.OpenDocumentSharedRead(filePath, password);
            return new PdfiumPreviewDocument(service, docId);
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    /// <summary>总页数。</summary>
    public int PageCount { get; }

    /// <summary>文档标识（测试/诊断用：查询在途计数等）。</summary>
    public string DocumentId => _docId;

    /// <summary>
    /// 诊断用：页旋转角度 0/90/180/270（不参与坐标换算——尖刺 #1 已证 PDFium 尺寸旋转感知）。
    /// </summary>
    public int GetPageRotation(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return _service.GetPageRotation(_docId, pageIndex);
    }

    /// <summary>可见页尺寸（pt）。已旋转感知，与旧栈一致；不做 derotation。</summary>
    public (double WidthPt, double HeightPt) GetPageSize(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        var size = _service.GetPageSize(_docId, pageIndex)
            ?? throw new ObjectDisposedException(nameof(PdfiumPreviewDocument));
        return (size.Width, size.Height);
    }

    /// <summary>构建该页在给定 zoom 下的坐标映射（<see cref="PdfCoordinateMapper"/> 契约不变）。</summary>
    public PdfCoordinateMapper CreateMapper(int pageIndex, double zoom)
    {
        ValidatePageIndex(pageIndex);
        var (widthPt, heightPt) = GetPageSize(pageIndex);
        return new PdfCoordinateMapper
        {
            PageWidthPt = widthPt,
            PageHeightPt = heightPt,
            Zoom = zoom,
        };
    }

    /// <summary>
    /// 后台渲染指定页为冻结 <see cref="BitmapSource"/>（像素尺寸 = <see cref="PdfCoordinateMapper.PixelSize"/>，
    /// 与旧栈一致）。表单模式已启用 → Widget 由 FFLDraw 绘制（Q5）。
    /// 【线程】返回位图已 Freeze，调用方须封送回 UI 线程再绑定（F3-BLK-1）。
    /// </summary>
    public Task<BitmapSource> RenderPageAsync(int pageIndex, double zoom)
    {
        ValidatePageIndex(pageIndex);
        var (pixelWidth, pixelHeight) = CreateMapper(pageIndex, zoom).PixelSize;
        return _service.RenderPagePreviewAsync(_docId, pageIndex, pixelWidth, pixelHeight);
    }

    /// <summary>该文档在途原生调用计数（Dispose 后应为 0；测试断言无句柄泄漏）。</summary>
    public int GetInFlightCount() => _service.GetInFlightCount(_docId);

    /// <summary>释放本实例独占句柄与表单环境（不触碰其他实例）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ValidatePageIndex(int pageIndex)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"页码超出范围：0–{PageCount - 1}。");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
