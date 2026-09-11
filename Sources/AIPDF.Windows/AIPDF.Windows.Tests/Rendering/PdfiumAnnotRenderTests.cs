// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T1 渲染旗标测试：
//   - FPDF_ANNOT 使 PDF 内嵌注释（Highlight / FreeText）屏上可见（PRD 4.1.3）
//   - BLK-5：FPDF_PRINTING 的打印语义（Print=false 注释不上屏）→ 屏上回退为仅 FPDF_ANNOT
//   - M-10：PyMuPDF FreeText 笔记文字在 PDFium FPDF_ANNOT 下可见（视觉断言，非「其他阅读器可打开」）
//   - 含 Widget PDF 的屏上行为（基线变化记录）
// 语料：tmp/t1-annot-corpus（PyMuPDF 合成）；Widget 复用 tmp/pdfium-spike mixed100 第 1 页。
using System;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace AIPDF.Windows.Tests.Rendering;

public sealed class PdfiumAnnotRenderTests
{
    // 语料区域（PyMuPDF 左上原点 pt，与 gen_annot_corpus.py 一致）
    private static readonly (double L, double T, double R, double B) HighlightRegion = (95, 82, 430, 112);
    private static readonly (double L, double T, double R, double B) FreeTextRegion = (100, 200, 430, 244);
    // mixed100 第 1 页 AcroForm 文本域（gen_corpus.py WIDGET_RECT = 50,700,250,730）
    private static readonly (double L, double T, double R, double B) WidgetRegion = (50, 700, 250, 730);
    // A4 全页（混入检测用）
    private static readonly (double L, double T, double R, double B) FullPage = (0, 0, 595, 842);

    private readonly ITestOutputHelper _output;

