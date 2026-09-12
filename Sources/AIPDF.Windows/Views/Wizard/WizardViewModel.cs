// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T4 向导骨架：文件暂存 + 步骤契约 + 内联校验 + 批量提交（快照/逐项去重键）+ 取消丢弃草稿。
// UI 无关状态机：模态宿主 <see cref="WizardDialog"/> 与 xunit 测试共用同一实现。
using System.Collections.ObjectModel;
using System.IO;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.Views.Wizard;

/// <summary>
/// 向导视图模型：默认 3 步「文件暂存 → 参数 → 执行结果」（裁决 Q2）；workflow 经适配口追加多步。
/// <para>
/// 提交委托到 V2 全局串行 <see cref="ToolTaskQueue"/>（不自建队列）；每项在提交时刻固化快照并分配逐项唯一
/// <see cref="WizardDedupKey"/>（review M2）。Esc / 点遮罩取消 → 草稿丢弃【ST-26】，不产生输出。
/// </para>
/// <para>
/// 宿主实例：页面 + VM 由 <c>ToolPageRegistry.TryCreate</c> 每次新建（fresh），绝不复用 <c>TabHost</c> 缓存实例；
/// 参数步内容即该 fresh 页面（裁决 Q1 / D16），向导会话内缓存，关闭即丢弃（Q10）。
/// </para>
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly IWizardToolAdapter? _adapter;
    private readonly ToolTaskQueue _queue;
    private readonly ObservableCollection<WizardStagedFile> _stagedFiles = [];
    private readonly ObservableCollection<WizardItemResult> _results = [];
    private readonly List<IWizardStep> _steps;

    public WizardViewModel(
        Tool tool,
        IWizardToolAdapter? adapter = null,
        ToolTaskQueue? queue = null,
        IReadOnlyList<IWizardStep>? steps = null)
    {
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _adapter = adapter;
        _queue = queue ?? ToolTaskQueue.Shared;
        _steps = steps?.ToList() ?? WizardStepPlan.CreateDefault(this, adapter);
        _stagedFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasStagedFiles));
            OnPropertyChanged(nameof(StagedCount));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanSubmit));
        };
        StagedFiles = new ReadOnlyObservableCollection<WizardStagedFile>(_stagedFiles);
        Results = new ReadOnlyObservableCollection<WizardItemResult>(_results);
    }

    /// <summary>目标工具（决定扩展名过滤、显示名与去重键的 ToolId 段）。</summary>
    public Tool Tool { get; }

    /// <summary>
    /// 参数步承载的 fresh 工具页（由宿主经 <c>ToolPageRegistry.TryCreate</c> 新建并挂接文档上下文/门禁后注入）。
    /// 向导会话内缓存供步骤往返（D16），关闭即丢弃（Q10）；<c>TabHost</c> 缓存实例绝不进向导（WPF 单父级）。
    /// </summary>
    public object? HostedContent { get; set; }

    /// <summary>步骤列表（默认 3 步；workflow 多步例外）。</summary>
    public IReadOnlyList<IWizardStep> Steps => _steps;

    /// <summary>文件暂存列表（当前文档自动入选置顶并标记）。</summary>
    public ReadOnlyObservableCollection<WizardStagedFile> StagedFiles { get; }

    /// <summary>执行结果（执行结果步展示）。</summary>
    public ReadOnlyObservableCollection<WizardItemResult> Results { get; }

    /// <summary>暂存文件路径（提交快照的输入；顺序即传参顺序）。</summary>
    public IReadOnlyList<string> StagedPaths => _stagedFiles.Select(f => f.Path).ToArray();

    /// <summary>是否有暂存文件。</summary>
    public bool HasStagedFiles => _stagedFiles.Count > 0;

    /// <summary>暂存文件数。</summary>
    public int StagedCount => _stagedFiles.Count;

    /// <summary>当前步骤索引（0 基）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(IsFileStagingStep))]
    [NotifyPropertyChangedFor(nameof(IsParameterStep))]
    [NotifyPropertyChangedFor(nameof(IsResultStep))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private int _currentStepIndex;

    /// <summary>内联校验错误（ST-25；null = 无错误）。</summary>
    [ObservableProperty]
    private string? _validationMessage;

    /// <summary>状态栏文案（提交摘要 / 取消 / 未接入提示）。</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>是否正在提交（期间禁用导航与再次提交）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isSubmitting;

    /// <summary>是否已提交完成（进入执行结果步）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private bool _hasSubmitted;

    /// <summary>是否被取消（草稿已丢弃）。</summary>
    [ObservableProperty]
    private bool _isCancelled;

    /// <summary>当前步骤。</summary>
    public IWizardStep CurrentStep => _steps[Math.Clamp(CurrentStepIndex, 0, _steps.Count - 1)];

    /// <summary>是否处于文件暂存步。</summary>
    public bool IsFileStagingStep => CurrentStepIndex == 0;

    /// <summary>是否处于参数步（暂存后的第 1 个参数步）。</summary>
    public bool IsParameterStep => CurrentStepIndex > 0 && CurrentStepIndex < _steps.Count - 1;

    /// <summary>是否处于执行结果步。</summary>
    public bool IsResultStep => CurrentStepIndex == _steps.Count - 1 && _steps.Count > 0;

    /// <summary>「下一步」可用：暂存步且暂存有效；参数步的推进由提交承担，故不算此处。</summary>
    public bool CanGoNext => CurrentStepIndex == 0 && !IsSubmitting && !HasSubmitted && ValidateStaging() is null;

    /// <summary>「上一步」可用：处于参数步且尚未提交。</summary>
    public bool CanGoBack => IsParameterStep && !IsSubmitting && !HasSubmitted;

    /// <summary>「开始处理」可用：参数步、已接入适配器、暂存与参数校验均通过、未提交。</summary>
    public bool CanSubmit => IsParameterStep && !IsSubmitting && !HasSubmitted
        && _adapter is not null && ValidateStaging() is null && ValidateParameters() is null;

    /// <summary>结果步可关闭（提交完成）。</summary>
    public bool CanFinish => HasSubmitted;

    // ---------------------------------------------------------------- 文件暂存

    /// <summary>
    /// 添加文件（浏览 / 拖入统一入口）：按工具扩展名过滤、去重；不因单文件工具清空——
    /// 单文件工具的批量语义 = N 个暂存文件生成 N 个队列项（review M2 / Q8）。
    /// </summary>
    public void StageFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(raw);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                continue; // 路径字符非法：忽略
            }

            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!Tool.Extensions.Select(e => e.ToLowerInvariant()).Contains(extension))
            {
                continue; // 与引擎扩展名校验同源
            }

            if (_stagedFiles.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _stagedFiles.Add(new WizardStagedFile(path));
        }
    }

    /// <summary>当前文档自动入选、置顶并标记「当前文档」【ST-26】。</summary>
    public void PinCurrentDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return;
        }

        var existing = _stagedFiles.FirstOrDefault(
            f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _stagedFiles.Remove(existing);
        }

        _stagedFiles.Insert(0, new WizardStagedFile(normalized, isCurrentDocument: true));
    }

    /// <summary>移除暂存项。</summary>
    public bool RemoveStagedFile(WizardStagedFile? file) => file is not null && _stagedFiles.Remove(file);

    /// <summary>上移一项（顺序即传参顺序）。</summary>
    public void MoveStagedFileUp(WizardStagedFile? file) => MoveStagedFile(file, -1);

    /// <summary>下移一项。</summary>
    public void MoveStagedFileDown(WizardStagedFile? file) => MoveStagedFile(file, +1);

    /// <summary>按目标索引移动（拖拽排序）。越界不动作。</summary>
    public void MoveStagedFile(WizardStagedFile? file, int targetIndex)
    {
        if (file is null)
        {
            return;
        }

        var index = _stagedFiles.IndexOf(file);
        if (index < 0 || targetIndex < 0 || targetIndex >= _stagedFiles.Count || targetIndex == index)
        {
            return;
        }

        _stagedFiles.Move(index, targetIndex);
    }

    // ---------------------------------------------------------------- 步骤导航与校验

    /// <summary>暂存步内联校验：至少一个文件。</summary>
    public string? ValidateStaging() => _stagedFiles.Count == 0 ? "请先添加至少一个文件。" : null;

    /// <summary>参数步内联校验（委托适配器；未接入时给出明确提示）。</summary>
    public string? ValidateParameters()
        => _adapter is null ? "该工具尚未接入向导。" : _adapter.ValidateParameters(StagedPaths);

    /// <summary>前进到参数步（含内联校验；失败仅显示错误不前进）。</summary>
    public void GoNext()
    {
        if (CurrentStepIndex >= _steps.Count - 1 || IsSubmitting || HasSubmitted)
        {
            return;
        }

        if (CurrentStep.Validate() is { } error)
        {
            ValidationMessage = error;
            return;
        }

        ValidationMessage = null;
        CurrentStepIndex++;
    }

    /// <summary>回到暂存步（仅参数步且未提交时可回退）。</summary>
    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        ValidationMessage = null;
        CurrentStepIndex = Math.Max(0, CurrentStepIndex - 1);
    }

    // ---------------------------------------------------------------- 提交 / 取消

    /// <summary>
    /// 批量提交：提交时刻固化每项快照并分配逐项唯一去重键，全部提交全局串行队列（FR-40）。
    /// 多文件工具 = 1 个多文件队列任务；单文件工具 = N 个队列任务（不被去重塌缩，review M2 / Q8）。
    /// </summary>
    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (HasSubmitted || IsSubmitting)
        {
            return;
        }

        if (ValidateStaging() is { } stagingError)
        {
            ValidationMessage = stagingError;
            return;
        }

        if (ValidateParameters() is { } parameterError)
        {
            ValidationMessage = parameterError;
            return;
        }

        IsSubmitting = true;
        ValidationMessage = null;
        StatusMessage = "正在提交…";
        try
        {
            // 快照：BuildSubmitItems 在提交时刻读取 VM 当前参数/输出目录并深拷贝固化（review M2）。
            var snapshots = _adapter!.BuildSubmitItems(StagedPaths);
            if (snapshots.Count == 0)
            {
                ValidationMessage = "没有可提交的文件。";
                return;
            }

            var items = new List<ToolTaskItem>(snapshots.Count);
            var workStarted = new bool[snapshots.Count];
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                var index = i;
                // 逐项唯一去重键（VM 工具 id + 固化文件集 + 索引）：绝不用 this，避免 N 项塌缩为 1。
                var key = new WizardDedupKey(Tool.Id, string.Join('\u001f', snapshot.Files), i);
                items.Add(_queue.Submit(snapshot.Name, token =>
                {
                    workStarted[index] = true;
                    return ExecuteSnapshotAsync(snapshot, token);
                }, key));
            }

            for (var i = 0; i < items.Count; i++)
            {
                await items[i].Completion.Task.ConfigureAwait(true);

                // FR-21 门禁（ToolTaskQueue.BeforeStart）拦截时工作委托未执行、不会产生结果项：
                // 把队列记录的原因显式落到结果步，避免「批注保存失败，工具未启动」在向导内静默丢失。
                if (!workStarted[i] && items[i].State == ToolTaskState.Failed)
                {
                    var reason = string.IsNullOrWhiteSpace(items[i].Error)
                        ? "批注保存失败，向导未执行。"
                        : items[i].Error!;
                    _results.Add(WizardItemResult.Failure(reason));
                }
            }

            HasSubmitted = true;
            CurrentStepIndex = _steps.Count - 1; // 进入执行结果步
            StatusMessage = BuildSummary();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>取消向导：丢弃草稿、不产生输出【ST-26 / Q10】。</summary>
    public void Cancel()
    {
        _stagedFiles.Clear();
        _results.Clear();
        ValidationMessage = null;
        HasSubmitted = false;
        IsCancelled = true;
        StatusMessage = "已取消，草稿已丢弃。";
    }

    /// <summary>执行单个快照项：异常/取消归一为结果项，不中断其余队列项。</summary>
    private async Task ExecuteSnapshotAsync(WizardSubmitItem snapshot, CancellationToken token)
    {
        WizardItemResult result;
        try
        {
            result = await _adapter!.ExecuteItemAsync(snapshot, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = WizardItemResult.Failure("已取消。");
        }
        catch (Exception error)
        {
            result = WizardItemResult.Failure(error.Message);
        }

        RunOnUiThread(() => _results.Add(result));
    }

    private string BuildSummary()
    {
        var ok = _results.Count(r => r.Ok);
        var failed = _results.Count - ok;
        return failed == 0
            ? $"完成：{ok} 个任务。"
            : $"完成：{ok} 个成功，{failed} 个失败。";
    }

    /// <summary>
    /// UI 线程封送（F3-BLK-1 / 082c7fd）：无 WPF Application、或 dispatcher 归属线程已退出/正在关闭时内联执行，
    /// 避免 BeginInvoke 到死 dispatcher 后永远等不到回调。
    /// </summary>
    private static void RunOnUiThread(Action body)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null
            || !dispatcher.Thread.IsAlive
            || dispatcher.HasShutdownStarted
            || dispatcher.HasShutdownFinished)
        {
            body();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            body();
            return;
        }

        dispatcher.Invoke(body);
    }
}

