// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views.Optimizer;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 优化文件组（压缩 / 修复 / OCR，T15）测试：
/// ① VM 参数映射与范围校验 ② OCR 语言列表解析（喂假 JSON）与 helper 缺失优雅禁用
/// ③ 修复工具引擎错误传播 ④ 真引擎集成（压缩 / OCR 合成英文图 / 损坏 PDF）。
/// 页面实例化不做 WPF 可视树断言（Wave 5 约定，同 WorkflowTests）。
/// </summary>
public sealed class OptimizerTests
{
    // ======================= 测试设施 =======================

    /// <summary>防误执行的桥：指向不存在的解释器，只有重写 ExecuteAsync 才会生效。</summary>
    private sealed class FakeBridge : EngineBridge
    {
        public FakeBridge() : base("nonexistent-python", "nonexistent-engine")
        {
        }

        public EngineRequest? LastRequest { get; private set; }

        public EngineResult Result { get; set; } = new()
        {
            Ok = true,
            Outputs = [@"D:\out\AIPDF-20260907-000000-abc123\文档-compress.pdf"],
            OutputDir = @"D:\out\AIPDF-20260907-000000-abc123",
            ExitCode = 0,
        };

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private static IReadOnlyList<Tool> RealCatalog() => ToolCatalog.Load();

    private static Tool ToolById(string id) => RealCatalog().First(tool => tool.Id == id);

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "aipdf-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // ======================= ① 压缩：参数映射与范围校验 =======================

    [Fact]
    public void CompressQualityOptions_MirrorCatalogChoicesAndDefault()
    {
        var tool = ToolById("compress");
        var qualityField = Assert.Single(tool.Fields, field => field.Key == "quality");
        var viewModel = new CompressViewModel(new FakeBridge(), tool);

        // 选项集合 = catalog choices（顺序一致），默认值 = catalog 字段默认值（balanced）。
        Assert.Equal(qualityField.Choices, viewModel.QualityOptions.Select(option => option.Value).ToArray());
        Assert.Contains(viewModel.SelectedQuality.Value, qualityField.Choices);
        Assert.Equal(qualityField.Value, viewModel.SelectedQuality.Value);
    }

    [Fact]
    public void SelectQuality_UnknownValue_ThrowsOutOfRange()
    {
        var viewModel = new CompressViewModel(new FakeBridge(), ToolById("compress"));

        // 范围校验：catalog 之外的档位必须拒绝（对应引擎 compress_doc 只认三档）。
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.SelectQuality("ultra"));
        Assert.Contains("ultra", error.Message);
    }

