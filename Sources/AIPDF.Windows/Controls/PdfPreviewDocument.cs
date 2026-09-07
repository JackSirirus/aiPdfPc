// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using PDFtoImage;
using SkiaSharp;

namespace AIPDF.Windows.Controls;

/// <summary>
/// 无 WPF 依赖的 PDF 文档包装：页数、页面 pt 尺寸、按 zoom 渲染 BGRA 像素。
/// 每次调用按需渲染，不缓存多页位图。渲染输出 Bgra8888（Premul），
/// 与 WPF PixelFormats.Bgra32 直接对应。
/// </summary>
public sealed class PdfPreviewDocument : IDisposable
{
    private byte[] _pdfBytes;
    private readonly string? _password;
    private bool _disposed;

    /// <summary>从 PDF 字节构建；会立即解析以获得页数，格式非法或密码错误时抛异常。</summary>
    public PdfPreviewDocument(byte[] pdfBytes, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        _pdfBytes = pdfBytes;
        _password = string.IsNullOrEmpty(password) ? null : password;
        PageCount = Conversion.GetPageSizes(_pdfBytes, _password).Count;
    }

    /// <summary>打开磁盘上的 PDF。以 FileShare.ReadWrite 共享读打开，
    /// 允许文件同时被其他进程读写（源文档只读、输出目录可继续写文件的场景）。</summary>
    public static PdfPreviewDocument OpenFile(string filePath, string? password = null)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new PdfPreviewDocument(memory.ToArray(), password);
    }

    /// <summary>总页数。</summary>
    public int PageCount { get; }

    /// <summary>页面尺寸（pt，可见页，来自 PDFium）。</summary>
    public (double WidthPt, double HeightPt) GetPageSize(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        var size = Conversion.GetPageSizes(_pdfBytes, _password)[pageIndex];
        return (size.Width, size.Height);
    }

    /// <summary>构建该页在给定 zoom 下的坐标映射。</summary>
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

    /// <summary>渲染指定页。像素尺寸 = round(pt × zoom × DpiScale)，与 PdfCoordinateMapper.PixelSize 一致。</summary>
    public RenderedPage RenderPage(int pageIndex, double zoom)
    {
        ValidatePageIndex(pageIndex);
        var (pixelWidth, pixelHeight) = CreateMapper(pageIndex, zoom).PixelSize;

        var options = new RenderOptions
        {
            Width = pixelWidth,
            Height = pixelHeight,
            WithAnnotations = true,
            WithFormFill = true,
            BackgroundColor = SKColors.White,
        };

        using var rendered = Conversion.ToImage(_pdfBytes, pageIndex, _password, options);
        using var converted = rendered.ColorType == SKColorType.Bgra8888 ? null : rendered.Copy(SKColorType.Bgra8888);
        var source = converted ?? rendered;

        return new RenderedPage(source.Width, source.Height, (byte[])source.Bytes.Clone());
    }

    /// <summary>单页渲染结果：BGRA（Premul）像素，每像素 4 字节，行 stride = PixelWidth × 4。</summary>
    public sealed record RenderedPage(int PixelWidth, int PixelHeight, byte[] BgraPixels);

    private void ValidatePageIndex(int pageIndex)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"页码超出范围：0–{PageCount - 1}。");
        }
    }

    /// <summary>释放文档字节引用（PDFtoImage 每次调用无状态，无原生句柄需要清理）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pdfBytes = [];
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
