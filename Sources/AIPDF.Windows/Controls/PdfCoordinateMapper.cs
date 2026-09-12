// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;

namespace AIPDF.Windows.Controls;

/// <summary>
/// 预览 view ↔ pdf 坐标换算的纯函数类（不可变）。
/// 坐标约定与 backend/engine.py 的 area() 一致：pdf 坐标指「可见旋转页、左上原点、pt 单位」；
/// view 坐标指页面位图左上原点的设备无关像素（DIP）。
/// </summary>
public sealed record PdfCoordinateMapper
{
    /// <summary>缩放范围，对应任务约定的 25%–400%。</summary>
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4.0;

    /// <summary>框选最小有效尺寸（pt），沿用上游 PDFPreview.swift 的 3pt 阈值。</summary>
    public const double MinRegionPt = 3.0;

    /// <summary>WPF 设备无关像素 / PDF 点（96/72）。96dpi 渲染时 1 pt = 4/3 px。</summary>
    public const double DpiScale = 96.0 / 72.0;

    /// <summary>可见页宽（pt）。</summary>
    public required double PageWidthPt { get; init; }

    /// <summary>可见页高（pt）。</summary>
    public required double PageHeightPt { get; init; }

    /// <summary>用户缩放倍数（0.25–4.0）。</summary>
    public required double Zoom { get; init; }

    /// <summary>view 像素 / pt。</summary>
    public double ViewScale => Zoom * DpiScale;

    /// <summary>页面在 view 中的宽（DIP）。</summary>
    public double ViewWidth => PageWidthPt * ViewScale;

    /// <summary>页面在 view 中的高（DIP）。</summary>
    public double ViewHeight => PageHeightPt * ViewScale;

    /// <summary>渲染位图像素尺寸 = round(pt × zoom × DpiScale)（至少 1px）。与 PdfiumPreviewDocument.RenderPageAsync 一致。</summary>
    public (int PixelWidth, int PixelHeight) PixelSize =>
        (PixelDimension(PageWidthPt * Zoom), PixelDimension(PageHeightPt * Zoom));

    /// <summary>view 像素 → pdf 点。</summary>
    public Point ViewToPdf(Point view) => new(view.X / ViewScale, view.Y / ViewScale);

    /// <summary>pdf 点 → view 像素。</summary>
    public Point PdfToView(Point pdf) => new(pdf.X * ViewScale, pdf.Y * ViewScale);

    /// <summary>view 矩形 → pdf 矩形（自动规范化负宽高）。</summary>
    public Rect ViewToPdfRect(Rect viewRect)
    {
        var topLeft = ViewToPdf(new Point(Math.Min(viewRect.Left, viewRect.Right), Math.Min(viewRect.Top, viewRect.Bottom)));
        var bottomRight = ViewToPdf(new Point(Math.Max(viewRect.Left, viewRect.Right), Math.Max(viewRect.Top, viewRect.Bottom)));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>pdf 矩形 → view 矩形（自动规范化负宽高）。</summary>
    public Rect PdfToViewRect(Rect pdfRect)
    {
        var topLeft = PdfToView(new Point(Math.Min(pdfRect.Left, pdfRect.Right), Math.Min(pdfRect.Top, pdfRect.Bottom)));
        var bottomRight = PdfToView(new Point(Math.Max(pdfRect.Left, pdfRect.Right), Math.Max(pdfRect.Top, pdfRect.Bottom)));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>与可见页求交并规范化；拖拽可越过页面边界，超界部分裁掉，完全在页外时为 Empty。</summary>
    public Rect ClampToPage(Rect pdfRect)
    {
        var normalized = Normalize(pdfRect);
        normalized.Intersect(new Rect(0, 0, PageWidthPt, PageHeightPt));
        return normalized;
    }

    /// <summary>规范化负宽高矩形（拖拽方向任意）。</summary>
    public static Rect Normalize(Rect rect)
    {
        if (rect.Width >= 0 && rect.Height >= 0)
        {
            return rect;
        }

        return new Rect(
            Math.Min(rect.Left, rect.Right),
            Math.Min(rect.Top, rect.Bottom),
            Math.Abs(rect.Width),
            Math.Abs(rect.Height));
    }

    private static int PixelDimension(double pagePt) => Math.Max(1, (int)Math.Round(pagePt * DpiScale, MidpointRounding.AwayFromZero));
}
