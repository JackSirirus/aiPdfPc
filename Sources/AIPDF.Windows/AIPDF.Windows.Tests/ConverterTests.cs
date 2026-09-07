// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 转换组（格式转换）测试：URL 校验、各 VM → 引擎 options 映射、缺失依赖的错误传播、
/// 视图模型工厂映射，以及图片→PDF 真引擎集成（2 图 → 1 个 PDF，断言页数 2）。
/// 单元用例全部走 FakeBridge（无进程）；依赖检查用静态接缝注入，不读写真实注册表状态。
/// </summary>
public sealed class ConverterTests
{
    private static readonly IReadOnlyList<Tool> Catalog = ToolCatalog.Load();

    private static Tool Tool(string id) => Catalog.First(t => t.Id == id);

    // ---------- FakeBridge（无进程，记录请求） ----------

    private sealed class FakeBridge : EngineBridge
    {
        public EngineRequest? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public Func<EngineRequest, Task<EngineResult>>? Handler { get; set; }

        public override async Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            CallCount++;
            await Task.Yield();
            if (Handler is not null)
            {
                return await Handler(request);
            }

            return new EngineResult { Ok = true, Outputs = ["out.pdf"], OutputDir = "D:\\out" };
        }
    }

    private static ImagesToPdfViewModel CreateImagesVm(EngineBridge bridge)
        => Assert.IsType<ImagesToPdfViewModel>(ConverterToolPages.CreateViewModel(Tool("images_to_pdf"), bridge)!);

    private static HtmlToPdfViewModel CreateHtmlVm(FakeBridge bridge)
        => Assert.IsType<HtmlToPdfViewModel>(ConverterToolPages.CreateViewModel(Tool("html_to_pdf"), bridge)!);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aipdf-converter-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- ① 视图模型工厂：11 个转换工具逐一映射 ----------

    [Fact]
    public void CreateViewModel_MapsAllElevenConverterTools()
    {
        var ids = new (string Id, Type Type)[]
        {
            ("images_to_pdf", typeof(ImagesToPdfViewModel)),
            ("pdf_to_images", typeof(PdfToImagesViewModel)),
            ("word_to_pdf", typeof(WordToPdfViewModel)),
            ("ppt_to_pdf", typeof(PptToPdfViewModel)),
            ("excel_to_pdf", typeof(ExcelToPdfViewModel)),
            ("pdf_to_word", typeof(PdfToWordViewModel)),
            ("pdf_to_ppt", typeof(PdfToPptViewModel)),
            ("pdf_to_excel", typeof(PdfToExcelViewModel)),
            ("html_to_pdf", typeof(HtmlToPdfViewModel)),
            ("pdfa", typeof(PdfaViewModel)),
            ("markdown", typeof(MarkdownViewModel)),
        };

        Assert.Equal(11, ids.Length);
        foreach (var (id, expectedType) in ids)
        {
            var viewModel = ConverterToolPages.CreateViewModel(Tool(id), null);
            Assert.NotNull(viewModel);
            Assert.IsType(expectedType, viewModel);
        }
    }

    [Fact]
    public void CreateViewModel_NonConverterTool_ReturnsNull()
    {
        Assert.Null(ConverterToolPages.CreateViewModel(Tool("merge"), null));
    }

    // ---------- ② HTML 转 PDF 的 URL 校验（与 engine.dispatch 同一条规则） ----------

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1:8000/index.html?tab=1")]
    public void HtmlToPdf_ValidUrl_AcceptsUrlOnlyMode(string url)
    {
        var viewModel = CreateHtmlVm(new FakeBridge());
        viewModel.UrlText = url;
        viewModel.OutputDir = NewTempDir(); // 输出目录有效是执行的前提之一

        try
        {
            Assert.True(viewModel.HasUsableUrl);
            Assert.Null(viewModel.UrlError);
            Assert.Null(viewModel.ValidationMessage); // 无文件但有合法网址：允许执行
        }
        finally
        {
            Directory.Delete(viewModel.OutputDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("http://exa mple.com")]
    [InlineData("javascript:alert(1)")]
    public void HtmlToPdf_InvalidUrl_ShowsEngineWording(string url)
    {
        var viewModel = CreateHtmlVm(new FakeBridge());
        viewModel.UrlText = url;

        Assert.False(viewModel.HasUsableUrl);
        Assert.Equal("网页地址必须以 https:// 或 http:// 开头。", viewModel.UrlError);
        Assert.Equal(viewModel.UrlError, viewModel.ValidationMessage);
    }

    [Fact]
    public void HtmlToPdf_EmptyInput_RequiresFileOrUrl()
    {
        var viewModel = CreateHtmlVm(new FakeBridge());

        Assert.Equal("请先选择 HTML 文件，或填写网页地址。", viewModel.ValidationMessage);
    }

    [Fact]
    public async Task HtmlToPdf_UrlIsTrimmedInRequestOptions()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateHtmlVm(bridge);
        viewModel.UrlText = "  https://example.com/page  ";
        var tempDir = NewTempDir();
        try
        {
            viewModel.OutputDir = tempDir;

            await viewModel.ExecuteAsync();

            Assert.Equal(1, bridge.CallCount);
            Assert.Equal("https://example.com/page", bridge.LastRequest!.Options["url"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------- ③ 各 VM → 引擎 options 映射（键与值以 backend/catalog.py 为准） ----------

    [Fact]
    public async Task ImagesToPdf_MapsPageSizeOrientationMargin()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateImagesVm(bridge);
        viewModel.PageSize = "Letter";
        viewModel.Orientation = "landscape";
        viewModel.MarginText = "36";
        var tempDir = NewTempDir();
        try
        {
            var image = Path.Combine(tempDir, "a.png");
            File.WriteAllBytes(image, CreateBmp(8, 8, 31, 89, 217));
            viewModel.AddFiles([image]);
            viewModel.OutputDir = tempDir;

            await viewModel.ExecuteAsync();

            Assert.Equal("images_to_pdf", bridge.LastRequest!.Operation);
            Assert.Equal([image], bridge.LastRequest.Files);
            Assert.Equal("Letter", bridge.LastRequest.Options["pageSize"]);
            Assert.Equal("landscape", bridge.LastRequest.Options["orientation"]);
            Assert.Equal("36", bridge.LastRequest.Options["margin"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    public async Task ImagesToPdf_InvalidMargin_BlocksAndDoesNotCallEngine(string margin)
    {
        var bridge = new FakeBridge();
        var viewModel = CreateImagesVm(bridge);
        viewModel.MarginText = margin;
        var tempDir = NewTempDir();
        try
        {
            var image = Path.Combine(tempDir, "a.png");
            File.WriteAllBytes(image, CreateBmp(8, 8, 0, 0, 0));
            viewModel.AddFiles([image]);
            viewModel.OutputDir = tempDir;

            Assert.Contains("页边距", viewModel.ValidationMessage);

            await viewModel.ExecuteAsync();

            Assert.Contains("页边距", viewModel.StatusMessage);
            Assert.Equal(0, bridge.CallCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PdfToImages_MapsPagesModeFormatDpi()
    {
        var bridge = new FakeBridge();
        var viewModel = Assert.IsType<PdfToImagesViewModel>(ConverterToolPages.CreateViewModel(Tool("pdf_to_images"), bridge)!);
        viewModel.PagesText = "1-3";
        viewModel.Mode = "embedded";
        viewModel.Format = "png";
        viewModel.DpiText = "300";
        var tempDir = NewTempDir();
        try
        {
            var pdf = Path.Combine(tempDir, "a.pdf");
            File.WriteAllBytes(pdf, TestPdfFactory.Create());
            viewModel.AddFiles([pdf]);
            viewModel.OutputDir = tempDir;

            await viewModel.ExecuteAsync();

            Assert.Equal("pdf_to_images", bridge.LastRequest!.Operation);
            Assert.Equal("1-3", bridge.LastRequest.Options["pages"]);
            Assert.Equal("embedded", bridge.LastRequest.Options["mode"]);
            Assert.Equal("png", bridge.LastRequest.Options["format"]);
            Assert.Equal("300", bridge.LastRequest.Options["dpi"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PdfToImages_InvalidPagesSpec_BlocksAndDoesNotCallEngine()
    {
        var bridge = new FakeBridge();
        var viewModel = Assert.IsType<PdfToImagesViewModel>(ConverterToolPages.CreateViewModel(Tool("pdf_to_images"), bridge)!);
        viewModel.PagesText = "abc";
        var tempDir = NewTempDir();
        try
        {
            var pdf = Path.Combine(tempDir, "a.pdf");
            File.WriteAllBytes(pdf, TestPdfFactory.Create());
            viewModel.AddFiles([pdf]);
            viewModel.OutputDir = tempDir;

            Assert.Contains("页码格式不正确", viewModel.ValidationMessage);

            await viewModel.ExecuteAsync();

            Assert.Equal(0, bridge.CallCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PdfToWord_MapsMode()
    {
        var bridge = new FakeBridge();
        var viewModel = Assert.IsType<PdfToWordViewModel>(ConverterToolPages.CreateViewModel(Tool("pdf_to_word"), bridge)!);
        viewModel.Mode = "visual";
        var tempDir = NewTempDir();
        try
        {
            var pdf = Path.Combine(tempDir, "a.pdf");
            File.WriteAllBytes(pdf, TestPdfFactory.Create());
            viewModel.AddFiles([pdf]);
            viewModel.OutputDir = tempDir;

            await viewModel.ExecuteAsync();

            Assert.Equal("pdf_to_word", bridge.LastRequest!.Operation);
            Assert.Equal("visual", bridge.LastRequest.Options["mode"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Pdfa_MapsLevel()
    {
        // pdfa 依赖 LibreOffice；本机可能未安装（设计上会拦截执行），选项映射测试注入"已安装"。
        var saved = ConverterDependencyChecks.OfficeLocator;
        ConverterDependencyChecks.OfficeLocator = () => @"C:\fake\soffice.exe";
        try
        {
            var bridge = new FakeBridge();
            var viewModel = Assert.IsType<PdfaViewModel>(ConverterToolPages.CreateViewModel(Tool("pdfa"), bridge)!);
            viewModel.Level = "1b";
            var tempDir = NewTempDir();
            try
            {
                var pdf = Path.Combine(tempDir, "a.pdf");
                File.WriteAllBytes(pdf, TestPdfFactory.Create());
                viewModel.AddFiles([pdf]);
                viewModel.OutputDir = tempDir;

                await viewModel.ExecuteAsync();

                Assert.Equal("pdfa", bridge.LastRequest!.Operation);
                Assert.Equal("1b", bridge.LastRequest.Options["pdfaLevel"]);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            ConverterDependencyChecks.OfficeLocator = saved;
        }
    }

    [Fact]
    public async Task Markdown_MapsPages()
    {
        var bridge = new FakeBridge();
        var viewModel = Assert.IsType<MarkdownViewModel>(ConverterToolPages.CreateViewModel(Tool("markdown"), bridge)!);
        viewModel.PagesText = "2,4";
        var tempDir = NewTempDir();
        try
        {
            var pdf = Path.Combine(tempDir, "a.pdf");
            File.WriteAllBytes(pdf, TestPdfFactory.Create());
            viewModel.AddFiles([pdf]);
            viewModel.OutputDir = tempDir;

            await viewModel.ExecuteAsync();

            Assert.Equal("markdown", bridge.LastRequest!.Operation);
            Assert.Equal("2,4", bridge.LastRequest.Options["pages"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EngineBusinessError_SurfacesInStatusMessage()
    {
        var bridge = new FakeBridge
        {
            Handler = _ => Task.FromResult(new EngineResult { Ok = false, Error = "所选页面没有可提取的嵌入图片。" }),
        };
        var viewModel = Assert.IsType<PdfToImagesViewModel>(ConverterToolPages.CreateViewModel(Tool("pdf_to_images"), bridge)!);
        viewModel.Mode = "embedded";
        var tempDir = NewTempDir();
        try
        {
            var pdf = Path.Combine(tempDir, "a.pdf");
            File.WriteAllBytes(pdf, TestPdfFactory.Create());
            viewModel.AddFiles([pdf]);
            viewModel.OutputDir = tempDir;

            await viewModel.ExecuteAsync();

            Assert.Contains("失败：所选页面没有可提取的嵌入图片", viewModel.StatusMessage);
            Assert.NotNull(viewModel.LastResult);
            Assert.False(viewModel.LastResult.Ok);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------- ④ 缺失依赖：UI 明确提示且不触达引擎 ----------

    [Fact]
    public async Task OfficeMissing_BlocksExecutionWithClearMessage()
    {
        var saved = ConverterDependencyChecks.OfficeLocator;
        try
        {
            ConverterDependencyChecks.OfficeLocator = () => null;
            var bridge = new FakeBridge();
            var viewModel = Assert.IsType<WordToPdfViewModel>(ConverterToolPages.CreateViewModel(Tool("word_to_pdf"), bridge)!);
            var tempDir = NewTempDir();
            try
            {
                var docx = Path.Combine(tempDir, "a.docx");
                File.WriteAllBytes(docx, [0x50, 0x4B]); // 依赖检查发生在读文件之前，占位字节即可
                viewModel.AddFiles([docx]);
                viewModel.OutputDir = tempDir;

                Assert.Equal(ConverterDependencyChecks.OfficeMissingMessage, viewModel.DependencyWarning);
                Assert.Equal(ConverterDependencyChecks.OfficeMissingMessage, viewModel.ValidationMessage);
                Assert.Contains("LibreOffice", viewModel.DependencyWarning);
                Assert.False(viewModel.ExecuteCommand.CanExecute(null));

                await viewModel.ExecuteAsync();

                Assert.Contains("LibreOffice", viewModel.StatusMessage);
                Assert.Equal(0, bridge.CallCount);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            ConverterDependencyChecks.OfficeLocator = saved;
        }
    }

    [Fact]
    public async Task OfficePresent_ExecutionProceeds()
    {
        var saved = ConverterDependencyChecks.OfficeLocator;
        try
        {
            ConverterDependencyChecks.OfficeLocator = () => @"C:\fake\soffice.exe";
            var bridge = new FakeBridge();
            var viewModel = Assert.IsType<WordToPdfViewModel>(ConverterToolPages.CreateViewModel(Tool("word_to_pdf"), bridge)!);
            var tempDir = NewTempDir();
            try
            {
                var docx = Path.Combine(tempDir, "a.docx");
                File.WriteAllBytes(docx, [0x50, 0x4B]);
                viewModel.AddFiles([docx]);
                viewModel.OutputDir = tempDir;

                Assert.Null(viewModel.DependencyWarning);
                Assert.Null(viewModel.ValidationMessage);

                await viewModel.ExecuteAsync();

                Assert.Equal(1, bridge.CallCount);
                Assert.Equal("word_to_pdf", bridge.LastRequest!.Operation);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            ConverterDependencyChecks.OfficeLocator = saved;
        }
    }

    [Fact]
    public async Task ChromiumMissing_BlocksUrlToPdfWithClearMessage()
    {
        var saved = ConverterDependencyChecks.ChromiumLocator;
        try
        {
            ConverterDependencyChecks.ChromiumLocator = () => null;
            var bridge = new FakeBridge();
            var viewModel = CreateHtmlVm(bridge);
            viewModel.UrlText = "https://example.com";
            var tempDir = NewTempDir();
            try
            {
                viewModel.OutputDir = tempDir;

                Assert.Equal(ConverterDependencyChecks.ChromiumMissingMessage, viewModel.DependencyWarning);
                Assert.Contains("playwright install chromium", viewModel.DependencyWarning);

                await viewModel.ExecuteAsync();

                Assert.Contains("Chromium", viewModel.StatusMessage);
                Assert.Equal(0, bridge.CallCount);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            ConverterDependencyChecks.ChromiumLocator = saved;
        }
    }

    [Fact]
    public void LocateChromium_UsesOverrideDirectory()
    {
        var savedEnvironment = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        var tempDir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "chromium-1234"));
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", tempDir);

            Assert.NotNull(ConverterDependencyChecks.LocateChromium());

            // 空目录 → 未检测到
            var emptyDir = NewTempDir();
            try
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", emptyDir);
                Assert.Null(ConverterDependencyChecks.LocateChromium());
            }
            finally
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", savedEnvironment);
                Directory.Delete(emptyDir, recursive: true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", savedEnvironment);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MissingInputFile_BlocksExecutionBeforeEngine()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateImagesVm(bridge);
        var tempDir = NewTempDir();
        try
        {
            viewModel.OutputDir = tempDir;

            Assert.Equal("请先选择文件。", viewModel.ValidationMessage);

            await viewModel.ExecuteAsync();

            Assert.Equal(0, bridge.CallCount);
            Assert.Contains("请先选择文件", viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidOutputDir_BlocksExecutionBeforeEngine()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateImagesVm(bridge);
        var tempDir = NewTempDir();
        try
        {
            var image = Path.Combine(tempDir, "a.png");
            File.WriteAllBytes(image, CreateBmp(4, 4, 1, 2, 3));
            viewModel.AddFiles([image]);
            viewModel.OutputDir = Path.Combine(tempDir, "不存在的输出目录");

            Assert.Contains("请选择有效的输出文件夹", viewModel.ValidationMessage);

            await viewModel.ExecuteAsync();

            Assert.Equal(0, bridge.CallCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AddFiles_FiltersByExtensionAndDeduplicates()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateImagesVm(bridge);
        var tempDir = NewTempDir();
        try
        {
            var image = Path.Combine(tempDir, "a.png");
            File.WriteAllBytes(image, CreateBmp(4, 4, 1, 2, 3));
            var document = Path.Combine(tempDir, "b.pdf");
            File.WriteAllBytes(document, TestPdfFactory.Create());

            viewModel.AddFiles([image, document, image]);

            Assert.Single(viewModel.Files);
            Assert.Equal(image, viewModel.Files[0].Path); // PDF 不在 images_to_pdf 扩展名内；重复路径被忽略
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------- ⑤ 真引擎集成：图片→PDF，2 图 → 1 个 PDF（页数 = 2） ----------

    [Fact]
    public async Task ImagesToPdf_RealEngine_TwoImages_ProducesTwoPagePdf()
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        var engine = EngineBridge.ResolveDefaultEnginePath();
        Assert.True(File.Exists(python), $"缺少引擎解释器（先建 .venv）：{python}");
        Assert.True(File.Exists(engine), $"缺少引擎脚本：{engine}");

        var bridge = new EngineBridge(python, engine);
        var viewModel = CreateImagesVm(bridge);
        var tempDir = NewTempDir();
        try
        {
            var outParent = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outParent);
            var imageA = Path.Combine(tempDir, "img-a.bmp");
            var imageB = Path.Combine(tempDir, "img-b.bmp");
            File.WriteAllBytes(imageA, CreateBmp(8, 8, 49, 89, 217));
            File.WriteAllBytes(imageB, CreateBmp(12, 12, 250, 128, 10));

            viewModel.AddFiles([imageA, imageB]);
            Assert.Equal(2, viewModel.Files.Count);
            viewModel.OutputDir = outParent;
            Assert.Null(viewModel.ValidationMessage);

            await viewModel.ExecuteAsync();

            Assert.True(viewModel.LastResult!.Ok, viewModel.StatusMessage);
            var output = Assert.Single(viewModel.LastResult.Outputs);
            Assert.EndsWith(".pdf", output, StringComparison.OrdinalIgnoreCase);

            // 引擎在输出父目录下新建 AIPDF-日期-编号 任务子目录，不覆盖源文件
            Assert.StartsWith(outParent, output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("AIPDF-", output);

            using var document = PdfPreviewDocument.OpenFile(output);
            Assert.Equal(2, document.PageCount);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // 引擎子进程偶尔尚未释放句柄：交由系统临时目录清理
            }
        }
    }

    // ---------- 辅助：手工构造 24 位 BMP（PIL 可读；测试不需要图形栈） ----------

    private static byte[] CreateBmp(int width, int height, byte blue, byte green, byte red)
    {
        var rowSize = (width * 3 + 3) / 4 * 4;
        var pixelBytes = rowSize * height;
        var bytes = new byte[54 + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        WriteInt32(bytes, 2, bytes.Length);
        WriteInt32(bytes, 10, 54);
        WriteInt32(bytes, 14, 40);
        WriteInt32(bytes, 18, width);
        WriteInt32(bytes, 22, height);
        bytes[26] = 1; // planes
        bytes[28] = 24; // bpp
        WriteInt32(bytes, 34, pixelBytes);
        WriteInt32(bytes, 38, 2835);
        WriteInt32(bytes, 42, 2835);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = 54 + (y * rowSize) + (x * 3);
                bytes[offset] = blue;
                bytes[offset + 1] = green;
                bytes[offset + 2] = red;
            }
        }

        return bytes;
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
}
