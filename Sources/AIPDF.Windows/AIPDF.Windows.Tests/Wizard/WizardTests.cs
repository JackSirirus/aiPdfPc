// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T4 向导骨架测试：步骤契约 / 文件暂存 / 内联校验 / 取消丢弃草稿 /
// FR-21 门禁触发 / 批量逐项快照与去重键（N 项不塌缩）/ 结果处置策略。
using System.Collections.Concurrent;
using System.IO;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.Views.Wizard;
using Xunit;

namespace AIPDF.Windows.Tests.Wizard;

public sealed class WizardTests
{
    // ---------------------------------------------------------------- 步骤契约（Q2）

    [Fact]
    public void DefaultPlan_HasThreeSteps_FileStagingParametersResult()
    {
        var viewModel = NewViewModel(MultipleAdapter());

        Assert.Equal(3, viewModel.Steps.Count);
        Assert.Equal(WizardStepPlan.FileStagingTitle, viewModel.Steps[0].Title);
        Assert.Equal(WizardStepPlan.ParametersTitle, viewModel.Steps[1].Title);
        Assert.Equal(WizardStepPlan.ResultTitle, viewModel.Steps[2].Title);
        Assert.True(viewModel.Steps[2].IsFinal);
        Assert.True(viewModel.IsFileStagingStep);
    }

    [Fact]
    public void WorkflowException_AddsAdapterParameterSteps()
    {
        var adapter = MultipleAdapter();
        adapter.AdditionalSteps.Add("选择预设");
        adapter.AdditionalSteps.Add("步骤参数");

        var viewModel = new WizardViewModel(Tool("workflow"), adapter, new ToolTaskQueue());

        Assert.Equal(4, viewModel.Steps.Count);
        Assert.Equal("选择预设", viewModel.Steps[1].Title);
        Assert.Equal("步骤参数", viewModel.Steps[2].Title);
        Assert.Equal(WizardStepPlan.ResultTitle, viewModel.Steps[3].Title);
    }

    [Fact]
    public void GoNext_NoFiles_ShowsInlineValidationAndStays()
    {
        var viewModel = NewViewModel(MultipleAdapter());

        viewModel.GoNext();

        Assert.True(viewModel.IsFileStagingStep); // 未前进
        Assert.Equal("请先添加至少一个文件。", viewModel.ValidationMessage);
    }

    [Fact]
    public void GoNext_WithFiles_AdvancesToParameters()
    {
        var viewModel = NewViewModel(MultipleAdapter());
        viewModel.StageFiles([Pdf("a")]);

        Assert.True(viewModel.CanGoNext);
        viewModel.GoNext();

        Assert.True(viewModel.IsParameterStep);
        Assert.Null(viewModel.ValidationMessage);
    }

    [Fact]
    public async Task Submit_ParameterValidationFails_DoesNotEnqueue()
    {
        var adapter = MultipleAdapter();
        adapter.ParamError = "参数错误。";
        var queue = new ToolTaskQueue();
        var viewModel = NewViewModel(adapter, queue);
        viewModel.StageFiles([Pdf("a")]);
        viewModel.GoNext();

        await viewModel.SubmitAsync();

        Assert.Equal("参数错误。", viewModel.ValidationMessage);
        Assert.False(viewModel.HasSubmitted);
        Assert.Empty(adapter.Executed);
        Assert.Empty(queue.Pending);
        Assert.Null(queue.RunningItem);
    }

    // ---------------------------------------------------------------- 文件暂存（ST-26）

    [Fact]
    public void PinCurrentDocument_TopsAndMarksIt()
    {
        var viewModel = NewViewModel(MultipleAdapter());
        viewModel.StageFiles([Pdf("a"), Pdf("b")]);
        viewModel.PinCurrentDocument(Pdf("b"));

        Assert.Equal(2, viewModel.StagedCount);
        Assert.True(viewModel.StagedFiles[0].IsCurrentDocument);
        Assert.Equal("当前文档", viewModel.StagedFiles[0].Badge);
        Assert.Equal(Path.GetFullPath(Pdf("b")), viewModel.StagedFiles[0].Path);
    }

    [Fact]
    public void Staging_FiltersByExtension_AndDeduplicates()
    {
        var viewModel = NewViewModel(MultipleAdapter());
        viewModel.StageFiles([Pdf("a"), Pdf("a"), Path.Combine(TempRoot, "note.txt")]);

        Assert.Equal(1, viewModel.StagedCount);
    }

