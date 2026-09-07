// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views.PageOrganizer;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 页面整理组（T14）：
/// ① VM 参数映射（表单状态 → 引擎请求逐字段）
/// ② 输入校验（无文件禁用执行、必填项、输出目录、页码格式）
/// ③ 文件列表排序语义（顺序即传参顺序）
/// ④ 目录/注册表对齐（options 键名 = catalog 字段键名，值全为字符串）
/// ⑤ 取消复位（引擎不参与）
/// ⑥ 真桥 → 真引擎集成（合并页数 = 输入之和，pypdf 验证）
/// </summary>
public sealed class PageOrganizerTests
{
    private static string TempPath => System.IO.Path.GetTempPath();

    // ---------- ① VM 参数映射 ----------

    [Fact]
    public async Task Merge_BuildsRequestWithFilesInListOrderAndEmptyOptions()
    {
        var (viewModel, bridge) = Create<MergeViewModel>("merge");
        viewModel.AddFiles([@"C:\in\b.pdf", @"C:\in\a.pdf"]); // 添加顺序即合并顺序
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(bridge.LastRequest);
        var request = bridge.LastRequest!;
        Assert.Equal("merge", request.Operation);
        Assert.Equal([@"C:\in\b.pdf", @"C:\in\a.pdf"], request.Files);
        Assert.Empty(request.Options);
    }

    [Fact]
    public async Task Split_MapsRangesTextToOption()
    {
        var (viewModel, bridge) = Create<SplitViewModel>("split");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.RangesText = "1-3;4-6;7";
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("split", bridge.LastRequest!.Operation);
        Assert.Equal("1-3;4-6;7", (string)bridge.LastRequest.Options["ranges"]!);
    }

    [Fact]
    public async Task Remove_MapsPagesTextToOption()
    {
        var (viewModel, bridge) = Create<RemoveViewModel>("remove");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.PagesText = "2,5";
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("remove", bridge.LastRequest!.Operation);
        Assert.Equal("2,5", (string)bridge.LastRequest.Options["pages"]!);
    }

    [Fact]
    public async Task Extract_MapsPagesTextToOption()
    {
        var (viewModel, bridge) = Create<ExtractViewModel>("extract");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.PagesText = "1-2";
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("extract", bridge.LastRequest!.Operation);
        Assert.Equal("1-2", (string)bridge.LastRequest.Options["pages"]!);
    }

    [Fact]
    public async Task Organize_MapsPagesOrderToOption()
    {
        var (viewModel, bridge) = Create<OrganizeViewModel>("organize");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.PagesText = "3,1,2"; // 顺序敏感
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("organize", bridge.LastRequest!.Operation);
        Assert.Equal("3,1,2", (string)bridge.LastRequest.Options["pages"]!);
    }

    [Fact]
    public async Task Scan_MapsScanModeAndPerspectiveToOptions()
    {
        var (viewModel, bridge) = Create<ScanViewModel>("scan");
        viewModel.AddFiles([@"C:\in\page.jpg"]);
        viewModel.ScanMode = "gray";
        viewModel.Perspective = false;
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("scan", bridge.LastRequest!.Operation);
        Assert.Equal("gray", (string)bridge.LastRequest.Options["scanMode"]!);
        Assert.Equal("false", (string)bridge.LastRequest.Options["perspective"]!);
    }

    [Fact]
    public void Scan_DefaultsComeFromCatalog()
    {
        var (viewModel, _) = Create<ScanViewModel>("scan");

        Assert.Equal("document", viewModel.ScanMode);
        Assert.True(viewModel.Perspective);
        Assert.Equal(["document", "gray", "original"], viewModel.ScanModeChoices);
    }

    // ---------- ② 输入校验 ----------

    [Fact]
    public async Task Execute_WithoutFiles_DisabledAndBlocked()
    {
        var (viewModel, bridge) = Create<MergeViewModel>("merge");
        viewModel.OutputDir = TempPath;

        Assert.False(viewModel.ExecuteCommand.CanExecute(null));

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("请先选择文件", viewModel.StatusMessage);
        Assert.Null(bridge.LastRequest);
    }

