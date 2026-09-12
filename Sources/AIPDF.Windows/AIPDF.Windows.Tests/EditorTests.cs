// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views.Editor;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>编辑组测试共用辅助：工具条目、空执行回调、目录「编辑内容」组 id。</summary>
internal static class EditorTestHost
{
    /// <summary>无副作用执行回调（VM 单元测试不跑引擎）。</summary>
    public static readonly ExecuteEditorToolDelegate Noop = (_, _, _, _) => Task.CompletedTask;

    /// <summary>按 id 取目录条目（catalog.json 随测试输出目录提供）。</summary>
    public static Tool Tool(string id) =>
        ToolCatalog.Load().First(tool => tool.Id == id);

    /// <summary>目录「编辑内容」组的工具 id（组名与组序来自数据，不在代码写死）。</summary>
    public static IReadOnlyList<string> CatalogEditorIds()
    {
        var group = ToolCatalog.GroupByCategory(ToolCatalog.Load()).First(g => g.Name == "编辑内容");
        return [.. group.Tools.Select(tool => tool.Id)];
    }
}

/// <summary>编辑组视图模型的参数映射：默认值与可选值照 catalog，键形状与 backend/engine.py 读取方式一致。</summary>
public sealed class EditorViewModelMappingTests
{
    [Fact]
    public void Rotate_DefaultsAndChoicesMatchCatalog()
    {
        var viewModel = new RotateViewModel(EditorTestHost.Tool("rotate"), EditorTestHost.Noop);

        Assert.Equal("90", viewModel.Angle);
        Assert.Equal(["90", "180", "270"], viewModel.Angles);
        Assert.Equal("", viewModel.Pages);

        viewModel.Angle = "270";
        viewModel.Pages = "2-4";
        var options = viewModel.BuildOptions();
        Assert.Equal("2-4", options["pages"]);
        Assert.Equal("270", options["angle"]);
        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void PageNumbers_DefaultsMatchCatalogAndMapsAllFields()
    {
        var viewModel = new PageNumbersViewModel(EditorTestHost.Tool("numbers"), EditorTestHost.Noop);

        Assert.Equal("{n} / {total}", viewModel.Format);
        Assert.Equal("1", viewModel.Start);
        Assert.Equal("bottom-center", viewModel.Position);
        Assert.Equal("11", viewModel.Fontsize);
        Assert.Equal(
            ["top-left", "top-center", "top-right", "center", "bottom-left", "bottom-center", "bottom-right"],
            viewModel.Positions);

        var options = viewModel.BuildOptions();
        Assert.Equal(
            ["fontsize", "format", "pages", "position", "start"], // 按序（Ordinal）排序后的完整键集
            [.. options.Keys.OrderBy(key => key, StringComparer.Ordinal)]);
        Assert.Equal("bottom-center", options["position"]);
        Assert.Equal("11", options["fontsize"]);
    }

    [Fact]
    public void Watermark_DefaultsMatchCatalog_ImageOmittedWhenEmpty()
    {
        var viewModel = new WatermarkViewModel(EditorTestHost.Tool("watermark"), EditorTestHost.Noop);

        Assert.Equal("内部资料", viewModel.Text);
        Assert.Equal("bottom-center", viewModel.Position);
        Assert.Equal("0.2", viewModel.Opacity);
        Assert.Equal("42", viewModel.Fontsize);

        var options = viewModel.BuildOptions();
        Assert.Equal("内部资料", options["text"]);
        Assert.Equal("0.2", options["opacity"]);
        Assert.False(options.ContainsKey("image"), "未选择图片时不得发送 image 键（引擎以真值判断走图片水印）");

        viewModel.ImagePath = @"D:\图\印章.png";
        Assert.Equal(@"D:\图\印章.png", viewModel.BuildOptions()["image"]);
    }

    [Fact]
    public void Watermark_EmptyTextAndImage_FailsValidation()
    {
        var viewModel = new WatermarkViewModel(EditorTestHost.Tool("watermark"), EditorTestHost.Noop)
        {
            Text = "",
        };

        Assert.Contains("水印文字", ValidateText(viewModel));
    }

    [Fact]
    public void Crop_RectDefaultsMatchCatalog()
    {
        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);

        Assert.Equal(48, viewModel.X, 2);
        Assert.Equal(72, viewModel.Y, 2);
        Assert.Equal(240, viewModel.Width, 2);
        Assert.Equal(64, viewModel.Height, 2);
        Assert.False(viewModel.HasSelection);
        Assert.Equal("", viewModel.SelectionSummary);

        var options = viewModel.BuildOptions();
        Assert.Equal("48", options["x"]);
        Assert.Equal("72", options["y"]);
        Assert.Equal("240", options["width"]);
        Assert.Equal("64", options["height"]);
    }

