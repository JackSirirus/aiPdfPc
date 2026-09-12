// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T8 向导工具接入测试：5 个 VM 层级实现 IWizardToolAdapter 的最小注入点契约——
// 参数校验 / 提交时刻快照（review M2）/ 单文件工具 N 项不塌缩（Q8）/ 引擎执行（不回读 VM 状态）/
// 批量经全局 ToolTaskQueue / workflow 多步例外，以及 21 个向导工具的适配器覆盖。
using System.IO;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views.Wizard;
using Xunit;

namespace AIPDF.Windows.Tests.Wizard;

public sealed class WizardToolAdapterTests
{
    // ---------------------------------------------------------------- 21 工具覆盖（通道分布回归）

    [Fact]
    public void AllWizardTools_ExposeIWizardToolAdapter_AndChannelIsWizard()
    {
        var wizardIds = ToolCatalog.Load()
            .Where(tool => ToolChannels.ChannelFor(tool) == ToolChannel.Wizard)
            .Select(tool => tool.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(21, wizardIds.Length); // 通道分布回归：向导 21

        foreach (var id in wizardIds)
        {
            Assert.Equal(ToolChannel.Wizard, ToolChannels.ChannelFor(ToolOf(id)));
            var adapter = CreateAdapter(id, new RecordingBridge());
            Assert.NotNull(adapter);
        }
    }

    // ---------------------------------------------------------------- 文件暂存（ST-26）

    [Fact]
    public void Wizard_StagingCurrentDocument_TopsAndMarksIt_ForRealAdapter()
    {
        using var temp = new TempDirectory();
        var adapter = new MergeViewModel(new RecordingBridge(), ToolOf("merge"));
        var wizard = new WizardViewModel(ToolOf("merge"), adapter, new ToolTaskQueue());
        var first = temp.CreatePdf("a.pdf");
        var current = temp.CreatePdf("b.pdf");

        wizard.StageFiles([first]);
        wizard.PinCurrentDocument(current);

        Assert.Equal(2, wizard.StagedCount);
        Assert.True(wizard.StagedFiles[0].IsCurrentDocument);
    }

    // ---------------------------------------------------------------- 参数校验（ST-25）

    [Theory]
    [InlineData("merge")]
    [InlineData("split")]
    [InlineData("compress")]
    [InlineData("unlock")]
    [InlineData("rotate")]
    [InlineData("workflow")]
    public void ValidateParameters_NoFiles_ReturnsError(string toolId)
    {
        var adapter = CreateAdapter(toolId, new RecordingBridge());
        Assert.False(string.IsNullOrEmpty(adapter.ValidateParameters([])));
    }

    [Fact]
    public void Merge_ValidateParameters_RequiresAtLeastTwoStagedFiles()
    {
        using var temp = new TempDirectory();
        var adapter = new MergeViewModel(new RecordingBridge(), ToolOf("merge"));
        var one = temp.CreatePdf("a.pdf");
        var two = temp.CreatePdf("b.pdf");

        Assert.Contains("至少添加两份", adapter.ValidateParameters([one]));
        Assert.Null(adapter.ValidateParameters([one, two]));
    }

    [Fact]
    public void Compare_ValidateParameters_RequiresExactlyTwoStagedFiles()
    {
        using var temp = new TempDirectory();
        var adapter = new CompareViewModel(new RecordingBridge(), ToolOf("compare"));
        var a = temp.CreatePdf("a.pdf");
        var b = temp.CreatePdf("b.pdf");
        var c = temp.CreatePdf("c.pdf");

        Assert.Contains("正好两份", adapter.ValidateParameters([a]));
        Assert.Contains("正好两份", adapter.ValidateParameters([a, b, c]));
        Assert.Null(adapter.ValidateParameters([a, b]));
    }

    [Fact]
    public void ValidateParameters_EmptyOutputDir_FallsBackToStagedFileDirectory()
    {
        using var temp = new TempDirectory();
        var adapter = new SplitViewModel(new RecordingBridge(), ToolOf("split"));
        var file = temp.CreatePdf("a.pdf");

        Assert.Equal("", adapter.OutputDir); // 无激活文档/页面选择时为空
        Assert.Null(adapter.ValidateParameters([file])); // 回退到源文件旁，与工具页默认语义一致
    }

    [Fact]
    public void Security_ValidateParameters_BadConfiguredOutputDir_ReturnsError()
    {
        using var temp = new TempDirectory();
        var adapter = new UnlockViewModel(new RecordingBridge(), ToolOf("unlock"))
        {
            OutputDir = Path.Combine(temp.Path, "missing-subdir"),
        };

        Assert.Equal("请选择有效的输出文件夹。", adapter.ValidateParameters([temp.CreatePdf("a.pdf")]));
    }

    // ---------------------------------------------------------------- 提交快照（review M2）

    [Fact]
    public async Task Security_BuildSubmitItems_SnapshotsPassword_NotAffectedByLaterMutation()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var adapter = new UnlockViewModel(bridge, ToolOf("unlock")) { OutputDir = temp.Path };

        adapter.PasswordProvider = () => "snapshot-secret";
        var files = new[] { temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf") };
        var items = adapter.BuildSubmitItems(files);

        Assert.Single(items); // unlock 目录 Multiple=true → 1 个多文件任务
        adapter.PasswordProvider = () => "changed-after-submit"; // 提交后改动 VM 状态

        var result = await adapter.ExecuteItemAsync(items[0], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Single(bridge.Requests);
        Assert.Equal("snapshot-secret", (string)bridge.Requests[0].Options["password"]!);
        Assert.Equal(files, bridge.Requests[0].Files);
    }

    [Fact]
    public async Task Optimizer_BuildSubmitItems_SnapshotsOptions()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var adapter = new CompressViewModel(bridge, ToolOf("compress")) { OutputDir = temp.Path };
        adapter.SelectQuality("small");

        var items = adapter.BuildSubmitItems([temp.CreatePdf("a.pdf")]);
        adapter.SelectQuality("lossless");

        await adapter.ExecuteItemAsync(items[0], CancellationToken.None);

        Assert.Equal("compress", bridge.Requests[0].Operation);
        Assert.Equal("small", (string)bridge.Requests[0].Options["quality"]!);
    }

    [Fact]
    public async Task Converter_BuildSubmitItems_SnapshotsOptions()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var adapter = new PdfToImagesViewModel(bridge, ToolOf("pdf_to_images")) { OutputDir = temp.Path };
        adapter.PagesText = "1-2";

        var items = adapter.BuildSubmitItems([temp.CreatePdf("a.pdf")]);
        adapter.PagesText = "9-9";

        await adapter.ExecuteItemAsync(items[0], CancellationToken.None);

        Assert.Equal("pdf_to_images", bridge.Requests[0].Operation);
        Assert.Equal("1-2", (string)bridge.Requests[0].Options["pages"]!);
    }

