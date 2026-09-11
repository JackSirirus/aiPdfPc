// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T2 单栈收敛：PdfiumPreviewDocument（PDFium 后端预览文档）测试。
// 覆盖：页数、页尺寸（含旋转实证结论）、按 zoom 像素尺寸、冻结位图、mapper 契约、
// Dispose（在途归零/无泄漏）、打开语义（byte[]/共享读/非法字节）、句柄隔离回归、表单 Widget 绘制。
// 语料全部合成，不读个人文档。
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Rendering;

public sealed class PdfiumPreviewDocumentTests : IDisposable
{
    // mixed100 第 1 页 AcroForm 文本域（PyMuPDF 左上原点 pt，与 gen_corpus.py WIDGET_RECT 一致）
    private static readonly (double L, double T, double R, double B) WidgetRegion = (50, 700, 250, 730);

    private readonly string _tempDirectory;

    public PdfiumPreviewDocumentTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"aipdf-pdfium-preview-{Guid.NewGuid():N}");
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

    // ---------------------------------------------------------------- 页数 / 页尺寸 / 旋转

    [Fact]
    public void ByteArray_Ctor_CountsPages()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void GetPageSize_ReturnsVisiblePoints()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792), (400, 300)));

        Assert.Equal(612, document.GetPageSize(0).WidthPt, 0.5);
        Assert.Equal(792, document.GetPageSize(0).HeightPt, 0.5);
        Assert.Equal(400, document.GetPageSize(1).WidthPt, 0.5);
        Assert.Equal(300, document.GetPageSize(1).HeightPt, 0.5);
    }

    /// <summary>
    /// 尖刺 #1 结论固化：PDFium GetPageSize 已旋转感知，0/90/180/270 返回可见尺寸，
    /// 与旧栈 PDFtoImage Conversion.GetPageSizes 一致 → 不做 derotation（防双重旋转）。
    /// </summary>
    [Fact]
    public void Rotation_PageSize_IsVisible_NoDerotationNeeded()
    {
        var bytes = TestPdfFactory.CreateRotated(400, 300, 0, 90, 180, 270);
        using var document = new PdfiumPreviewDocument(bytes);

        Assert.Equal(4, document.PageCount);
        Assert.Equal(0, document.GetPageRotation(0));
        Assert.Equal(90, document.GetPageRotation(1));
        Assert.Equal(180, document.GetPageRotation(2));
        Assert.Equal(270, document.GetPageRotation(3));

        // 0/180 -> 400x300；90/270 -> 300x400（已旋转/可见尺寸）
        Assert.Equal((400.0, 300.0), document.GetPageSize(0));
        Assert.Equal((300.0, 400.0), document.GetPageSize(1));
        Assert.Equal((400.0, 300.0), document.GetPageSize(2));
        Assert.Equal((300.0, 400.0), document.GetPageSize(3));
    }

    [Fact]
    public void Rotation_PageSize_MatchesOldStack_VisibleSize()
    {
        // 交叉验证：与旧栈 PDFtoImage 可见尺寸数值一致（尖刺 #1 的四角度对照固化）
        var bytes = TestPdfFactory.CreateRotated(400, 300, 0, 90, 180, 270);
        using var document = new PdfiumPreviewDocument(bytes);
        using var oldStack = new PdfPreviewDocument(bytes);

        for (var i = 0; i < 4; i++)
        {
            var (w, h) = document.GetPageSize(i);
            var (ow, oh) = oldStack.GetPageSize(i);
            Assert.Equal(ow, w, 0.5);
            Assert.Equal(oh, h, 0.5);
        }
    }

    // ---------------------------------------------------------------- 像素尺寸 / 冻结位图 / mapper

    [Fact]
    public async Task RenderPageAsync_PixelSizeFollowsMapper_AndIsFrozen()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((612, 792)));

        var mapper = document.CreateMapper(0, 1.0);
        var bitmap = await document.RenderPageAsync(0, 1.0);

        Assert.True(bitmap.IsFrozen, "预览位图必须冻结才能跨线程绑定（F3-BLK-1）");
        Assert.Equal(mapper.PixelSize.PixelWidth, bitmap.PixelWidth);
        Assert.Equal(mapper.PixelSize.PixelHeight, bitmap.PixelHeight);
        Assert.Equal(816, bitmap.PixelWidth);   // 612 * 4/3
        Assert.Equal(1056, bitmap.PixelHeight); // 792 * 4/3
    }

    [Fact]
    public async Task RenderPageAsync_ZoomScalesPixels()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((400, 300)));

        var at100 = await document.RenderPageAsync(0, 1.0);
        var at25 = await document.RenderPageAsync(0, 0.25);

        Assert.Equal((533, 400), (at100.PixelWidth, at100.PixelHeight)); // 400*4/3, 300*4/3
        Assert.Equal((133, 100), (at25.PixelWidth, at25.PixelHeight));   // 400*4/3*0.25, 300*4/3*0.25
        // 像素尺寸与 mapper.PixelSize 逐值一致
        Assert.Equal(document.CreateMapper(0, 1.0).PixelSize, (at100.PixelWidth, at100.PixelHeight));
        Assert.Equal(document.CreateMapper(0, 0.25).PixelSize, (at25.PixelWidth, at25.PixelHeight));
    }

    [Fact]
    public void CreateMapper_ContractUnchanged()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((400, 300)));

        var mapper = document.CreateMapper(0, 2.0);

        Assert.Equal(400, mapper.PageWidthPt, 0.5);
        Assert.Equal(300, mapper.PageHeightPt, 0.5);
        Assert.Equal(2.0, mapper.Zoom);
        Assert.Equal((1067, 800), mapper.PixelSize); // 400*4/3*2=1066.67→1067（AwayFromZero）

        // view ↔ pdf 往返一致
        var view = new Point(100, 60);
        var pdf = mapper.ViewToPdf(view);
        var back = mapper.PdfToView(pdf);
        Assert.Equal(view.X, back.X, 0.001);
        Assert.Equal(view.Y, back.Y, 0.001);

        // 超界裁剪到页内
        var clamped = mapper.ClampToPage(new Rect(-10, -10, 5000, 5000));
        Assert.Equal(new Rect(0, 0, 400, 300), clamped);
    }

    [Fact]
    public async Task RenderPageAsync_InvalidIndex_Throws()
    {
        using var document = new PdfiumPreviewDocument(TestPdfFactory.Create((400, 300)));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = document.RenderPageAsync(-1, 1.0); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = document.RenderPageAsync(1, 1.0); });
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- Dispose / 泄漏

    [Fact]
    public async Task Dispose_ClosesHandle_AndInFlightReturnsToZero()
    {
        var document = new PdfiumPreviewDocument(TestPdfFactory.Create((400, 300)));
        var bitmap = await document.RenderPageAsync(0, 1.0);
        Assert.NotNull(bitmap);
        Assert.Equal(0, document.GetInFlightCount());

        document.Dispose();

        // 句柄已关闭：文档不在服务表中 → 在途归零；后续访问抛 ObjectDisposedException
        Assert.Equal(0, document.GetInFlightCount());
        Assert.Throws<ObjectDisposedException>(() => document.GetPageSize(0));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var document = new PdfiumPreviewDocument(TestPdfFactory.Create((400, 300)));
        document.Dispose();
        document.Dispose(); // 二次不抛
        Assert.Equal(0, document.GetInFlightCount());
    }

    // ---------------------------------------------------------------- 打开语义

    [Fact]
    public void Ctor_NullBytes_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfiumPreviewDocument(null!));
    }

    [Fact]
    public void Ctor_EmptyBytes_Throws()
    {
        Assert.Throws<PdfiumOpenException>(() => new PdfiumPreviewDocument(Array.Empty<byte>()));
    }

    [Fact]
    public void Ctor_InvalidBytes_Throws()
    {
        Assert.Throws<PdfiumOpenException>(() => new PdfiumPreviewDocument("this is not a pdf"u8.ToArray()));
    }

    [Fact]
    public void OpenFile_MissingFile_Throws()
    {
        var missing = Path.Combine(_tempDirectory, "nope.pdf");
        Assert.Throws<PdfiumOpenException>(() => PdfiumPreviewDocument.OpenFile(missing));
    }

    [Fact]
    public void OpenFile_NullOrBlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => PdfiumPreviewDocument.OpenFile(string.Empty));
    }

    [Fact]
    public void OpenFile_SharedRead_AllowsConcurrentReadWriteHolder()
    {
        // FileShare.ReadWrite 契约：预览打开期间，其他句柄仍能以 ReadWrite 持有同一文件
        var path = WriteTempPdf("shared.pdf", TestPdfFactory.Create((612, 792), (400, 300)));

        using var concurrent = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var document = PdfiumPreviewDocument.OpenFile(path);

        Assert.Equal(2, document.PageCount);
        Assert.Equal(612, document.GetPageSize(0).WidthPt, 0.5);
    }

    [Fact]
    public async Task OpenFile_RendersRealFileContent()
    {
        var path = WriteTempPdf("two-pages.pdf", TestPdfFactory.Create((612, 792), (400, 300)));

        using var document = PdfiumPreviewDocument.OpenFile(path);
        var bitmap = await document.RenderPageAsync(1, 1.0);

        Assert.Equal(2, document.PageCount);
        Assert.Equal((533, 400), (bitmap.PixelWidth, bitmap.PixelHeight)); // 400*4/3, 300*4/3
    }

    // ---------------------------------------------------------------- 句柄隔离回归（B2）

    [Fact]
    public async Task HandleIsolation_PreviewDispose_DoesNotCloseReaderHandle()
    {
        var path = WriteTempPdf("isolation.pdf", TestPdfFactory.Create((612, 792), (400, 300)));

        // 模拟“阅读器”实例：先打开并渲染同路径
        using var reader = new PdfiumRenderService();
        var readerDoc = reader.OpenDocument(path);
        var readerBefore = await reader.RenderPageAsync(readerDoc, 0, 96, 100);
        Assert.Equal((816, 1056), (readerBefore.PixelWidth, readerBefore.PixelHeight));

        // 预览用独立实例打开同一路径 → 渲染 → Dispose
        using (var preview = PdfiumPreviewDocument.OpenFile(path))
        {
            Assert.Equal(2, preview.PageCount);
            var previewBitmap = await preview.RenderPageAsync(0, 1.0);
            Assert.True(previewBitmap.IsFrozen);
        }

        // 阅读器句柄未被误关：仍可正常渲染（无 use-after-free/白屏）
        var readerAfter = await reader.RenderPageAsync(readerDoc, 0, 96, 100);
        Assert.Equal(readerBefore.PixelWidth, readerAfter.PixelWidth);
        Assert.Equal(readerBefore.PixelHeight, readerAfter.PixelHeight);
        Assert.Equal(0, reader.GetInFlightCount(readerDoc));
    }

    [Fact]
    public async Task HandleIsolation_TwoPreviewsSamePath_IndependentHandles()
    {
        var path = WriteTempPdf("two-previews.pdf", TestPdfFactory.Create((400, 300)));

        var first = PdfiumPreviewDocument.OpenFile(path);
        var second = PdfiumPreviewDocument.OpenFile(path);
        try
        {
            var firstBitmap = await first.RenderPageAsync(0, 1.0);
            first.Dispose(); // 关闭第一个预览

            // 第二个预览句柄不受影响
            var secondBitmap = await second.RenderPageAsync(0, 1.0);
            Assert.Equal(firstBitmap.PixelWidth, secondBitmap.PixelWidth);
            Assert.Equal(firstBitmap.PixelHeight, secondBitmap.PixelHeight);
        }
        finally
        {
            second.Dispose();
        }
    }

    // ---------------------------------------------------------------- 表单 Widget 绘制（Q5）

    [Fact]
    public async Task FormFillPreview_DrawsWidget()
    {
        using var document = PdfiumPreviewDocument.OpenFile(Corpus.Mixed100);

        var bitmap = await document.RenderPageAsync(0, 1.0);
        var bgra = Bgra(bitmap);
        var yellow = CountYellow(bgra, WidgetRegion);

        Assert.True(yellow > 0, $"预览表单模式应绘制 AcroForm Widget（黄色填充像素），实际 {yellow}");
    }

    [Fact]
    public async Task FormFillPreview_PlainReaderFlags_DoesNotDrawWidget()
    {
        // 对照：阅读器屏幕旗标路径（无表单环境）不绘制 Widget → 证明表单模式只存在于预览入口
        using var reader = new PdfiumRenderService();
        var docId = reader.OpenDocument(Corpus.Mixed100);
        var bitmap = await reader.RenderPageAsync(docId, 0, 96, 100);

        Assert.Equal(0, CountYellow(Bgra(bitmap), WidgetRegion));
    }

    // ---------------------------------------------------------------- 像素工具

    private static (byte[] Pixels, int Width) Bgra(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return (pixels, bitmap.PixelWidth);
    }

    private static int CountYellow((byte[] Pixels, int Width) bmp, (double L, double T, double R, double B) region)
    {
        var left = (int)Math.Round(region.L * 96.0 / 72.0);
        var top = (int)Math.Round(region.T * 96.0 / 72.0);
        var right = (int)Math.Round(region.R * 96.0 / 72.0);
        var bottom = (int)Math.Round(region.B * 96.0 / 72.0);
        var count = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var i = (y * bmp.Width + x) * 4;
            var b = bmp.Pixels[i];
            var g = bmp.Pixels[i + 1];
            var r = bmp.Pixels[i + 2];
            if (r > 230 && g > 230 && b > 180 && b < 248) count++;
        }
        return count;
    }
}
