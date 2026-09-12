// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.Views.Wizard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AIPDF.Windows.Tools;

/// <summary>工具页文件条目。只保存路径，不读取内容（大文件纪律：内容一律由引擎子进程按需读取）。</summary>
public sealed class PageFileItem
{
    public PageFileItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>结果列表条目：引擎输出文件路径（位于新建任务子目录内）。</summary>
public sealed class PageResultItem
{
    public PageResultItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 页面整理组工具页视图模型基类：文件输入（拖拽 + 浏览）→ 参数表单 → 输出目录 → 执行/取消 → 进度与结果。
/// 输出约定：outputDir 只传父目录，引擎在其下新建 AIPDF-日期-编号 任务子目录，绝不覆盖源文件。
/// </summary>
public abstract partial class PageOrganizerViewModelBase : ObservableObject, IToolDocumentReceiver, ICurrentDocumentPinnable, IWizardToolAdapter
{
    /// <summary>与 backend/engine.py parse_pages 的单项格式一致（数字或 a-b）。</summary>
    private static readonly Regex PagesItemPattern = new(@"^\s*\d+\s*(?:-\s*\d+\s*)?$", RegexOptions.Compiled);

    private readonly EngineBridge? _bridge;
    private ToolTaskItem? _activeTask;

    protected PageOrganizerViewModelBase(EngineBridge? bridge, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _bridge = bridge;
        Tool = tool;
        FileDialogFilter = BuildFilter(tool);
        Files.CollectionChanged += OnFilesCollectionChanged;
    }

    /// <summary>全局串行任务队列（V2 T7）；默认进程内单例，测试可注入隔离实例。</summary>
    public ToolTaskQueue Queue { get; set; } = ToolTaskQueue.Shared;

    /// <summary>该入口是否已有排队或执行中的任务（CanExecute 派生，M-8；排队 ≠ 执行中）。</summary>
    public bool HasActiveTask => _activeTask is not null;

    /// <summary>该入口的任务是否正在排队（尚未开始执行）。</summary>
    public bool IsQueued => _activeTask?.State == ToolTaskState.Queued;

    public Tool Tool { get; }

    /// <summary>输入文件（顺序即传参顺序）。organize 等单文件工具至多一项。</summary>
    public ObservableCollection<PageFileItem> Files { get; } = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>是否支持多文件（决定文件选择语义与排序按钮可见性）。</summary>
    public bool SupportsMultipleFiles => Tool.Multiple;

    // ---------- 浮菜单「用工具处理」置顶/预填（V2 T2） ----------

    /// <summary>被浮菜单固定的当前文档路径（null = 无）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedDocument))]
    [NotifyPropertyChangedFor(nameof(PinnedDocumentLabel))]
    private string? _pinnedDocumentPath;

    /// <summary>是否有「当前文档」标记（XAML 徽标显隐）。</summary>
    public bool HasPinnedDocument => !string.IsNullOrEmpty(PinnedDocumentPath);

    /// <summary>「当前文档：文件名」标记文案。</summary>
    public string? PinnedDocumentLabel => HasPinnedDocument
        ? $"当前文档：{System.IO.Path.GetFileName(PinnedDocumentPath!)}"
        : null;

    /// <summary>是否有页范围字段可预填（基类默认无）。</summary>
    public virtual bool SupportsPageRangePrefill => false;

    /// <summary>预填页范围（基类默认空实现）。</summary>
    public virtual void PrefillPageRange(int startPage, int endPage)
    {
    }

    /// <summary>把当前文档加入并置顶、标记「当前文档」（浮菜单子菜单入口）。</summary>
    public void PinCurrentDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return; }

        AddFiles([fullPath]);

        var item = Files.FirstOrDefault(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            var index = Files.IndexOf(item);
            if (index > 0) Files.Move(index, 0);
        }

        PinnedDocumentPath = fullPath;
    }

    /// <summary>合并是引擎硬约束：至少两份 PDF。</summary>
    public bool NeedsAtLeastTwoFiles => Tool.Id == "merge";

    [ObservableProperty]
    private string _outputDir = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _progressMessage = "";

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _statusMessage = "";

    public ObservableCollection<PageResultItem> Results { get; } = [];

    /// <summary>最近一次引擎结果（测试断言用；业务状态以 Results/StatusMessage 为准）。</summary>
    public EngineResult? LastResult { get; private set; }

    public string FileDialogFilter { get; private set; } = "所有支持的文件|*.*";

    public bool CanExecuteNow => !HasActiveTask && Files.Count > 0 && (!NeedsAtLeastTwoFiles || Files.Count >= 2);

    // ---------- 命令 ----------