    [Fact]
    public async Task Execute_MapsQualityAndFilesIntoEngineRequest()
    {
        var bridge = new FakeBridge();
        var viewModel = new CompressViewModel(bridge, ToolById("compress"));
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "文档.pdf");
            await File.WriteAllBytesAsync(source, TestPdfFactory.Create((595, 842)));
            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;
            viewModel.SelectQuality("small");

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            Assert.NotNull(bridge.LastRequest);
            Assert.Equal("compress", bridge.LastRequest.Operation);
            Assert.Equal([source], bridge.LastRequest.Files);
            Assert.Equal(dir, bridge.LastRequest.OutputDir);
            Assert.Equal("small", Assert.IsType<string>(bridge.LastRequest.Options["quality"]));
            Assert.True(viewModel.LastResult!.Ok);
            Assert.False(viewModel.IsRunning);
            Assert.Contains("完成", viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_InvalidOutputDir_ShowsValidationAndSkipsBridge()
    {
        var bridge = new FakeBridge();
        var viewModel = new CompressViewModel(bridge, ToolById("compress"));
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "文档.pdf");
            await File.WriteAllBytesAsync(source, TestPdfFactory.Create((595, 842)));
            viewModel.AddFiles([source]);
            viewModel.OutputDir = Path.Combine(dir, "不存在的目录");

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            // 输出目录无效：拦截在 UI 层，引擎不被调用，无崩溃。
            Assert.Contains("请选择有效的输出文件夹", viewModel.StatusMessage);
            Assert.Null(bridge.LastRequest);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddFiles_FiltersExtensionsAndDuplicates()
    {
        var viewModel = new CompressViewModel(new FakeBridge(), ToolById("compress"));
        var dir = NewTempDir();
        try
        {
            var pdf = Path.Combine(dir, "文档.pdf");
            File.WriteAllBytes(pdf, TestPdfFactory.Create((595, 842)));
            var text = Path.Combine(dir, "说明.txt");
            File.WriteAllText(text, "不是 PDF");
            var missing = Path.Combine(dir, "不存在.pdf");

            viewModel.AddFiles([pdf, text, pdf, missing, ""]);

            // 扩展名过滤 + 去重（与 T14 AddFiles 约定一致）；文件是否存在由引擎校验并报明确错误。
            Assert.Equal(2, viewModel.Files.Count);
            Assert.Equal(pdf, viewModel.Files[0].Path);
            Assert.Equal(missing, viewModel.Files[1].Path);
            Assert.True(viewModel.HasFiles);
            // 首个有效文件的所在目录成为默认输出位置
            Assert.Equal(dir, viewModel.OutputDir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_MissingFile_EngineReportsAndVmSurfaces()
    {
        var bridge = new FakeBridge
        {
            Result = new EngineResult { Ok = false, Error = "文件不存在：不存在.pdf", ExitCode = 1 },
        };
        var viewModel = new CompressViewModel(bridge, ToolById("compress"));
        var dir = NewTempDir();
        try
        {
            viewModel.AddFiles([Path.Combine(dir, "不存在.pdf")]);
            viewModel.OutputDir = dir;

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            Assert.False(viewModel.LastResult!.Ok);
            Assert.Contains("文件不存在", viewModel.StatusMessage);
            Assert.False(viewModel.IsRunning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= ② OCR：语言列表解析与优雅禁用 =======================

    [Fact]
    public void OcrLanguages_Parse_ValidJsonArray_PreservesTagsAndNames()
    {
        const string json = """[{"tag":"zh-Hans-CN","name":"中文(简体，中国)"},{"tag":"en-US","name":"English (United States)"}]""";

        var languages = WindowsOcrLanguages.Parse(json);

        Assert.Equal(2, languages.Count);
        Assert.Equal("zh-Hans-CN", languages[0].Tag);
        Assert.Equal("中文(简体，中国)", languages[0].DisplayName);
        Assert.Equal("en-US", languages[1].Tag);
        Assert.Equal("English (United States)", languages[1].DisplayName);
    }

    [Fact]
    public void OcrLanguages_Parse_MissingName_FallsBackToTag()
    {
        var languages = WindowsOcrLanguages.Parse("""[{"tag":"fr-FR"}]""");

        var language = Assert.Single(languages);
        Assert.Equal("fr-FR", language.Tag);
        Assert.Equal("fr-FR", language.DisplayName);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"tag\":\"en-US\"}")]
    [InlineData("")]
    public void OcrLanguages_Parse_InvalidJson_Throws(string json)
    {
        // 非 JSON / 非数组：解析失败由调用方转为禁用 + 提示，这里断言异常类型稳定。
        Assert.ThrowsAny<Exception>(() => WindowsOcrLanguages.Parse(json));
    }

    [Fact]
    public void ResolveHelperPath_MatchesBuiltHelperOrNone()
    {
        var helper = WindowsOcrLanguages.ResolveHelperPath();

        // helper 已构建（T8）：路径存在且指向 WindowsOcrHelper.exe；未构建时必须为 null（优雅降级的前提）。
        if (helper is not null)
        {
            Assert.True(File.Exists(helper), $"解析出的 helper 不存在：{helper}");
            Assert.EndsWith("WindowsOcrHelper.exe", helper, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task OcrLanguages_FakeLoader_LoadsSelectsAndEnables()
    {
        var viewModel = new OcrViewModel(new FakeBridge(), ToolById("ocr"), _ => Task.FromResult<IReadOnlyList<OcrLanguageOption>>(
        [
            new OcrLanguageOption("zh-Hans-CN", "中文(简体，中国)"),
            new OcrLanguageOption("en-US", "English (United States)"),
        ]));

        await viewModel.LoadLanguagesOnceAsync();

        Assert.Equal(2, viewModel.Languages.Count);
        Assert.True(viewModel.IsLanguageListEnabled);
        Assert.Equal("zh-Hans-CN", viewModel.SelectedLanguage!.Tag);
        Assert.Contains("2", viewModel.LanguageHint);
    }

    [Fact]
    public async Task OcrLanguages_LoadedOnce_RepeatCallDoesNotReload()
    {
        var calls = 0;
        var viewModel = new OcrViewModel(new FakeBridge(), ToolById("ocr"), _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<OcrLanguageOption>>([new OcrLanguageOption("en-US", "English (United States)")]);
        });

        await viewModel.LoadLanguagesOnceAsync();
        await viewModel.LoadLanguagesOnceAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OcrLanguages_MissingHelper_DisablesGracefullyWithHint()
    {
        var viewModel = new OcrViewModel(new FakeBridge(), ToolById("ocr"),
            _ => throw new InvalidOperationException("OCR helper 未构建：找不到 WindowsOcrHelper.exe，请先构建 WindowsOcrHelper 项目。"));

        await viewModel.LoadLanguagesOnceAsync();

        // helper 缺失：下拉禁用、原因可见、不崩溃、不静默。
        Assert.False(viewModel.IsLanguageListEnabled);
        Assert.Empty(viewModel.Languages);
        Assert.Null(viewModel.SelectedLanguage);
        Assert.Contains("OCR helper", viewModel.LanguageHint);
    }

    [Fact]
    public async Task OcrLanguages_EmptyList_ShowsLanguagePackHint()
    {
        var viewModel = new OcrViewModel(new FakeBridge(), ToolById("ocr"),
            _ => Task.FromResult<IReadOnlyList<OcrLanguageOption>>([]));

        await viewModel.LoadLanguagesOnceAsync();

        Assert.False(viewModel.IsLanguageListEnabled);
        Assert.Contains("语言包", viewModel.LanguageHint);
    }

    [Fact]
    public async Task Execute_MapsPagesForceAndLanguage()
    {
        var bridge = new FakeBridge();
        var viewModel = new OcrViewModel(bridge, ToolById("ocr"), _ => Task.FromResult<IReadOnlyList<OcrLanguageOption>>(
        [
            new OcrLanguageOption("zh-Hans-CN", "中文(简体，中国)"),
            new OcrLanguageOption("en-US", "English (United States)"),
        ]));
        await viewModel.LoadLanguagesOnceAsync();
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "扫描件.pdf");
            await File.WriteAllBytesAsync(source, TestPdfFactory.Create((595, 842)));
            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;
            viewModel.PagesText = "1-2";
            viewModel.Force = true;
            viewModel.SelectedLanguage = viewModel.Languages[1];

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            Assert.NotNull(bridge.LastRequest);
            Assert.Equal("ocr", bridge.LastRequest.Operation);
            Assert.Equal("1-2", Assert.IsType<string>(bridge.LastRequest.Options["pages"]));
            Assert.Equal("true", Assert.IsType<string>(bridge.LastRequest.Options["force"]));
            Assert.Equal("en-US", Assert.IsType<string>(bridge.LastRequest.Options["language"]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_InvalidPagesSpec_ShowsValidationAndSkipsBridge()
    {
        var bridge = new FakeBridge();
        var viewModel = new OcrViewModel(bridge, ToolById("ocr"));
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "扫描件.pdf");
            await File.WriteAllBytesAsync(source, TestPdfFactory.Create((595, 842)));
            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;
            viewModel.PagesText = "abc";

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            Assert.Contains("页面格式不正确", viewModel.StatusMessage);
            Assert.Null(bridge.LastRequest);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= ③ 修复：引擎错误传播 =======================

    [Fact]
    public async Task Repair_EngineError_SurfacesMessageWithoutCrash()
    {
        var bridge = new FakeBridge
        {
            Result = new EngineResult { Ok = false, Error = "无法读取 broken.pdf。文件可能损坏或格式不受支持。", ExitCode = 1 },
        };
        var viewModel = new RepairViewModel(bridge, ToolById("repair"));
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "broken.pdf");
            await File.WriteAllBytesAsync(source, [0x25, 0x50, 0x44, 0x46]); // 伪 PDF，桥被替换不真正打开
            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            // 引擎错误（ok=false）按原文进入状态展示；VM 不抛异常、状态复位。
            Assert.False(viewModel.LastResult!.Ok);
            Assert.Contains("无法读取", viewModel.StatusMessage);
            Assert.False(viewModel.IsRunning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= 导航注册（ToolPageRegistry 约定） =======================

    [Fact]
    public void OptimizerPages_SupportedIds_CoverCatalogOptimizerGroup()
    {
        var optimizerIds = RealCatalog()
            .Where(tool => tool.Category == "优化文件")
            .Select(tool => tool.Id)
            .ToArray();

        // 优化文件组的每个工具都必须被本组解析器覆盖（catalog 权威，当前为压缩/修复/OCR）。
        Assert.Equal(optimizerIds, OptimizerToolPages.SupportedToolIds);
    }

    // ======================= ④ 真引擎集成 =======================

    private static EngineBridge CreateRealBridge()
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        var engine = EngineBridge.ResolveDefaultEnginePath();
        Assert.True(File.Exists(python), $"缺少引擎解释器（先建 .venv）：{python}");
        Assert.True(File.Exists(engine), $"缺少引擎脚本：{engine}");
        return new EngineBridge(python, engine);
    }

    /// <summary>真引擎压缩：大尺寸噪声图片 PDF → quality=small → 产物存在且小于原文件。</summary>
    [Fact]
    public async Task Compress_RealEngine_NoiseImage_SmallerOutput()
    {
        var bridge = CreateRealBridge();
        var viewModel = new CompressViewModel(bridge, ToolById("compress"));
        viewModel.SelectQuality("small");
        var dir = NewTempDir();
        try
        {
            // 1400x1400 随机 RGB 图片（不可再压缩的噪声）+ 手工 PDF 封装，压缩后必然显著变小。
            var width = 1400;
            var height = 1400;
            var raw = new byte[width * height * 3];
            new Random(20260907).NextBytes(raw);
            var source = Path.Combine(dir, "大图文档.pdf");
            await File.WriteAllBytesAsync(source, BuildImagePdf(ZlibCompress(raw), width, height, 612, 792, 0));

            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            Assert.True(viewModel.LastResult!.Ok, viewModel.StatusMessage);
            var output = Assert.Single(viewModel.LastResult.Outputs);
            Assert.True(File.Exists(output));
            // 引擎报告 + 磁盘双重验证：产物显著小于输入，且在新任务目录内。
            Assert.True(viewModel.LastResult.OutputBytes < viewModel.LastResult.InputBytes,
                $"压缩结果 {viewModel.LastResult.OutputBytes} 应小于输入 {viewModel.LastResult.InputBytes}");
            Assert.StartsWith(dir, viewModel.LastResult.OutputDir, StringComparison.Ordinal);
            Assert.Contains("AIPDF-", viewModel.LastResult.OutputDir);
            Assert.True(new FileInfo(output).Length < new FileInfo(source).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>真引擎修复 + 损坏 PDF：引擎返回错误 JSON，VM 展示原文，无未处理异常。</summary>
    [Fact]
    public async Task Repair_RealEngine_CorruptPdf_EngineErrorNotCrash()
    {
        var bridge = CreateRealBridge();
        var viewModel = new RepairViewModel(bridge, ToolById("repair"));
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "损坏.pdf");
            await File.WriteAllBytesAsync(source, Encoding.ASCII.GetBytes(
                "this payload is intentionally corrupt and not a pdf structure at all. 0123456789"));

            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            // 优雅错误路径：ok=false + 引擎原文（含「无法读取」），进程与 VM 都不崩溃。
            Assert.False(viewModel.LastResult!.Ok);
            Assert.Contains("无法读取", viewModel.StatusMessage);
            Assert.False(viewModel.IsRunning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>真引擎 OCR：T8 同款合成大号英文文本 → 全链（VM→桥→引擎→WindowsOcrHelper）→ 产物文字层含 TEST。</summary>
    [Fact]
    public async Task Ocr_RealEngine_SyntheticText_ImageOnlyPdf()
    {
        // helper 未构建时这里显式失败（QA 政策：跳过必须给出原因，不静默当作通过）。
        Assert.True(WindowsOcrLanguages.ResolveHelperPath() is not null,
            "OCR helper 未构建（dotnet build Sources\\WindowsOcrHelper），OCR 集成测试无法运行。");

        var bridge = CreateRealBridge();
        var viewModel = new OcrViewModel(bridge, ToolById("ocr"));
        var dir = NewTempDir();
        try
        {
            // T8 同款：白底黑字 Arial 大号 "AIPDF OCR TEST"；渲染为位图后作为整页图片嵌入（无文字层）。
            var (width, height, rgb) = RenderTextImage("AIPDF OCR TEST");
            var displayHeight = 612d * height / width;
            var source = Path.Combine(dir, "扫描图片文档.pdf");
            await File.WriteAllBytesAsync(source, BuildImagePdf(ZlibCompress(rgb), width, height, 612, displayHeight, 792 - displayHeight));

            viewModel.AddFiles([source]);
            viewModel.OutputDir = dir;

            await viewModel.ExecuteCommand.ExecuteAsync(null);

            Assert.True(viewModel.LastResult!.Ok, viewModel.StatusMessage);
            var output = Assert.Single(viewModel.LastResult.Outputs);
            Assert.True(File.Exists(output));
            // OCR 结果写入页面文字层；用引擎同源解释器提取输出文本并断言命中（图像页原本无文字）。
            var text = ExtractPdfTextViaPython(output);
            Assert.Contains("TEST", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= 合成文件辅助 =======================

    /// <summary>用 WPF 渲染白底黑字文本位图，返回 RGB 像素（镜像 T8 的 PIL 生成方式）。</summary>
    private static (int Width, int Height, byte[] Rgb) RenderTextImage(string text)
    {
        const int width = 900;
        const int height = 220;
        byte[]? rgb = null;
        RunSta(() =>
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, width, height));
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    80,
                    Brushes.Black,
                    1.0);
                context.DrawText(formatted, new System.Windows.Point(30, 50));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bgra = new byte[width * height * 4];
            bitmap.CopyPixels(bgra, width * 4, 0);
            rgb = BgraToRgb(bgra);
        });
        return (width, height, rgb!);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                error = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

    private static byte[] BgraToRgb(byte[] bgra)
    {
        var rgb = new byte[bgra.Length / 4 * 3];
        for (int source = 0, target = 0; source < bgra.Length; source += 4, target += 3)
        {
            rgb[target] = bgra[source + 2];
            rgb[target + 1] = bgra[source + 1];
            rgb[target + 2] = bgra[source];
        }

        return rgb;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// 手工构造含 FlateDecode RGB 图片 XObject 的单页 PDF（对象偏移与 xref 逐字节计算）。
    /// 图片按 displayWidthPt × displayHeightPt 绘制于 (0, displayYOffsetPt)。
    /// </summary>
    private static byte[] BuildImagePdf(byte[] zlibRgb, int width, int height, double displayWidthPt, double displayHeightPt, double displayYOffsetPt)
    {
        static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        using var stream = new MemoryStream();
        void WriteText(string value) => stream.Write(Encoding.ASCII.GetBytes(value));

        var offsets = new List<int>();
        void BeginObject(int index)
        {
            offsets.Add((int)stream.Position);
            WriteText($"{index} 0 obj\n");
        }

        WriteText("%PDF-1.4\n");
        BeginObject(1);
        WriteText("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        WriteText("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObject(3);
        WriteText("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        var content = $"q {Fmt(displayWidthPt)} 0 0 {Fmt(displayHeightPt)} 0 {Fmt(displayYOffsetPt)} cm /Im0 Do Q";
        BeginObject(4);
        WriteText($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObject(5);
        WriteText($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {zlibRgb.Length} >>\nstream\n");
        stream.Write(zlibRgb, 0, zlibRgb.Length);
        WriteText("\nendstream\nendobj\n");

        var xrefStart = (int)stream.Position;
        WriteText("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            WriteText($"{offset:D10} 00000 n \n");
        }

        WriteText($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
        return stream.ToArray();
    }

    /// <summary>用引擎同源 venv 解释器提取 PDF 文本（验证 OCR 写入的文字层）。</summary>
    private static string ExtractPdfTextViaPython(string pdfPath)
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        Assert.True(File.Exists(python), $"缺少引擎解释器：{python}");
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("import sys, pymupdf\nfor page in pymupdf.open(sys.argv[1]):\n    sys.stdout.write(page.get_text())\n");
        startInfo.ArgumentList.Add(pdfPath);
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60000), "文本提取超时");
        var text = stdoutTask.Result;
        Assert.True(process.ExitCode == 0, $"文本提取失败（退出码 {process.ExitCode}）：{stderrTask.Result}");
        return text;
    }
}