    [Fact]
    public void Staging_MoveAndRemove_Reorders()
    {
        var viewModel = NewViewModel(MultipleAdapter());
        viewModel.StageFiles([Pdf("a"), Pdf("b"), Pdf("c")]);

        viewModel.MoveStagedFileDown(viewModel.StagedFiles[0]);
        Assert.Equal(["b.pdf", "a.pdf", "c.pdf"], viewModel.StagedFiles.Select(f => f.Name));

        viewModel.RemoveStagedFile(viewModel.StagedFiles.Single(f => f.Name == "b.pdf"));
        Assert.Equal(["a.pdf", "c.pdf"], viewModel.StagedFiles.Select(f => f.Name));
    }

    // ---------------------------------------------------------------- 取消（ST-26 / Q10）

    [Fact]
    public void Cancel_DiscardsDraft_AndProducesNoOutput()
    {
        var adapter = MultipleAdapter();
        var queue = new ToolTaskQueue();
        var viewModel = NewViewModel(adapter, queue);
        viewModel.StageFiles([Pdf("a"), Pdf("b")]);
        viewModel.PinCurrentDocument(Pdf("a"));

        viewModel.Cancel();

        Assert.True(viewModel.IsCancelled);
        Assert.Empty(viewModel.StagedFiles);
        Assert.Empty(viewModel.Results);
        Assert.Empty(adapter.Executed);
        Assert.Empty(queue.Pending);
        Assert.Null(queue.RunningItem);
    }

    // ---------------------------------------------------------------- FR-21 门禁（review M1）

    [Fact]
    public async Task Submit_QueueGateFalse_BlocksWork_AndFiresGate()
    {
        var gateCalls = 0;
        var queue = new ToolTaskQueue
        {
            BeforeStart = (_, _) => { gateCalls++; return Task.FromResult(false); },
        };
        var adapter = MultipleAdapter();
        var viewModel = NewViewModel(adapter, queue);
        viewModel.StageFiles([Pdf("a")]);
        viewModel.GoNext();

        await viewModel.SubmitAsync();

        Assert.Equal(1, gateCalls);        // 门禁确实触发
        Assert.Empty(adapter.Executed);    // 拦截：工作委托不执行
        Assert.True(viewModel.HasSubmitted);
    }

    [Fact]
    public async Task Submit_QueueGateTrue_FiresGateOncePerBatchItem_AndRuns()
    {
        var gateCalls = 0;
        var queue = new ToolTaskQueue
        {
            BeforeStart = (_, _) => { gateCalls++; return Task.FromResult(true); },
        };
        var adapter = MultipleAdapter();
        var viewModel = NewViewModel(adapter, queue);
        viewModel.StageFiles([Pdf("a")]);
        viewModel.GoNext();

        await viewModel.SubmitAsync();

        Assert.Equal(1, gateCalls);
        Assert.Single(adapter.Executed);
    }

    // ---------------------------------------------------------------- 批量：粒度 / 去重键 / 快照（review M2 / Q8）

    [Fact]
    public async Task Submit_MultiFileTool_OneQueueItem()
    {
        var adapter = MultipleAdapter(); // Multiple = true
        var viewModel = NewViewModel(adapter, new ToolTaskQueue());
        viewModel.StageFiles([Pdf("a"), Pdf("b"), Pdf("c")]);
        viewModel.GoNext();

        await viewModel.SubmitAsync();

        Assert.Single(adapter.Executed); // 一个多文件队列任务
        Assert.Equal(3, adapter.Executed.Single().Files.Count);
    }

    [Fact]
    public async Task Submit_SingleFileTool_NItemsWithDistinctDedupKeys_NotCollapsed()
    {
        var gate = new TaskCompletionSource();
        var adapter = MultipleAdapter();
        adapter.Multiple = false; // 单文件工具 → N 个队列项
        adapter.OnExecute = async (_, _) =>
        {
            if (adapter.Executed.Count == 1)
            {
                await gate.Task; // 阻塞第 1 项，令其余项保持排队
            }
        };
        var queue = new ToolTaskQueue();
        var viewModel = NewViewModel(adapter, queue);
        viewModel.StageFiles([Pdf("a"), Pdf("b"), Pdf("c")]);
        viewModel.GoNext();

        var submit = viewModel.SubmitAsync();

        // 此刻：1 项执行中 + 2 项排队；三者去重键必须互不相同（不能是 this 而塌缩为 1）。
        var keys = new List<object?>();
        if (queue.RunningItem is not null)
        {
            keys.Add(queue.RunningItem.DedupKey);
        }

        keys.AddRange(queue.Pending.Select(p => p.DedupKey));
        Assert.Equal(3, keys.Count);
        Assert.Equal(3, keys.Distinct().Count());

        gate.SetResult();
        await submit;

        Assert.Equal(3, adapter.Executed.Count);
    }