    /// <summary>
    /// 执行入口：校验通过后提交全局串行队列（FR-40），排队期间参数仍可编辑；队列轮到时才置 <see cref="IsRunning"/>（M-8）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteNow))]
    private async Task ExecuteAsync()
    {
        if (_activeTask is not null)
        {
            return; // 防重入：该入口已有排队/执行中的任务
        }

        var error = Validate();
        if (error is not null)
        {
            StatusMessage = error;
            return;
        }

        if (_bridge is null)
        {
            StatusMessage = "引擎未就绪：未配置执行桥。";
            return;
        }

        var item = Queue.Create(Tool.Name, RunAsync, this);
        AttachActiveTask(item);
        Queue.Enqueue(item);
        await item.Completion.Task;
        DetachActiveTask(item);
    }

    /// <summary>队列轮到时执行的实体（原 ExecuteAsync 主体）：进度、取消/异常处理与状态复位。</summary>
    private async Task RunAsync(CancellationToken token)
    {
        var request = new EngineRequest
        {
            Operation = Tool.Id,
            Files = [.. Files.Select(f => f.Path)],
            Options = new Dictionary<string, object?>(BuildOptions()),
            OutputDir = OutputDir.Trim(),
        };

        IsRunning = true;
        ProgressFraction = 0;
        ProgressMessage = "";
        StatusMessage = $"正在执行 {Tool.Name}…";
        Results.Clear();
        _bridge!.Progress += OnBridgeProgress;
        try
        {
            var result = await _bridge.ExecuteAsync(request, token);
            LastResult = result;
            if (result.Ok)
            {
                foreach (var output in result.Outputs)
                {
                    Results.Add(new PageResultItem(output));
                }

                StatusMessage = $"完成 {Tool.Name}：{result.Outputs.Count} 个文件 → {result.OutputDir}";
            }
            else
            {
                StatusMessage = $"失败：{result.Error}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消。";
        }
        catch (EngineBridgeException bridgeError)
        {
            StatusMessage = $"引擎异常：{bridgeError.Message}";
        }
        finally
        {
            _bridge.Progress -= OnBridgeProgress;
            IsRunning = false;
            ProgressMessage = "";
            ProgressFraction = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveTask))]
    private void Cancel() => Queue.Cancel(_activeTask);

    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"为{Tool.Name}选择文件",
            Filter = FileDialogFilter,
            Multiselect = SupportsMultipleFiles,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    /// <summary>上移一项（多文件工具的顺序调整；顺序即传参顺序）。</summary>
    [RelayCommand]
    private void MoveUp(PageFileItem? item) => Move(item, -1);

    /// <summary>下移一项。</summary>
    [RelayCommand]
    private void MoveDown(PageFileItem? item) => Move(item, +1);

    [RelayCommand]
    private void RemoveFile(PageFileItem? item)
    {
        if (item is not null && !IsRunning && Files.Remove(item))
        {
            OnPropertyChanged(nameof(HasFiles));
        }
    }

    // ---------- 文件输入 ----------

    /// <summary>
    /// 拖拽 / 浏览的统一入口。按工具扩展名过滤、去重；单文件工具以新文件替换旧文件。
    /// V2 T7：不再因执行/排队而静默丢弃——任务进行中仍可添加文件（用于下次任务）。
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var allowed = Tool.Extensions.Select(e => e.ToLowerInvariant()).ToHashSet();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path;
            try
            {
                path = System.IO.Path.GetFullPath(raw);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                continue; // 路径字符非法：忽略，交由用户重新选择
            }