    [Fact]
    public void Edit_DefaultsMatchCatalogAndMapsRect()
    {
        var viewModel = new EditViewModel(EditorTestHost.Tool("edit"), EditorTestHost.Noop);

        Assert.Equal("text", viewModel.Mode);
        Assert.Equal("18", viewModel.Fontsize);
        Assert.Equal("#3159D9", viewModel.Color);
        Assert.Equal(["text", "image", "rectangle", "highlight", "ink"], viewModel.Modes);

        var options = viewModel.BuildOptions();
        Assert.Equal("text", options["mode"]);
        Assert.Equal("#3159D9", options["color"]);
        Assert.Equal("48", options["x"]);
        Assert.Equal("240", options["width"]);
        Assert.False(options.ContainsKey("image"));
    }

    [Fact]
    public void Edit_TextModeRequiresText_ImageModeRequiresImage()
    {
        var viewModel = new EditViewModel(EditorTestHost.Tool("edit"), EditorTestHost.Noop) { Text = "" };
        Assert.Equal("请输入文字内容。", ValidateText(viewModel));

        viewModel.Mode = "image";
        Assert.Equal("请选择要添加的图片。", ValidateText(viewModel));

        viewModel.ImagePath = @"D:\图\签名.png";
        Assert.Null(ValidateText(viewModel));
    }

