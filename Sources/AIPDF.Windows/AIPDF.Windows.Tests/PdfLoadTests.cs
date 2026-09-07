// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using AIPDF.Windows.Controls;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// PDF 加载与渲染测试（PDFtoImage/pdfium 真实调用，合成 PDF 不读个人文档）。
/// </summary>
public sealed class PdfLoadTests : IDisposable
{
    private readonly string _tempDirectory;

    public PdfLoadTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"aipdf-preview-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private string WriteTempPdf(string fileName, byte[] bytes)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void OpenFile_CountsPages()
    {
        var bytes = TestPdfFactory.Create((612, 792), (400, 300));

        using var document = new PdfPreviewDocument(bytes);

        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void OpenFile_InvalidPdf_Throws()
    {
        Assert.ThrowsAny<Exception>(() => new PdfPreviewDocument("this is not a pdf"u8.ToArray()));
    }

    [Fact]
    public void Constructor_NullBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfPreviewDocument(null!));
    }

    [Fact]
    public void GetPageSize_ReturnsPointsPerVisiblePage()
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        var page0 = document.GetPageSize(0);
        var page1 = document.GetPageSize(1);

        Assert.Equal(612, page0.WidthPt, 0.5);
        Assert.Equal(792, page0.HeightPt, 0.5);
        Assert.Equal(400, page1.WidthPt, 0.5);
        Assert.Equal(300, page1.HeightPt, 0.5);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void GetPageSize_InvalidIndex_Throws(int pageIndex)
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        Assert.Throws<ArgumentOutOfRangeException>(() => document.GetPageSize(pageIndex));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void RenderPage_InvalidIndex_Throws(int pageIndex)
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderPage(pageIndex, 1.0));
    }

    [Fact]
    public void RenderPage_PixelDimensionsFollowZoom()
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        var page0 = document.RenderPage(0, 1.0);
        var page1ZoomedOut = document.RenderPage(1, 0.25);

        Assert.Equal(816, page0.PixelWidth);
        Assert.Equal(1056, page0.PixelHeight);
        Assert.Equal(133, page1ZoomedOut.PixelWidth);  // 400*4/3*0.25 = 133.33 → 133
        Assert.Equal(100, page1ZoomedOut.PixelHeight); // 300*4/3*0.25 = 100
    }

    [Fact]
    public void RenderPage_BufferIsBgraWithWhiteBackground()
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((612, 792)));

        var rendered = document.RenderPage(0, 1.0);

        Assert.Equal(rendered.PixelWidth * rendered.PixelHeight * 4, rendered.BgraPixels.Length);
        // 白底渲染：不是全 0（有内容），且首像素（页面角落）为不透明白 BGRA = FF FF FF FF
        Assert.Contains(rendered.BgraPixels, b => b != 0);
        Assert.Equal(0xFF, rendered.BgraPixels[0]);
        Assert.Equal(0xFF, rendered.BgraPixels[1]);
        Assert.Equal(0xFF, rendered.BgraPixels[2]);
        Assert.Equal(0xFF, rendered.BgraPixels[3]);
    }

    [Fact]
    public void RenderPage_EachRenderIsIndependent_NoMultiPageCache()
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        var first = document.RenderPage(0, 1.0);
        var second = document.RenderPage(0, 1.0);

        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
        Assert.Equal(first.BgraPixels, second.BgraPixels);
        Assert.NotSame(first.BgraPixels, second.BgraPixels); // 每次新缓冲，不共享缓存
    }

    [Fact]
    public void OpenFile_SharedReadWriteOpenWhileSessionAlive()
    {
        // FileShare.ReadWrite 契约：会话存续期间，其他进程仍能以 ReadWrite 再打开同一文件
        var path = WriteTempPdf("shared.pdf", TestPdfFactory.Create((612, 792)));

        var document = PdfPreviewDocument.OpenFile(path);
        try
        {
            using var concurrent = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.True(concurrent.Length > 0);
        }
        finally
        {
            document.Dispose();
        }
    }

    [Fact]
    public void OpenFile_LoadsRealFileContent()
    {
        var path = WriteTempPdf("two-pages.pdf", TestPdfFactory.Create((612, 792), (400, 300)));

        using var document = PdfPreviewDocument.OpenFile(path);

        Assert.Equal(2, document.PageCount);
        var (widthPt, heightPt) = document.GetPageSize(1);
        Assert.Equal(400, widthPt, 0.5);
        Assert.Equal(300, heightPt, 0.5);
    }

    [Fact]
    public void CreateMapper_PageSizeAndZoom()
    {
        using var document = new PdfPreviewDocument(TestPdfFactory.Create((400, 300)));

        var mapper = document.CreateMapper(0, 2.0);

        Assert.Equal(400, mapper.PageWidthPt, 0.5);
        Assert.Equal(300, mapper.PageHeightPt, 0.5);
        Assert.Equal(2.0, mapper.Zoom);
        Assert.Equal((1067, 800), mapper.PixelSize); // 400*4/3*2 = 1066.67 → 1067
    }
}
