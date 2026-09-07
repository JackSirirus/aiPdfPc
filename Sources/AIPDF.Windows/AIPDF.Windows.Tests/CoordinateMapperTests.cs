// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Controls;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// PdfCoordinateMapper 纯函数测试。
/// 基准页取 US Letter（612 × 792 pt）；96dpi 基准下 1 pt = 4/3 DIP。
/// pdf 坐标约定 = 可见页左上原点（backend/engine.py area()）。
/// </summary>
public sealed class CoordinateMapperTests
{
    private const double DpiScale = PdfCoordinateMapper.DpiScale; // 4/3

    private static PdfCoordinateMapper Letter(double zoom = 1.0) => new()
    {
        PageWidthPt = 612,
        PageHeightPt = 792,
        Zoom = zoom,
    };

    [Fact]
    public void ViewScale_MultipliesZoomAndDpiScale()
    {
        Assert.Equal(1.0 * DpiScale, Letter(1.0).ViewScale, 9);
        Assert.Equal(2.0 * DpiScale, Letter(2.0).ViewScale, 9);
        Assert.Equal(0.25 * DpiScale, Letter(0.25).ViewScale, 9);
    }

    [Fact]
    public void ViewSize_PageTimesViewScale()
    {
        var mapper = Letter(1.5);

        Assert.Equal(612 * 1.5 * DpiScale, mapper.ViewWidth, 9);
        Assert.Equal(792 * 1.5 * DpiScale, mapper.ViewHeight, 9);
    }

    [Fact]
    public void PixelSize_ExactAt96DpiLetter()
    {
        var (width, height) = Letter(1.0).PixelSize;

        Assert.Equal(816, width);   // 612 * 4/3
        Assert.Equal(1056, height); // 792 * 4/3
    }

    [Theory]
    [InlineData(400, 300, 0.25, 133, 100)]  // 400*4/3*0.25 = 133.33 → 133
    [InlineData(400, 300, 1.0, 533, 400)]   // 400*4/3 = 533.33 → 533
    [InlineData(612, 792, 4.0, 3264, 4224)]
    [InlineData(72, 72, 0.25, 24, 24)]
    public void PixelSize_RoundsPageTimesZoomTimesDpiScale(
        double widthPt, double heightPt, double zoom, int expectedW, int expectedH)
    {
        var mapper = new PdfCoordinateMapper { PageWidthPt = widthPt, PageHeightPt = heightPt, Zoom = zoom };

        Assert.Equal((expectedW, expectedH), mapper.PixelSize);
    }

    [Fact]
    public void ViewToPdf_TopLeftOrigin_PtUnits()
    {
        var mapper = Letter(1.0);

        var pdf = mapper.ViewToPdf(new Point(816, 1056)); // 视图右下角（1:1 时）

        Assert.Equal(612, pdf.X, 6);
        Assert.Equal(792, pdf.Y, 6);
    }

    [Fact]
    public void PdfToView_IsInverseOfViewToPdf()
    {
        var mapper = Letter(2.5);
        var view = new Point(123.4, 567.8);

        var roundtrip = mapper.PdfToView(mapper.ViewToPdf(view));

        Assert.Equal(view.X, roundtrip.X, 9);
        Assert.Equal(view.Y, roundtrip.Y, 9);
    }

    [Fact]
    public void ViewToPdfRect_PreservesCornersAndSize()
    {
        var mapper = Letter(2.0);
        var viewRect = new Rect(100, 50, 400, 200);

        var pdfRect = mapper.ViewToPdfRect(viewRect);
        var backToView = mapper.PdfToViewRect(pdfRect);

        Assert.Equal(100 / (2.0 * DpiScale), pdfRect.X, 9);
        Assert.Equal(50 / (2.0 * DpiScale), pdfRect.Y, 9);
        Assert.Equal(400 / (2.0 * DpiScale), pdfRect.Width, 9);
        Assert.Equal(200 / (2.0 * DpiScale), pdfRect.Height, 9);
        Assert.Equal(viewRect, backToView);
    }

    [Fact]
    public void ViewToPdfRect_NormalizesDragFromBottomRightToTopLeft()
    {
        var mapper = Letter(1.0);
        // 右下往左上拖：锚点 (500,400)，当前位置 (200,150)。Rect(Point,Point) 自动规范化
        var dragged = new Rect(new Point(500, 400), new Point(200, 150));

        var pdfRect = mapper.ViewToPdfRect(dragged);

        Assert.True(pdfRect.Width > 0);
        Assert.True(pdfRect.Height > 0);
        Assert.Equal(200 / DpiScale, pdfRect.X, 9);
        Assert.Equal(150 / DpiScale, pdfRect.Y, 9);
        Assert.Equal(300 / DpiScale, pdfRect.Width, 9);
        Assert.Equal(250 / DpiScale, pdfRect.Height, 9);
    }

    [Fact]
    public void ClampToPage_ClipsOverflowToVisiblePage()
    {
        var mapper = Letter(1.0);
        // 左上 50,40 起，延伸出页面右下边界
        var overflowing = new Rect(600, 780, 100, 50);

        var clamped = mapper.ClampToPage(overflowing);

        Assert.Equal(600, clamped.X, 9);
        Assert.Equal(780, clamped.Y, 9);
        Assert.Equal(12, clamped.Width, 9);   // 612 - 600
        Assert.Equal(12, clamped.Height, 9);  // 792 - 780
    }

    [Fact]
    public void ClampToPage_FullyOutsidePage_IsEmpty()
    {
        var mapper = Letter(1.0);

        var clamped = mapper.ClampToPage(new Rect(700, 900, 100, 100));

        Assert.True(clamped.IsEmpty);
    }

    [Fact]
    public void ClampToPage_NegativeDragOrigin_IsNormalizedBeforeClip()
    {
        var mapper = Letter(1.0);
        // 锚点 (100,100) 往左上拖到 (50,60)：规范化后 view 区域 = 50,60 → 100,100
        var dragged = new Rect(new Point(100, 100), new Point(50, 60));

        var clamped = mapper.ClampToPage(mapper.ViewToPdfRect(dragged));

        Assert.Equal(50 / DpiScale, clamped.X, 9);
        Assert.Equal(60 / DpiScale, clamped.Y, 9);
        Assert.Equal(50 / DpiScale, clamped.Width, 9);
        Assert.Equal(40 / DpiScale, clamped.Height, 9);
    }

    [Fact]
    public void MinRegionPt_MatchesUpstreamThreePointThreshold()
    {
        Assert.Equal(3.0, PdfCoordinateMapper.MinRegionPt);
    }

    [Theory]
    [InlineData(0.25, true, 0.33)]
    [InlineData(1.0, true, 1.5)]
    [InlineData(4.0, true, 4.0)]     // 到顶不再放大
    [InlineData(1.0, false, 0.67)]
    [InlineData(0.25, false, 0.25)]  // 到底不再缩小
    [InlineData(1.49, false, 1.0)]
    public void NextZoomPreset_StepsThroughLadder(double current, bool zoomIn, double expected)
    {
        Assert.Equal(expected, PdfPreviewControl.NextZoomPreset(current, zoomIn));
    }
}