    [Fact]
    public void Forms_FillModeBuildsFormValues()
    {
        var viewModel = new FormsViewModel(EditorTestHost.Tool("forms"), EditorTestHost.Noop)
        {
            FormMode = "fill",
            FieldName = "姓名",
            FieldValue = "张三",
        };

        Assert.Equal(["fill", "create"], viewModel.FormModes);
        Assert.Equal(["text", "checkbox", "list", "radio"], viewModel.FieldTypes);

        var options = viewModel.BuildOptions();
        Assert.Equal("fill", options["formMode"]);
        var formValues = Assert.IsType<Dictionary<string, object?>>(options["formValues"]);
        Assert.Equal("张三", formValues["姓名"]);
        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void Forms_CreateModeMapsFieldDefinitionAndRect()
    {
        var viewModel = new FormsViewModel(EditorTestHost.Tool("forms"), EditorTestHost.Noop)
        {
            FormMode = "create",
            FieldName = "部门",
            FieldType = "list",
            FieldValue = "选项一",
            Choices = "选项一\n选项二",
            Pages = "1",
        };

        var options = viewModel.BuildOptions();
        Assert.Equal("create", options["formMode"]);
        Assert.Equal("部门", options["fieldName"]);
        Assert.Equal("list", options["fieldType"]);
        Assert.Equal("选项一\n选项二", options["choices"]);
        Assert.Equal("48", options["x"]);
        Assert.Null(ValidateText(viewModel));
    }

    [Fact]
    public void Forms_CreateOptionCountValidation()
    {
        var viewModel = new FormsViewModel(EditorTestHost.Tool("forms"), EditorTestHost.Noop)
        {
            FormMode = "create",
            FieldName = "性别",
            FieldType = "radio",
            Choices = "男",
        };
        Assert.Equal("单选组至少需要两个选项。", ValidateText(viewModel));

        viewModel.Choices = "男\n女";
        Assert.Null(ValidateText(viewModel));
    }

    /// <summary>参数级预检（不依赖文件系统）：构造合法文件/目录后调用 Validate。</summary>
    private static string? ValidateText(EditorToolViewModel viewModel)
    {
        using var workspace = new TempDirectory();
        var file = Path.Combine(workspace.Path, "样例.pdf");
        File.WriteAllBytes(file, [0x25, 0x50, 0x44, 0x46]); // %PDF 头，预检只查存在性
        viewModel.InputFile = file;
        viewModel.OutputDir = workspace.Path;
        return viewModel.Validate();
    }

    /// <summary>测试用临时目录：释放时递归删除。</summary>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 句柄尚未完全释放时容忍残留，由系统临时目录清理兜底
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// 框选坐标 → 裁剪参数映射：预览 mapper（view px → pdf pt）→ 事件参数 → ApplySelection → BuildOptions。
/// 坐标约定见 backend/engine.py area()：可见页、左上原点、pt；不做任何翻转。
/// </summary>
public sealed class CropSelectionMappingTests
{
    [Fact]
    public void ApplySelection_FillsOneBasedPageAndPointRect()
    {
        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);

        viewModel.ApplySelection(new PdfRegionSelectedEventArgs
        {
            PageIndex = 2,
            RegionPt = new Rect(48, 72, 240, 192),
        });

        Assert.Equal("3", viewModel.Pages); // 1 基页码（事件为 0 基）
        Assert.Equal(48, viewModel.X, 2);
        Assert.Equal(72, viewModel.Y, 2);
        Assert.Equal(240, viewModel.Width, 2);
        Assert.Equal(192, viewModel.Height, 2);
        Assert.True(viewModel.HasSelection);
        Assert.Contains("第 3 页", viewModel.SelectionSummary);

        var options = viewModel.BuildOptions();
        Assert.Equal("48", options["x"]);
        Assert.Equal("72", options["y"]);
        Assert.Equal("240", options["width"]);
        Assert.Equal("192", options["height"]);
    }

    [Fact]
    public void PreviewMapperChain_SelectionPixelsBecomeEnginePoints()
    {
        // 612×792 pt 页面、100% 缩放：view px = pt × 4/3（96dpi 渲染）。
        var mapper = new PdfCoordinateMapper { PageWidthPt = 612, PageHeightPt = 792, Zoom = 1.0 };
        Assert.Equal(612 * PdfCoordinateMapper.DpiScale, mapper.ViewWidth, 6);

        var viewRect = new Rect(64, 96, 320, 256); // 拖出的框（px）
        var pdfRect = mapper.ClampToPage(mapper.ViewToPdfRect(viewRect)); // 控件事件前完成换算与裁页

        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);
        viewModel.ApplySelection(new PdfRegionSelectedEventArgs { PageIndex = 0, RegionPt = pdfRect });

        Assert.Equal(48, viewModel.X, 2); // 64px ÷ 4/3 = 48pt
        Assert.Equal(72, viewModel.Y, 2);
        Assert.Equal(240, viewModel.Width, 2);
        Assert.Equal(192, viewModel.Height, 2);
        Assert.Equal("1", viewModel.Pages);

        var options = viewModel.BuildOptions();
        Assert.Equal("48", options["x"]);
        Assert.Equal("192", options["height"]);
    }

    [Fact]
    public void ApplySelection_NegativeCoordinatesClampedToZero()
    {
        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);

        viewModel.ApplySelection(new PdfRegionSelectedEventArgs
        {
            PageIndex = 0,
            RegionPt = new Rect(-10, 0, 100, 80), // 控件已裁页；防御性钳位到 0
        });

        Assert.Equal(0, viewModel.X, 2);
        Assert.Equal(100, viewModel.Width, 2);
    }

    [Fact]
    public void ClearSelection_RestoresCatalogDefaultsKeepsPages()
    {
        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);
        viewModel.ApplySelection(new PdfRegionSelectedEventArgs { PageIndex = 1, RegionPt = new Rect(10, 20, 30, 40) });
        Assert.True(viewModel.HasSelection);

        viewModel.ClearSelection();

        Assert.False(viewModel.HasSelection);
        Assert.Equal("", viewModel.SelectionSummary);
        Assert.Equal(48, viewModel.X, 2);
        Assert.Equal(240, viewModel.Width, 2);
        Assert.Equal("2", viewModel.Pages); // 页面范围保留用户输入
    }

    [Fact]
    public void ApplySelection_NullArgs_Throws()
    {
        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);
        Assert.Throws<ArgumentNullException>(() => viewModel.ApplySelection(null!));
    }
}