/// <summary>默认步骤计划：统一 3 步；workflow 多步例外经 <see cref="IWizardToolAdapter.AdditionalStepTitles"/> 追加。</summary>
public static class WizardStepPlan
{
    /// <summary>文件暂存步标题。</summary>
    public const string FileStagingTitle = "文件暂存";

    /// <summary>参数步标题。</summary>
    public const string ParametersTitle = "参数";

    /// <summary>执行结果步标题。</summary>
    public const string ResultTitle = "执行结果";

    /// <summary>
    /// 生成默认步骤：文件暂存（≥1 文件）→ 参数（适配器校验）→ [workflow 追加步] → 执行结果。
    /// 参数步内容由模态宿主挂接 fresh 工具页；这里只保留契约与校验，不重写参数 UI。
    /// </summary>
    public static List<IWizardStep> CreateDefault(WizardViewModel viewModel, IWizardToolAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var steps = new List<IWizardStep>
        {
            new WizardStep(FileStagingTitle, viewModel.ValidateStaging),
        };

        // workflow 多步例外：适配器声明的额外参数步（默认 0）。
        var additional = adapter?.AdditionalStepTitles() ?? [];
        if (additional.Count == 0)
        {
            steps.Add(new WizardStep(ParametersTitle, viewModel.ValidateParameters));
        }
        else
        {
            for (var i = 0; i < additional.Count; i++)
            {
                steps.Add(new WizardStep(additional[i], viewModel.ValidateParameters));
            }
        }

        steps.Add(new WizardStep(ResultTitle, validate: null, content: null, isFinal: true));
        return steps;
    }
}