            var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!allowed.Contains(extension))
            {
                continue; // 与引擎扩展名校验同源，越界文件不入列
            }

            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!SupportsMultipleFiles)
            {
                Files.Clear();
            }

            Files.Add(new PageFileItem(path));
            if (OutputDir.Trim().Length == 0)
            {
                // 默认输出目录 = 源文件旁（引擎在其下新建任务子目录，不覆盖源文件）
                OutputDir = System.IO.Path.GetDirectoryName(path) ?? "";
            }
        }

        OnPropertyChanged(nameof(HasFiles));
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFiles));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void Move(PageFileItem? item, int delta)
    {
        if (item is null || IsRunning)
        {
            return;
        }

        var index = Files.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Files.Count)
        {
            return;
        }

        Files.Move(index, target);
    }

    partial void OnIsRunningChanged(bool value) => RefreshCommandStates();

    /// <summary>绑定活动任务并订阅其状态变化（IsQueued 派生与命令刷新，M-8）。</summary>
    private void AttachActiveTask(ToolTaskItem item)
    {
        _activeTask = item;
        item.PropertyChanged += OnActiveTaskPropertyChanged;
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(IsQueued));
        RefreshCommandStates();
    }

    /// <summary>完成/取消后解除活动任务绑定。</summary>
    private void DetachActiveTask(ToolTaskItem item)
    {
        item.PropertyChanged -= OnActiveTaskPropertyChanged;
        _activeTask = null;
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(IsQueued));
        RefreshCommandStates();
    }

    private void OnActiveTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolTaskItem.State))
        {
            OnPropertyChanged(nameof(IsQueued));
            RefreshCommandStates();
        }
    }

    private void RefreshCommandStates()
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    // ---------- 参数与校验（子类实现） ----------

    /// <summary>表单状态 → 引擎 options。值一律为字符串（引擎把目录默认值与请求选项按字符串合并）。</summary>
    protected abstract Dictionary<string, object?> BuildOptions();

    /// <summary>
    /// 执行前校验。返回错误文案（不发请求）；null 放行。
    /// 顺序：文件 → 工具选项 → 输出目录（越基础的错误越先报）。
    /// 错误信息与引擎语义对齐，提前拦截省一次引擎往返。
    /// </summary>
    protected virtual string? Validate()
    {
        if (Files.Count == 0)
        {
            return "请先选择文件。";
        }

        if (!Tool.Multiple && Files.Count > 1)
        {
            return "此工具一次处理一份文件。";
        }

        // merge 是引擎硬约束（至少两份 PDF）；归入基类，向导与工具页共用（向导参数校验在 ValidateParameters）。
        if (NeedsAtLeastTwoFiles && Files.Count < 2)
        {
            return "请至少添加两份 PDF。";
        }

        if (ValidateOptions() is { } optionError)
        {
            return optionError;
        }

        var output = OutputDir.Trim();
        if (output.Length == 0 || !Path.IsPathRooted(output) || !Directory.Exists(output))
        {
            return "请选择有效的输出文件夹。";
        }

        return null;
    }

    /// <summary>子类选项校验（在输出目录检查之前执行）。</summary>
    protected virtual string? ValidateOptions() => null;

    // ---------- V3 T8 向导接入（IWizardToolAdapter 最小注入点） ----------

    /// <summary>参数步内联校验【ST-25】：按暂存文件复用既有校验语义（不读 VM 的 <see cref="Files"/>）。</summary>
    public string? ValidateParameters(IReadOnlyList<string> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);

        if (stagedFiles.Count == 0)
        {
            return "请先选择文件。";
        }

        if (NeedsAtLeastTwoFiles && stagedFiles.Count < 2)
        {
            return "请至少添加两份 PDF。";
        }

        if (WizardAdapterSupport.ResolveOutputDir(OutputDir, stagedFiles, out _) is { } outputError)
        {
            return outputError;
        }

        return ValidateOptions();
    }

    /// <summary>提交快照：提交时刻读取当前参数/输出目录并交给 <see cref="WizardSubmitItem"/> 固化（review M2）。</summary>
    public IReadOnlyList<WizardSubmitItem> BuildSubmitItems(IReadOnlyList<string> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);
        WizardAdapterSupport.ResolveOutputDir(OutputDir, stagedFiles, out var outputDir);
        return WizardAdapterSupport.BuildItems(Tool, stagedFiles, BuildOptions(), outputDir);
    }

    /// <summary>执行单个已固化快照项：直接调引擎，不回读 VM 可变状态、不触碰 UI 绑定。</summary>
    public async Task<WizardItemResult> ExecuteItemAsync(WizardSubmitItem item, CancellationToken token)
    {
        if (_bridge is null)
        {
            return WizardItemResult.Failure("引擎未就绪：未配置执行桥。");
        }

        var result = await _bridge.ExecuteAsync(WizardAdapterSupport.BuildRequest(Tool.Id, item), token)
            .ConfigureAwait(true);
        return WizardAdapterSupport.ToResult(result);
    }

    /// <summary>取目录字段默认值（catalog 权威）。</summary>
    protected static string FieldValue(Tool tool, string key)
        => tool.Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";

    /// <summary>校验页面范围（留空是否合法由子类决定）：, 或 ，分隔的 "数字" 或 "a-b"，镜像 parse_pages 的单项正则。</summary>
    protected static string? ValidatePagesSpec(string spec)
    {
        var normalized = spec.Replace('，', ',');
        var items = normalized.Split(',');
        foreach (var item in items)
        {
            if (item.Length > 0 && !PagesItemPattern.IsMatch(item))
            {
                return "页码格式不正确，请输入类似 1-3,5 的范围。";
            }
        }

        return null;
    }

    private void OnBridgeProgress(object? sender, EngineProgressEventArgs e)
    {
        // 进度事件来自引擎子进程的读循环线程；WPF 绑定要求 UI 线程，测试环境没有 Application。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ProgressMessage = e.Message;
            ProgressFraction = e.Fraction;
        }
        else
        {
            dispatcher.InvokeAsync(() =>
            {
                ProgressMessage = e.Message;
                ProgressFraction = e.Fraction;
            });
        }
    }

    private static string BuildFilter(Tool tool)
    {
        var extensions = tool.Extensions;
        var pattern = string.Join(";", extensions.Select(e => "*." + e.ToLowerInvariant()));
        var label = extensions.Contains("pdf", StringComparer.OrdinalIgnoreCase) ? "PDF 文件" : "图片文件";
        return $"{label}|{pattern}";
    }
}