    public PdfiumAnnotRenderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EmbeddedHighlight_IsInvisibleWithoutFlag_VisibleWithAnnot()
    {
        T1AnnotCorpus.Ensure();
        using var svc = new PdfiumRenderService();
        var doc = svc.OpenDocument(T1AnnotCorpus.PrintTrue);

        var none = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.None));
        var annot = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.Annot));

        var redNone = CountRed(none, HighlightRegion);
        var redAnnot = CountRed(annot, HighlightRegion);
        _output.WriteLine($"Highlight red pixels: flags=None {redNone}, flags=ANNOT {redAnnot}");

        Assert.Equal(0, redNone);
        Assert.True(redAnnot > 100, $"FPDF_ANNOT 应渲染内嵌高亮，实际红像素 {redAnnot}");
    }

    [Fact]
    public async Task PrintFalseHighlight_VisibleOnScreen_HiddenInPrintingMode_Blk5()
    {
        T1AnnotCorpus.Ensure();
        using var svc = new PdfiumRenderService();
        var doc = svc.OpenDocument(T1AnnotCorpus.PrintFalse);

        var annot = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.Annot));
        var printing = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderService.PrintRenderFlags));

        var redAnnot = CountRed(annot, HighlightRegion);
        var redPrinting = CountRed(printing, HighlightRegion);
        _output.WriteLine($"Print=false highlight red pixels: ANNOT {redAnnot}, ANNOT|PRINTING {redPrinting}");

        // FPDF_PRINTING 是打印语义：Print 位为假的注释在打印模式被过滤 → 屏上不可用它。
        Assert.True(redAnnot > 100, $"Print=false 高亮在 FPDF_ANNOT 下应可见，实际 {redAnnot}");
        Assert.Equal(0, redPrinting);

        // 生产屏上旗标不得含 FPDF_PRINTING，且默认渲染（RenderPageAsync）必须显示该注释。
        Assert.Equal(0, PdfiumRenderService.ScreenRenderFlags & PdfiumRenderFlags.Printing);
        var screen = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100));
        Assert.True(CountRed(screen, HighlightRegion) > 100,
            "屏上默认渲染必须显示 Print=false 的内嵌高亮（BLK-5 回退结论）");
    }

    [Fact]
    public async Task PrintTrueHighlight_VisibleInBothScreenAndPrinting()
    {
        T1AnnotCorpus.Ensure();
        using var svc = new PdfiumRenderService();
        var doc = svc.OpenDocument(T1AnnotCorpus.PrintTrue);

        var annot = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.Annot));
        var printing = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderService.PrintRenderFlags));

        Assert.True(CountRed(annot, HighlightRegion) > 100);
        Assert.True(CountRed(printing, HighlightRegion) > 100,
            "Print=true 的高亮在打印语义下也应可见");
    }

    [Fact]
    public async Task FreeTextNote_TextIsVisibleUnderAnnotFlag_M10()
    {
        T1AnnotCorpus.Ensure();
        using var svc = new PdfiumRenderService();
        var doc = svc.OpenDocument(T1AnnotCorpus.FreeText);

        var none = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.None));
        var annot = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.Annot));
        var screen = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100));

        var darkNone = CountDark(none, FreeTextRegion);
        var darkAnnot = CountDark(annot, FreeTextRegion);
        var darkScreen = CountDark(screen, FreeTextRegion);
        _output.WriteLine($"FreeText dark pixels: None {darkNone}, ANNOT {darkAnnot}, screen {darkScreen}");

        Assert.Equal(0, darkNone);
        Assert.True(darkAnnot > 20, $"FreeText 笔记文字在 FPDF_ANNOT 下应可见，实际暗像素 {darkAnnot}");
        Assert.True(darkScreen > 20, "FreeText 笔记文字在屏上默认渲染下应可见");
    }

    [Fact]
    public async Task WidgetPdf_ScreenRendering_RecordsBaselineChange_Blk5()
    {
        using var svc = new PdfiumRenderService();
        var doc = svc.OpenDocument(Corpus.Mixed100);

        var none = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.None));
        var annot = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.Annot));
        var printing = Bgra(await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderService.PrintRenderFlags));

        var yNone = CountYellow(none, WidgetRegion);
        var yAnnot = CountYellow(annot, WidgetRegion);
        var yPrinting = CountYellow(printing, WidgetRegion);
        // 全页亦扫一遍，排除区域映射偏差
        var yNonePage = CountYellow(none, FullPage);
        var yAnnotPage = CountYellow(annot, FullPage);
        var yPrintingPage = CountYellow(printing, FullPage);
        var bounds = YellowBounds(annot);
        _output.WriteLine($"Widget region yellow (None/ANNOT/PRINT): {yNone}/{yAnnot}/{yPrinting}; " +
                          $"full-page: {yNonePage}/{yAnnotPage}/{yPrintingPage}; bounds={bounds}");

        // BLK-5 Widget 结论：plain FPDF_RenderPageBitmap 不绘制 AcroForm Widget（其 AP 由
        // FPDF_FFLDraw / FPDF_RenderPageBitmapWithMatrix 单独承载）——Widget 区域内三种旗标均无 Widget 像素；
        // 全页像素签名在 None/ANNOT/ANNOT|PRINTING 下完全一致，故旗标变更不引入 Widget/注释视觉基线回归。
        Assert.Equal(0, yNone);
        Assert.Equal(0, yAnnot);
        Assert.Equal(0, yPrinting);
        Assert.Equal(yNonePage, yAnnotPage);
        Assert.Equal(yNonePage, yPrintingPage);
    }

    [Fact]
    public async Task NonScreenFlags_DoNotPopulateScreenLruCache()
    {
        T1AnnotCorpus.Ensure();
        using var svc = new PdfiumRenderService();
        var doc = svc.OpenDocument(T1AnnotCorpus.PrintTrue);

        await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderFlags.None);
        await svc.RenderPageAsync(doc, 0, 96, 100, PdfiumRenderService.PrintRenderFlags);

        var stats = svc.GetLruStats(doc);
        Assert.Equal(0, stats.Hits);
        Assert.Equal(0, stats.Misses);
        Assert.Equal(0, stats.EntryCount);

        // 屏幕旗标路径正常缓存
        await svc.RenderPageAsync(doc, 0, 96, 100);
        Assert.Equal(1, svc.GetLruStats(doc).Misses);
    }

    [Fact]
    public void CountAnnotations_CountsNonWidgetAnnotations_ExcludesWidgets()
    {
        T1AnnotCorpus.Ensure();
        using var svc = new PdfiumRenderService();
        // 各语料恰含 1 条非 Widget 注释
        Assert.Equal(1, svc.CountAnnotations(svc.OpenDocument(T1AnnotCorpus.PrintTrue)));
        Assert.Equal(1, svc.CountAnnotations(svc.OpenDocument(T1AnnotCorpus.FreeText)));
        // mixed100 仅含 1 个 AcroForm Widget、无批注 → FR-22 警告不得被表单字段误触发
        Assert.Equal(0, svc.CountAnnotations(svc.OpenDocument(Corpus.Mixed100)));
    }

    // ---------------------------------------------------------------- 像素工具

    private static (byte[] Pixels, int Width) Bgra(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return (pixels, bitmap.PixelWidth);
    }

    private static int Px(double pt) => (int)Math.Round(pt * 96.0 / 72.0);

    private static int CountRed((byte[] Pixels, int Width) bmp, (double L, double T, double R, double B) region)
        => Count(bmp, region, static (b, g, r) => r > 140 && r - g > 50 && r - b > 50);

    private static int CountDark((byte[] Pixels, int Width) bmp, (double L, double T, double R, double B) region)
        => Count(bmp, region, static (b, g, r) => r < 128 && g < 128 && b < 128);

    private static int CountYellow((byte[] Pixels, int Width) bmp, (double L, double T, double R, double B) region)
        => Count(bmp, region, static (b, g, r) => r > 230 && g > 230 && b > 180 && b < 248);

    private static string YellowBounds((byte[] Pixels, int Width) bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, n = 0;
        for (var y = 0; y < bmp.Pixels.Length / (bmp.Width * 4); y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var i = (y * bmp.Width + x) * 4;
                if (bmp.Pixels[i] > 180 && bmp.Pixels[i] < 248 && bmp.Pixels[i + 1] > 230 && bmp.Pixels[i + 2] > 230)
                {
                    n++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return n == 0 ? "(none)" : $"n={n} x[{minX}..{maxX}] y[{minY}..{maxY}]";
    }

    private static int Count((byte[] Pixels, int Width) bmp, (double L, double T, double R, double B) region,
        Func<byte, byte, byte, bool> match)
    {
        var left = Px(region.L);
        var top = Px(region.T);
        var right = Px(region.R);
        var bottom = Px(region.B);
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = (y * bmp.Width + x) * 4;
                if (match(bmp.Pixels[i], bmp.Pixels[i + 1], bmp.Pixels[i + 2])) count++;
            }
        }
        return count;
    }
}