    [Fact]
    public async Task Merge_WithSingleFile_BlockedWithEngineAlignedMessage()
    {
        var (viewModel, bridge) = Create<MergeViewModel>("merge");
        viewModel.AddFiles([@"C:\in\a.pdf"]); // OutputDir 自动填 C:\in（不存在），输出目录校验在数量校验之后

        Assert.False(viewModel.ExecuteCommand.CanExecute(null)); // 合并至少两份才可执行

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("请至少添加两份 PDF", viewModel.StatusMessage);
        Assert.Null(bridge.LastRequest);
    }

    [Fact]
    public async Task Remove_WithoutPages_BlockedWithEngineAlignedMessage()
    {
        var (viewModel, bridge) = Create<RemoveViewModel>("remove");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.OutputDir = TempPath;
        Assert.Equal("", viewModel.PagesText); // catalog 默认为空

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("请输入需要删除的页面", viewModel.StatusMessage);
        Assert.Null(bridge.LastRequest);
    }

    [Fact]
    public async Task Extract_WithBadPagesFormat_Blocked()
    {
        var (viewModel, bridge) = Create<ExtractViewModel>("extract");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.PagesText = "1-3, abc";
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("页码格式不正确", viewModel.StatusMessage);
        Assert.Null(bridge.LastRequest);
    }

    [Fact]
    public async Task Execute_WithMissingOutputDir_Blocked()
    {
        var (viewModel, bridge) = Create<ExtractViewModel>("extract");
        viewModel.AddFiles([@"C:\in\doc.pdf"]);
        viewModel.PagesText = "1";
        viewModel.OutputDir = @"C:\aipdf-does-not-exist-42";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("请选择有效的输出文件夹", viewModel.StatusMessage);
        Assert.Null(bridge.LastRequest);
    }

    // ---------- ③ 文件列表语义 ----------

    [Fact]
    public void Organize_AddSecondFile_ReplacesExisting()
    {
        var (viewModel, _) = Create<OrganizeViewModel>("organize");
        viewModel.AddFiles([@"C:\in\a.pdf"]);
        viewModel.AddFiles([@"C:\in\b.pdf"]);

        var file = Assert.Single(viewModel.Files);
        Assert.Equal(@"C:\in\b.pdf", file.Path);
    }

    [Fact]
    public void AddFiles_FiltersByCatalogExtensionsAndDeduplicates()
    {
        var (viewModel, _) = Create<MergeViewModel>("merge");
        viewModel.AddFiles([@"C:\in\a.txt", @"C:\in\B.PDF", @"C:\in\a.pdf"]);

        Assert.Equal(2, viewModel.Files.Count);
        Assert.Equal(@"C:\in\B.PDF", viewModel.Files[0].Path);
        Assert.Equal(@"C:\in\a.pdf", viewModel.Files[1].Path);
    }

    [Fact]
    public void AddFiles_DefaultsOutputDirToSourceFolderOnce()
    {
        var (viewModel, _) = Create<MergeViewModel>("merge");
        viewModel.AddFiles([@"C:\in\a.pdf", @"C:\elsewhere\b.pdf"]);

        Assert.Equal(@"C:\in", viewModel.OutputDir); // 首个文件旁，后续不覆盖
    }

    [Fact]
    public async Task FileOrder_FollowsMoveCommands_IntoRequestFiles()
    {
        var (viewModel, bridge) = Create<MergeViewModel>("merge");
        viewModel.AddFiles([@"C:\in\a.pdf", @"C:\in\b.pdf", @"C:\in\c.pdf"]);
        viewModel.MoveDownCommand.Execute(viewModel.Files[0]); // [b,a,c]
        viewModel.MoveUpCommand.Execute(viewModel.Files[2]); // [b,c,a]

        viewModel.OutputDir = TempPath;
        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(
            [@"C:\in\b.pdf", @"C:\in\c.pdf", @"C:\in\a.pdf"],
            bridge.LastRequest!.Files);
    }