/// <summary>
/// 中文水印参数往返（乱码防护）：WatermarkViewModel.BuildOptions → 引擎桥请求 JSON
/// （UTF-8 无 BOM，与 T12 桥的 stdin 契约一致）→ 解析回读，文本逐字无损。
/// </summary>
public sealed class ChineseWatermarkRoundTripTests
{
    [Fact]
    public void ChineseOptions_SerializeToEngineJson_PreservesText()
    {
        var expected = "机密文件·内部资料";
        var viewModel = new WatermarkViewModel(EditorTestHost.Tool("watermark"), EditorTestHost.Noop) { Text = expected };
        var request = new EngineRequest
        {
            Operation = "watermark",
            Files = [@"D:\样例\机密文件.pdf"],
            Options = viewModel.BuildOptions(),
            OutputDir = @"D:\输出",
        };

        // 桥用 UTF-8（无 BOM）序列化后写 stdin；这里复刻 EngineBridge.SerializeRequest 的
        // 精确线上形状（小写键字典，而非对象直序列化的 PascalCase 属性名）。
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["operation"] = request.Operation,
            ["files"] = request.Files,
            ["options"] = request.Options,
            ["outputDir"] = request.OutputDir,
        });
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

        Assert.NotEqual(0xEF, bytes[0]); // T12 教训：BOM 前导会让引擎 json.load 读到 U+FEFF
        var decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.DoesNotContain("\uFFFD", decoded);

        using var document = JsonDocument.Parse(bytes);
        var text = document.RootElement.GetProperty("options").GetProperty("text").GetString();
        Assert.Equal(expected, text);
        Assert.Equal(@"D:\样例\机密文件.pdf", document.RootElement.GetProperty("files")[0].GetString());
    }

    [Fact]
    public void EditChineseText_RoundTripsThroughJson()
    {
        var viewModel = new EditViewModel(EditorTestHost.Tool("edit"), EditorTestHost.Noop) { Text = "审阅意见：请修正第 2 页的标题" };
        var request = new EngineRequest { Operation = "edit", Files = ["文档.pdf"], Options = viewModel.BuildOptions(), OutputDir = "out" };

        // 复刻 EngineBridge.SerializeRequest 的线上形状（小写键字典）。
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["operation"] = request.Operation,
            ["files"] = request.Files,
            ["options"] = request.Options,
            ["outputDir"] = request.OutputDir,
        }));
        using var document = JsonDocument.Parse(bytes);

        Assert.Equal("审阅意见：请修正第 2 页的标题", document.RootElement.GetProperty("options").GetProperty("text").GetString());
    }
}

/// <summary>编辑组导航注册（Wave 5 ToolPageRegistry 约定）。</summary>
public sealed class EditorNavigationTests
{
    [Fact]
    public void SupportedToolIds_MatchCatalogEditorGroup()
    {
        Assert.Equal([.. EditorTestHost.CatalogEditorIds()], [.. EditorPages.SupportedToolIds]);
        Assert.Equal(6, EditorPages.SupportedToolIds.Count);
    }

    [Fact]
    public void Resolve_NonEditorTool_ReturnsNull()
    {
        // 不构建任何 WPF 可视树（测试线程无 Application），只验证解析器的认领边界。
        Assert.Null(EditorPages.Resolve(EditorTestHost.Tool("merge"), null));
        Assert.Null(EditorPages.Resolve(EditorTestHost.Tool("compress"), null));
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        EditorPages.Register();
        EditorPages.Register(); // 重复注册不得抛异常（内部有守卫）
        Assert.Null(EditorPages.Resolve(EditorTestHost.Tool("split"), null));
    }
}