/// <summary>合并 PDF：按文件列表顺序合并，顺序即传参顺序。</summary>
public sealed partial class MergeViewModel : PageOrganizerViewModelBase
{
    public MergeViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    protected override Dictionary<string, object?> BuildOptions() => new Dictionary<string, object?>();
}

/// <summary>拆分 PDF：按范围拆分（分号分组），留空逐页一份。</summary>
public sealed partial class SplitViewModel : PageOrganizerViewModelBase
{
    [ObservableProperty]
    private string _rangesText = "";

    public SplitViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _rangesText = FieldValue(tool, "ranges"); // catalog 默认值
    }

    protected override Dictionary<string, object?> BuildOptions()
        => new Dictionary<string, object?> { ["ranges"] = RangesText.Trim() };

    protected override string? ValidateOptions()
    {
        var normalized = RangesText.Trim().Replace('；', ';');
        foreach (var segment in normalized.Split(';'))
        {
            if (segment.Length > 0 && ValidatePagesSpec(segment) is { } itemError)
            {
                return itemError;
            }
        }

        return null;
    }

    /// <summary>浮菜单「拆分」有页范围字段（RangesText），可预填当前页（H-6）。</summary>
    public override bool SupportsPageRangePrefill => true;

    /// <summary>预填拆分范围为「startPage-endPage」（1 基）。</summary>
    public override void PrefillPageRange(int startPage, int endPage)
        => RangesText = $"{startPage}-{endPage}";
}

/// <summary>删除页面：pages 必填，原文件保持不变。</summary>
public sealed partial class RemoveViewModel : PageOrganizerViewModelBase
{
    [ObservableProperty]
    private string _pagesText = "";

    public RemoveViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _pagesText = FieldValue(tool, "pages");
    }

    protected override Dictionary<string, object?> BuildOptions()
        => new Dictionary<string, object?> { ["pages"] = PagesText.Trim() };

    protected override string? ValidateOptions()
    {
        if (PagesText.Trim().Length == 0)
        {
            return "请输入需要删除的页面。";
        }

        return ValidatePagesSpec(PagesText);
    }
}

/// <summary>提取页面：pages 留空为全部。</summary>
public sealed partial class ExtractViewModel : PageOrganizerViewModelBase
{
    [ObservableProperty]
    private string _pagesText = "";

    public ExtractViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _pagesText = FieldValue(tool, "pages");
    }

    protected override Dictionary<string, object?> BuildOptions()
        => new Dictionary<string, object?> { ["pages"] = PagesText.Trim() };

    protected override string? ValidateOptions()
        => PagesText.Trim().Length == 0 ? null : ValidatePagesSpec(PagesText);
}

/// <summary>排列页面：pages 顺序敏感、允许重复（如 3,1,2）；一次只处理一份。</summary>
public sealed partial class OrganizeViewModel : PageOrganizerViewModelBase
{
    [ObservableProperty]
    private string _pagesText = "";

    public OrganizeViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _pagesText = FieldValue(tool, "pages");
    }

    protected override Dictionary<string, object?> BuildOptions()
        => new Dictionary<string, object?> { ["pages"] = PagesText.Trim() };

    protected override string? ValidateOptions()
        => PagesText.Trim().Length == 0 ? null : ValidatePagesSpec(PagesText);
}

/// <summary>扫描为 PDF：扫描效果 + 自动识别纸张边缘；输入是图片。</summary>
public sealed partial class ScanViewModel : PageOrganizerViewModelBase
{
    public ScanViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        var modeField = tool.Fields.FirstOrDefault(f => f.Key == "scanMode");
        ScanModeChoices = modeField?.Choices is { Count: > 0 } choices ? choices : ["document", "gray", "original"];
        _scanMode = modeField?.Value is { Length: > 0 } value ? value : ScanModeChoices[0];

        // catalog toggle 默认值是字符串 "true"/"false"，与引擎 flag() 同源解析
        var perspectiveField = tool.Fields.FirstOrDefault(f => f.Key == "perspective");
        _perspective = string.Equals(perspectiveField?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ScanModeChoices { get; }

    [ObservableProperty]
    private string _scanMode;

    [ObservableProperty]
    private bool _perspective;

    protected override Dictionary<string, object?> BuildOptions()
        => new Dictionary<string, object?>
        {
            ["scanMode"] = ScanMode,
            ["perspective"] = Perspective ? "true" : "false",
        };
}