    // ---------- ⑤ 运行守卫与取消复位 ----------

    [Fact]
    public async Task Execute_IsRunning_BlocksReentryAndCancelResets()
    {
        using var workspace = new TempDirectory();
        var tool = ToolCatalog.Load().First(t => t.Id == "merge");
        var bridge = new RecordingBridge
        {
            // 挂起直到取消：Task.Delay(Infinite, ct) 在取消时抛 OperationCanceledException
            Handler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new EngineResult { Ok = true };
            },
        };
        var viewModel = new MergeViewModel(bridge, tool);
        viewModel.AddFiles([@"C:\in\a.pdf", @"C:\in\b.pdf"]);
        viewModel.OutputDir = workspace.Path;

        var runTask = viewModel.ExecuteCommand.ExecuteAsync(null);
        await WaitUntil(() => viewModel.IsRunning);

        Assert.False(viewModel.ExecuteCommand.CanExecute(null)); // 运行中禁止重入
        Assert.True(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);
        await runTask;

        Assert.False(viewModel.IsRunning);
        Assert.Contains("已取消", viewModel.StatusMessage);
    }

    // ---------- ④ 目录 / 注册表对齐 ----------

    [Fact]
    public void PageOrganizerPages_CoverCatalogGroupInOrder()
    {
        var groupIds = ToolCatalog.Load().Where(t => t.Category == "整理页面").Select(t => t.Id).ToList();

        Assert.Equal(groupIds, PageOrganizerPages.ToolIds);
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("split")]
    [InlineData("remove")]
    [InlineData("extract")]
    [InlineData("organize")]
    [InlineData("scan")]
    public async Task Options_KeysAlignWithCatalogFields_AndValuesAreStrings(string toolId)
    {
        var (viewModel, bridge) = CreateBase(toolId);
        viewModel.AddFiles(ValidInputsFor(toolId));
        ApplyMinimalOptions(viewModel);
        viewModel.OutputDir = TempPath;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var request = bridge.LastRequest!;
        var tool = ToolCatalog.Load().First(t => t.Id == toolId);
        Assert.Equal(toolId, request.Operation);
        Assert.All(request.Options.Keys, key => Assert.Contains(key, tool.Fields.Select(f => f.Key)));
        Assert.All(request.Options.Values, value => Assert.IsType<string>(value));
    }

    // ---------- ⑥ 真桥 → 真引擎集成 ----------

    [Fact]
    public async Task Merge_TwoGeneratedPdfs_OutputPageCountEqualsSum()
    {
        using var workspace = new TempDirectory();
        var first = System.IO.Path.Combine(workspace.Path, "a.pdf");
        var second = System.IO.Path.Combine(workspace.Path, "b.pdf");
        await File.WriteAllBytesAsync(first, TestPdfFactory.Create((612, 792), (612, 792))); // 2 页
        await File.WriteAllBytesAsync(second, TestPdfFactory.Create((792, 612), (792, 612), (792, 612))); // 3 页

        var viewModel = CreateReal<MergeViewModel>("merge");
        viewModel.AddFiles([first, second]); // OutputDir 默认 = workspace

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var result = viewModel.LastResult;
        Assert.NotNull(result);
        Assert.True(result!.Ok, result.Error);
        // 引擎输出位于新建任务子目录（AIPDF-日期-编号），不落在源目录
        Assert.StartsWith(workspace.Path, result.OutputDir);
        Assert.Contains("AIPDF-", result.OutputDir);

        var output = Assert.Single(viewModel.Results);
        Assert.Equal(5, GetPageCountWithPypdf(output.Path)); // 2 + 3 = 5
    }

    [Fact]
    public async Task Split_SemicolonGroups_ProducesPartPerGroup()
    {
        using var workspace = new TempDirectory();
        var source = System.IO.Path.Combine(workspace.Path, "src.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792), (792, 612)));

        var viewModel = CreateReal<SplitViewModel>("split");
        viewModel.AddFiles([source]);
        viewModel.RangesText = "1;2";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var result = viewModel.LastResult!;
        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.All(viewModel.Results, item => Assert.Equal(1, GetPageCountWithPypdf(item.Path)));
    }

    [Fact]
    public async Task Remove_DeletesSelectedPages_KeepsRemainder()
    {
        using var workspace = new TempDirectory();
        var source = System.IO.Path.Combine(workspace.Path, "src.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792), (792, 612)));

        var viewModel = CreateReal<RemoveViewModel>("remove");
        viewModel.AddFiles([source]);
        viewModel.PagesText = "1";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var result = viewModel.LastResult!;
        Assert.True(result.Ok, result.Error);
        var output = Assert.Single(viewModel.Results);
        Assert.Equal(1, GetPageCountWithPypdf(output.Path));
        var (width, height) = GetFirstPageSizeWithPypdf(output.Path);
        Assert.Equal(792, width); // 剩下的是原第 2 页（横向）
        Assert.Equal(612, height);
    }

    [Fact]
    public async Task Extract_KeepsSelectedPages_InOrder()
    {
        using var workspace = new TempDirectory();
        var source = System.IO.Path.Combine(workspace.Path, "src.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792), (792, 612)));

        var viewModel = CreateReal<ExtractViewModel>("extract");
        viewModel.AddFiles([source]);
        viewModel.PagesText = "2";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var result = viewModel.LastResult!;
        Assert.True(result.Ok, result.Error);
        var output = Assert.Single(viewModel.Results);
        Assert.Equal(1, GetPageCountWithPypdf(output.Path));
        var (width, height) = GetFirstPageSizeWithPypdf(output.Path);
        Assert.Equal(792, width); // 提取的是第 2 页（横向）
        Assert.Equal(612, height);
    }

    [Fact]
    public async Task Organize_ReordersPages_BySpec()
    {
        using var workspace = new TempDirectory();
        var source = System.IO.Path.Combine(workspace.Path, "src.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.Create((612, 792), (792, 612)));

        var viewModel = CreateReal<OrganizeViewModel>("organize");
        viewModel.AddFiles([source]); // 单文件工具
        viewModel.PagesText = "2,1";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var result = viewModel.LastResult!;
        Assert.True(result.Ok, result.Error);
        var output = Assert.Single(viewModel.Results);
        Assert.Equal(2, GetPageCountWithPypdf(output.Path));
        var (width, height) = GetFirstPageSizeWithPypdf(output.Path);
        Assert.Equal(792, width); // 新的第 1 页 = 原第 2 页（横向）
        Assert.Equal(612, height);
    }

    [Fact]
    public async Task Scan_FromImage_CreatesScannedPdfWithoutVisionHelper()
    {
        using var workspace = new TempDirectory();
        var image = System.IO.Path.Combine(workspace.Path, "page.bmp");
        await File.WriteAllBytesAsync(image, CreateTinyBmp());

        var viewModel = CreateReal<ScanViewModel>("scan");
        viewModel.AddFiles([image]);
        viewModel.Perspective = false; // 跳过 OCR helper 依赖：增强走纯 PIL 路径

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var result = viewModel.LastResult!;
        Assert.True(result.Ok, result.Error);
        var output = Assert.Single(viewModel.Results);
        Assert.Equal(1, GetPageCountWithPypdf(output.Path));
    }

    // ---------- 辅助 ----------

    private static (T ViewModel, RecordingBridge Bridge) Create<T>(string toolId) where T : PageOrganizerViewModelBase
    {
        var tool = ToolCatalog.Load().First(t => t.Id == toolId);
        var bridge = new RecordingBridge();
        var viewModel = (T)Activator.CreateInstance(typeof(T), bridge, tool)!;
        return (viewModel, bridge);
    }

    private static (PageOrganizerViewModelBase ViewModel, RecordingBridge Bridge) CreateBase(string toolId)
        => toolId switch
        {
            "merge" => Create<MergeViewModel>("merge"),
            "split" => Create<SplitViewModel>("split"),
            "remove" => Create<RemoveViewModel>("remove"),
            "extract" => Create<ExtractViewModel>("extract"),
            "organize" => Create<OrganizeViewModel>("organize"),
            "scan" => Create<ScanViewModel>("scan"),
            _ => throw new UnreachableException(),
        };

    private static string[] ValidInputsFor(string toolId) => toolId switch
    {
        "merge" => ["C:\\in\\a.pdf", "C:\\in\\b.pdf"],
        "organize" => ["C:\\in\\one.pdf"],
        "scan" => ["C:\\in\\page.jpg"],
        _ => ["C:\\in\\doc.pdf"],
    };

    private static void ApplyMinimalOptions(PageOrganizerViewModelBase viewModel)
    {
        switch (viewModel)
        {
            case RemoveViewModel remove: remove.PagesText = "1"; break;
            case SplitViewModel split: split.RangesText = "1"; break;
            case ExtractViewModel extract: extract.PagesText = "1"; break;
            case OrganizeViewModel organize: organize.PagesText = "1"; break;
        }
    }

    private static T CreateReal<T>(string toolId) where T : PageOrganizerViewModelBase
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        var engine = EngineBridge.ResolveDefaultEnginePath();
        Assert.True(File.Exists(python), $"缺少引擎解释器（先建 .venv）：{python}");
        Assert.True(File.Exists(engine), $"缺少引擎脚本：{engine}");
        var tool = ToolCatalog.Load().First(t => t.Id == toolId);
        return (T)Activator.CreateInstance(typeof(T), new EngineBridge(python, engine), tool)!;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static int GetPageCountWithPypdf(string pdfPath)
        => int.Parse(RunPypdf("import sys; from pypdf import PdfReader; print(len(PdfReader(sys.argv[1]).pages))", pdfPath),
            CultureInfo.InvariantCulture);

    private static (double Width, double Height) GetFirstPageSizeWithPypdf(string pdfPath)
    {
        var output = RunPypdf(
            "import sys; from pypdf import PdfReader; b=PdfReader(sys.argv[1]).pages[0].mediabox; print(b.width, b.height)",
            pdfPath);
        var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static string RunPypdf(string script, string pdfPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = EngineBridge.ResolveDefaultPythonPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(pdfPath);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 pypdf 检查进程");
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(30000);
        Assert.Equal(0, process.ExitCode);
        return stdout;
    }

    /// <summary>最小 24 位 BMP（8×8 灰块），供引擎 load_image（PIL）读取。</summary>
    private static byte[] CreateTinyBmp(int width = 8, int height = 8)
    {
        var rowBytes = (width * 3 + 3) / 4 * 4;
        var pixelData = rowBytes * height;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(54 + pixelData);
            writer.Write(0);
            writer.Write(54); // 像素数据偏移
            writer.Write(40); // BITMAPINFOHEADER
            writer.Write(width);
            writer.Write(height);
            writer.Write((short)1); // 位平面
            writer.Write((short)24); // 位深
            writer.Write(0); // BI_RGB
            writer.Write(pixelData);
            writer.Write(2835); // 水平 DPI
            writer.Write(2835); // 垂直 DPI
            writer.Write(0);
            writer.Write(0);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < rowBytes; x++)
                {
                    writer.Write((byte)0x80);
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>测试桥：只记录请求，不启动进程；Handler 可注入取消/延迟行为。</summary>
    private sealed class RecordingBridge : EngineBridge
    {
        public EngineRequest? LastRequest { get; private set; }

        public Func<EngineRequest, CancellationToken, Task<EngineResult>>? Handler { get; set; }

        public override async Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Handler is null)
            {
                await Task.Yield();
                return new EngineResult { Ok = true, Outputs = ["out.pdf"], OutputDir = "D:\\out" };
            }

            return await Handler(request, cancellationToken);
        }
    }

    /// <summary>测试用临时目录：释放时递归删除（句柄占用时容忍残留）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-organizer-" + Guid.NewGuid().ToString("N"));
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