    [Fact]
    public async Task PageOrganizer_BuildSubmitItems_SnapshotsOptions()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var adapter = new SplitViewModel(bridge, ToolOf("split")) { OutputDir = temp.Path };
        adapter.RangesText = "1-3";

        var items = adapter.BuildSubmitItems([temp.CreatePdf("a.pdf")]);
        adapter.RangesText = "4-6";

        await adapter.ExecuteItemAsync(items[0], CancellationToken.None);

        Assert.Equal("split", bridge.Requests[0].Operation);
        Assert.Equal("1-3", (string)bridge.Requests[0].Options["ranges"]!);
    }

    [Fact]
    public async Task Editor_BuildSubmitItems_SnapshotsOptions_AndUsesInjectedBridge()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var adapter = new RotateViewModel(ToolOf("rotate"), NoopEditor) { OutputDir = temp.Path, WizardBridge = bridge };
        adapter.Pages = "2";

        var items = adapter.BuildSubmitItems([temp.CreatePdf("a.pdf")]);
        adapter.Pages = "99"; // 提交后改动

        var result = await adapter.ExecuteItemAsync(items[0], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("rotate", bridge.Requests[0].Operation);
        Assert.Equal("2", (string)bridge.Requests[0].Options["pages"]!);
    }

    // ---------------------------------------------------------------- 单文件工具 N 项不塌缩（Q8 / review M2）

    [Fact]
    public async Task SingleFileAdapter_ProducesNQueueItems_DistinctDedupKeys_Fifo()
    {
        using var temp = new TempDirectory();
        var release = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();
        var bridge = new RecordingBridge();
        var calls = 0;
        bridge.Handler = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await release.Task;
            }

            return Ok();
        };

        var tool = ToolOf("unlock");
        tool.Multiple = false; // 单文件工具 → N 个队列项
        var queue = new ToolTaskQueue();
        var adapter = new UnlockViewModel(bridge, tool) { OutputDir = temp.Path, PasswordProvider = () => "p" };
        var wizard = new WizardViewModel(tool, adapter, queue);

        var files = new[] { temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf"), temp.CreatePdf("c.pdf") };
        wizard.StageFiles(files);
        wizard.GoNext();

        var submit = wizard.SubmitAsync();
        await firstStarted.Task;

        var keys = new List<object?>();
        if (queue.RunningItem is not null)
        {
            keys.Add(queue.RunningItem.DedupKey);
        }

        keys.AddRange(queue.Pending.Select(p => p.DedupKey));
        Assert.Equal(3, keys.Count);
        Assert.Equal(3, keys.Distinct().Count()); // 逐项去重键：绝不用 this，N 项不塌缩

        release.SetResult();
        await submit;

        Assert.Equal(3, bridge.Requests.Count);
        Assert.Equal(["a.pdf"], bridge.Requests[0].Files.Select(Path.GetFileName));
        Assert.Equal(["b.pdf"], bridge.Requests[1].Files.Select(Path.GetFileName));
        Assert.Equal(["c.pdf"], bridge.Requests[2].Files.Select(Path.GetFileName)); // FIFO，顺序即暂存顺序
    }

    // ---------------------------------------------------------------- 批量经全局队列 + 结果

    [Fact]
    public async Task MultiFileAdapter_SubmitThroughWizard_OneQueueItem_ResultsCollected()
    {
        using var temp = new TempDirectory();
        var queue = new ToolTaskQueue();
        var bridge = new RecordingBridge();
        var adapter = new MergeViewModel(bridge, ToolOf("merge")) { OutputDir = temp.Path };
        var wizard = new WizardViewModel(ToolOf("merge"), adapter, queue);

        var files = new[] { temp.CreatePdf("c.pdf"), temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf") };
        wizard.StageFiles(files); // 顺序即传参顺序
        wizard.GoNext();

        await wizard.SubmitAsync();

        Assert.True(wizard.HasSubmitted);
        Assert.Equal(3, wizard.StagedPaths.Count);
        Assert.Single(bridge.Requests); // 多文件工具 = 1 个多文件队列任务
        Assert.Equal("merge", bridge.Requests[0].Operation);
        Assert.Equal(files, bridge.Requests[0].Files);
        Assert.Single(wizard.Results);
        Assert.True(wizard.Results[0].Ok);
    }

    [Fact]
    public async Task Wizard_QueueGate_BlocksSubmit_DoesNotExecute()
    {
        using var temp = new TempDirectory();
        var gateCalls = 0;
        var queue = new ToolTaskQueue
        {
            BeforeStart = (_, _) => { gateCalls++; return Task.FromResult(false); },
        };
        var bridge = new RecordingBridge();
        var adapter = new MergeViewModel(bridge, ToolOf("merge")) { OutputDir = temp.Path };
        var wizard = new WizardViewModel(ToolOf("merge"), adapter, queue);
        wizard.StageFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);
        wizard.GoNext();

        await wizard.SubmitAsync();

        Assert.Equal(1, gateCalls); // 向导提交不绕过队列门禁（FR-21）
        Assert.Empty(bridge.Requests); // 拦截：工作委托不执行
        Assert.True(wizard.HasSubmitted);
    }

    // ---------------------------------------------------------------- 取消（ST-26 / Q10）

    [Fact]
    public void Wizard_Cancel_WithRealAdapter_DiscardsDraft_NoRequests()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var adapter = new MergeViewModel(bridge, ToolOf("merge")) { OutputDir = temp.Path };
        var wizard = new WizardViewModel(ToolOf("merge"), adapter, new ToolTaskQueue());
        wizard.StageFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        wizard.Cancel();

        Assert.True(wizard.IsCancelled);
        Assert.Empty(wizard.StagedFiles);
        Assert.Empty(bridge.Requests);
    }

    // ---------------------------------------------------------------- workflow 多步例外（Q2）

    [Fact]
    public async Task Workflow_Adapter_DeclaresMultiStep_AndSubmitsSnapshotSteps()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var tool = ToolOf("workflow");
        var adapter = new WorkflowToolViewModel(bridge, ToolCatalog.Load(), catalogTool: tool)
        {
            OutputDir = temp.Path,
        };
        adapter.AddStepInternal();
        adapter.Steps[0].SelectedOperation = ToolOf("merge");

        Assert.Equal(2, adapter.AdditionalStepTitles().Count); // workflow = 多步例外

        var files = new[] { temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf") };
        Assert.Null(adapter.ValidateParameters(files));

        var items = adapter.BuildSubmitItems(files);
        adapter.Steps[0].SelectedOperation = ToolOf("split"); // 提交后改动

        await adapter.ExecuteItemAsync(items[0], CancellationToken.None);

        Assert.Single(bridge.Requests);
        Assert.Equal("workflow", bridge.Requests[0].Operation);
        var steps = Assert.IsType<List<object?>>(bridge.Requests[0].Options["steps"]);
        var step = Assert.IsType<Dictionary<string, object?>>(steps[0]);
        Assert.Equal("merge", step["operation"]);
    }

    // ---------------------------------------------------------------- 测试替身

    private static readonly ExecuteEditorToolDelegate NoopEditor = (_, _, _, _) => Task.CompletedTask;

    private static Tool ToolOf(string id) => ToolCatalog.Load().First(tool => tool.Id == id);

    private static IWizardToolAdapter CreateAdapter(string id, EngineBridge bridge)
    {
        var tool = ToolOf(id);
        return id switch
        {
            "merge" => new MergeViewModel(bridge, tool),
            "split" => new SplitViewModel(bridge, tool),
            "remove" => new RemoveViewModel(bridge, tool),
            "extract" => new ExtractViewModel(bridge, tool),
            "compress" => new CompressViewModel(bridge, tool),
            "repair" => new RepairViewModel(bridge, tool),
            "ocr" => new OcrViewModel(bridge, tool),
            "pdf_to_images" => new PdfToImagesViewModel(bridge, tool),
            "pdf_to_word" => new PdfToWordViewModel(bridge, tool),
            "pdf_to_ppt" => new PdfToPptViewModel(bridge, tool),
            "pdf_to_excel" => new PdfToExcelViewModel(bridge, tool),
            "markdown" => new MarkdownViewModel(bridge, tool),
            "rotate" => new RotateViewModel(tool, NoopEditor),
            "numbers" => new PageNumbersViewModel(tool, NoopEditor),
            "watermark" => new WatermarkViewModel(tool, NoopEditor),
            "unlock" => new UnlockViewModel(bridge, tool),
            "protect" => new ProtectViewModel(bridge, tool),
            "sign" => new SignViewModel(bridge, tool),
            "redact" => new RedactViewModel(bridge, tool),
            "compare" => new CompareViewModel(bridge, tool),
            "workflow" => new WorkflowToolViewModel(bridge, ToolCatalog.Load(), catalogTool: tool),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "非向导工具"),
        };
    }

    private static EngineResult Ok() => new()
    {
        Ok = true,
        Outputs = [Path.Combine(Path.GetTempPath(), "aipdf-t8-out.pdf")],
        OutputDir = Path.GetTempPath(),
    };

    /// <summary>测试桥：只记录请求，不启动进程；Handler 可注入阻塞/取消行为。</summary>
    private sealed class RecordingBridge : EngineBridge
    {
        public List<EngineRequest> Requests { get; } = [];

        public Func<EngineRequest, CancellationToken, Task<EngineResult>>? Handler { get; set; }

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Handler is null ? Task.FromResult(Ok()) : Handler(request, cancellationToken);
        }
    }

    /// <summary>测试用临时目录：释放时递归删除（句柄占用时容忍残留）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-t8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreatePdf(string name)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, "%PDF-1.4\n");
            return file;
        }

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
