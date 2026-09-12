// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Controls;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// PDF 加载与渲染测试（PDFium 后端真实调用，合成 PDF 不读个人文档）。
/// V3 T7 单栈收敛：旧 PDFtoImage/PdfPreviewDocument 栈已删除，统一迁移到 PdfiumPreviewDocument。
/// 渲染改为后台异步 + 冻结 <see cref="BitmapSource"/>（F3-BLK-1）。
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

        using var document = new PdfiumPreviewDocument(bytes);

        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void OpenFile_InvalidPdf_Throws()
    {
        Assert.ThrowsAny<Exception>(() => new PdfiumPreviewDocument("this is not a pdf"u8.ToArray()));
    }

    [Fact]
    public void Constructor_NullBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfiumPreviewDocument(null!));
    }

    [Fact]
    public void GetPageSize_ReturnsPointsPerVisiblePage()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

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
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        Assert.Throws<ArgumentOutOfRangeException>(() => document.GetPageSize(pageIndex));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void RenderPage_InvalidIndex_Throws(int pageIndex)
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        // 页索引校验在返回 Task 前同步抛出（与旧栈一致）
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = document.RenderPageAsync(pageIndex, 1.0); });
    }

    [Fact]
    public async Task RenderPage_PixelDimensionsFollowZoom()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        var page0 = await document.RenderPageAsync(0, 1.0);
        var page1ZoomedOut = await document.RenderPageAsync(1, 0.25);

        Assert.Equal(816, page0.PixelWidth);
        Assert.Equal(1056, page0.PixelHeight);
        Assert.Equal(133, page1ZoomedOut.PixelWidth);  // 400*4/3*0.25 = 133.33 → 133
        Assert.Equal(100, page1ZoomedOut.PixelHeight); // 300*4/3*0.25 = 100
    }

    [Fact]
    public async Task RenderPage_WhiteOpaqueBackground_AndFrozen()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792)));

        var bitmap = await document.RenderPageAsync(0, 1.0);

        Assert.True(bitmap.IsFrozen, "预览位图必须冻结才能跨线程绑定（F3-BLK-1）");
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        Assert.Equal(stride * bitmap.PixelHeight, pixels.Length);
        // 白底渲染：页面角落为不透明白 BGRA = FF FF FF FF
        Assert.Equal(0xFF, pixels[0]);
        Assert.Equal(0xFF, pixels[1]);
        Assert.Equal(0xFF, pixels[2]);
        Assert.Equal(0xFF, pixels[3]);
    }

    [Fact]
    public async Task RenderPage_EachRenderIsIndependent()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        var first = await document.RenderPageAsync(0, 1.0);
        var second = await document.RenderPageAsync(0, 1.0);

        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
        Assert.NotSame(first, second); // 每次新位图，不复用同一实例
    }

    [Fact]
    public void OpenFile_SharedReadWriteOpenWhileSessionAlive()
    {
        // FileShare.ReadWrite 契约：会话存续期间，其他进程仍能以 ReadWrite 再打开同一文件
        var path = WriteTempPdf("shared.pdf", TestPdfFactory.Create((612, 792)));

        var document = PdfiumPreviewDocument.OpenFile(path);
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

        using var document = PdfiumPreviewDocument.OpenFile(path);

        Assert.Equal(2, document.PageCount);
        var (widthPt, heightPt) = document.GetPageSize(1);
        Assert.Equal(400, widthPt, 0.5);
        Assert.Equal(300, heightPt, 0.5);
    }

    [Fact]
    public void CreateMapper_PageSizeAndZoom()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((400, 300)));

        var mapper = document.CreateMapper(0, 2.0);

        Assert.Equal(400, mapper.PageWidthPt, 0.5);
        Assert.Equal(300, mapper.PageHeightPt, 0.5);
        Assert.Equal(2.0, mapper.Zoom);
        Assert.Equal((1067, 800), mapper.PixelSize); // 400*4/3*2 = 1066.67 → 1067
    }
}