/// <summary>真引擎集成：旋转宽高互换、框选裁剪尺寸、中文水印全链路。依赖仓库 .venv（T3 起）。</summary>
public sealed class EditorEngineIntegrationTests
{
    [Fact]
    public async Task Rotate90_SwapsVisiblePageWidthAndHeight()
    {
        var bridge = CreateRealBridge();
        using var workspace = new TempDirectory();
        var source = Path.Combine(workspace.Path, "旋转集成.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792)));

        var result = await ExecuteAsync(bridge, new EngineRequest
        {
            Operation = "rotate",
            Files = [source],
            Options = new Dictionary<string, object?> { ["angle"] = "90" },
            OutputDir = workspace.Path,
        });

        Assert.True(result.Ok, result.Error ?? "旋转失败");
        var output = Assert.Single(result.Outputs);
        using var document = PdfiumPreviewDocument.OpenFile(output);
        var (width, height) = document.GetPageSize(0);
        // PDFium 报告可见页（旋转后）尺寸：612×792 顺时针 90° → 792×612
        Assert.True(Math.Abs(width - 792) < 0.5, $"宽 {width} 应为 792±0.5");
        Assert.True(Math.Abs(height - 612) < 0.5, $"高 {height} 应为 612±0.5");
    }

    [Fact]
    public async Task CropSelectionChain_ProducesOutputMatchingSelectionSize()
    {
        var bridge = CreateRealBridge();
        using var workspace = new TempDirectory();
        var source = Path.Combine(workspace.Path, "裁剪集成.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792)));

        // 完整链路：框选事件参数 → VM 参数 → 引擎执行。
        var viewModel = new CropViewModel(EditorTestHost.Tool("crop"), EditorTestHost.Noop);
        viewModel.ApplySelection(new PdfRegionSelectedEventArgs { PageIndex = 0, RegionPt = new Rect(100, 100, 200, 150) });

        var result = await ExecuteAsync(bridge, new EngineRequest
        {
            Operation = "crop",
            Files = [source],
            Options = viewModel.BuildOptions(),
            OutputDir = workspace.Path,
        });

        Assert.True(result.Ok, result.Error ?? "裁剪失败");
        var output = Assert.Single(result.Outputs);
        using var document = PdfiumPreviewDocument.OpenFile(output);
        var (width, height) = document.GetPageSize(0);
        Assert.True(Math.Abs(width - 200) <= 1.0, $"宽 {width} 应为框选宽 200±1pt");
        Assert.True(Math.Abs(height - 150) <= 1.0, $"高 {height} 应为框选高 150±1pt");
    }

    [Fact]
    public async Task WatermarkChineseText_SurvivesFullEngineRoundTrip()
    {
        var bridge = CreateRealBridge();
        using var workspace = new TempDirectory();
        var source = Path.Combine(workspace.Path, "机密文件.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792)));

        var viewModel = new WatermarkViewModel(EditorTestHost.Tool("watermark"), EditorTestHost.Noop)
        {
            Text = "机密文件·内部资料",
        };
        var result = await ExecuteAsync(bridge, new EngineRequest
        {
            Operation = "watermark",
            Files = [source],
            Options = viewModel.BuildOptions(),
            OutputDir = workspace.Path,
        });

        Assert.True(result.Ok, result.Error ?? "水印失败");
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);

        // 回读：markdown 导出走引擎 get_text，验证水印中文逐字可提取、无替换符。
        var markdown = await ExecuteAsync(bridge, new EngineRequest
        {
            Operation = "markdown",
            Files = [output],
            Options = [],
            OutputDir = workspace.Path,
        });
        Assert.True(markdown.Ok, markdown.Error ?? "Markdown 导出失败");
        var text = await File.ReadAllTextAsync(Assert.Single(markdown.Outputs), Encoding.UTF8);
        Assert.Contains("机密文件", text);
        Assert.Contains("内部资料", text);
        Assert.DoesNotContain("\uFFFD", text);
    }

    // ---------- 辅助（与 EngineBridgeTests 同款：真引擎 + 临时目录 + 90s 超时） ----------

    private static EngineBridge CreateRealBridge()
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        var engine = EngineBridge.ResolveDefaultEnginePath();
        Assert.True(File.Exists(python), $"缺少引擎解释器（先建 .venv）：{python}");
        Assert.True(File.Exists(engine), $"缺少引擎脚本：{engine}");
        return new EngineBridge(python, engine);
    }

    private static async Task<EngineResult> ExecuteAsync(EngineBridge bridge, EngineRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await bridge.ExecuteAsync(request, timeout.Token);
    }

    /// <summary>测试用临时目录：释放时递归删除。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-editor-int-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 句柄尚未完全释放时容忍残留，由系统临时目录清理兜底
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