    [Fact]
    public async Task Submit_PostSubmitMutation_DoesNotAffectQueuedSnapshots()
    {
        var firstStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var adapter = MultipleAdapter();
        adapter.Multiple = false;
        adapter.OutputDir = @"C:\out-old";
        adapter.Options["quality"] = "high";
        adapter.OnExecute = async (item, _) =>
        {
            if (item.Files[0].EndsWith("a.pdf", StringComparison.Ordinal))
            {
                firstStarted.SetResult();
                await release.Task; // 第 1 项执行中，第 2 项已入队但未执行
            }
        };

        var queue = new ToolTaskQueue();
        var viewModel = NewViewModel(adapter, queue);
        viewModel.StageFiles([Pdf("a"), Pdf("b")]);
        viewModel.GoNext();

        var submit = viewModel.SubmitAsync();
        await firstStarted.Task;

        // 提交后修改 VM/适配器的可变状态：延迟队列项必须仍用提交时刻快照。
        adapter.Options["quality"] = "low";
        adapter.OutputDir = @"C:\out-new";

        release.SetResult();
        await submit;

        var second = adapter.Executed.Single(i => i.Files[0].EndsWith("b.pdf", StringComparison.Ordinal));
        Assert.Equal("high", second.Options["quality"]);
        Assert.Equal(@"C:\out-old", second.OutputDir);
    }

    // ---------------------------------------------------------------- 结果处置（D5 / ST-29）

    [Fact]
    public void ResultPolicy_AllPdf_OpensTabs()
        => Assert.Equal(WizardResultAction.OpenTabs,
            WizardResultPolicy.Decide([@"C:\o\a.pdf", @"C:\o\b.pdf"]));

    [Fact]
    public void ResultPolicy_NonPdf_ToastAndFolder()
    {
        Assert.Equal(WizardResultAction.ToastAndOpenFolder, WizardResultPolicy.Decide([@"C:\o\a.docx"]));
        Assert.Equal(WizardResultAction.ToastAndOpenFolder, WizardResultPolicy.Decide([@"C:\o\a.pdf", @"C:\o\b.docx"]));
        Assert.Equal(WizardResultAction.ToastAndOpenFolder, WizardResultPolicy.Decide([]));
        Assert.Equal(WizardResultAction.ToastAndOpenFolder, WizardResultPolicy.Decide([@"C:\o\a.pdf"], anyFailed: true));
    }

    // ---------------------------------------------------------------- 测试替身

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "aipdf-wizard");

    private static string Pdf(string name) => Path.Combine(TempRoot, name + ".pdf");

    private static Tool Tool(string id) => ToolCatalog.Load().Single(t => t.Id == id);

    private static FakeAdapter MultipleAdapter() => new() { Multiple = true };

    private static WizardViewModel NewViewModel(FakeAdapter adapter, ToolTaskQueue? queue = null)
        => new(Tool("merge"), adapter, queue ?? new ToolTaskQueue());

    /// <summary>测试用适配器：可切换多文件/单文件批量语义，记录提交快照与执行。</summary>
    private sealed class FakeAdapter : IWizardToolAdapter
    {
        public bool Multiple { get; set; } = true;

        public Dictionary<string, object?> Options { get; } = new();

        public string OutputDir { get; set; } = "";

        public string? ParamError { get; set; }

        public List<string> AdditionalSteps { get; } = [];

        public ConcurrentQueue<WizardSubmitItem> Executed { get; } = new();

        public Func<WizardSubmitItem, CancellationToken, Task>? OnExecute { get; set; }

        public string? ValidateParameters(IReadOnlyList<string> stagedFiles)
            => stagedFiles.Count == 0 ? "请先选择文件。" : ParamError;

        public IReadOnlyList<WizardSubmitItem> BuildSubmitItems(IReadOnlyList<string> stagedFiles)
        {
            if (Multiple)
            {
                return [new WizardSubmitItem("合并 PDF", stagedFiles, Options, OutputDir)];
            }

            return [.. stagedFiles.Select(file => new WizardSubmitItem("合并 PDF", [file], Options, OutputDir))];
        }

        public async Task<WizardItemResult> ExecuteItemAsync(WizardSubmitItem item, CancellationToken token)
        {
            Executed.Enqueue(item);
            if (OnExecute is not null)
            {
                await OnExecute(item, token);
            }

            return new WizardItemResult(true, [.. item.Files.Select(f => f + ".pdf")], null, item.OutputDir);
        }

        public IReadOnlyList<string> AdditionalStepTitles() => AdditionalSteps;
    }
}
