// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Wizard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.Tools;

/// <summary>工作流程的一个步骤：目标工具 + 参数。与引擎 workflow 步骤形状一致（backend/engine.py 的 options.steps）。</summary>
public sealed class WorkflowStep
{
    public string Operation { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = [];
}

/// <summary>本机预设：某工具（默认 workflow）的参数组合快照。序列化前必须先 Sanitize 跳过密码类字段。</summary>
public sealed class WorkflowPreset
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Name { get; set; } = "";
    public string ToolId { get; set; } = "workflow";
    public List<WorkflowStep> Steps { get; set; } = [];

    /// <summary>密码类参数键一律不含于预设：键名含 password（大小写不敏感）即视为密码，如 password / newPassword。</summary>
    public static bool IsPasswordKey(string key)
        => key.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>返回去除全部密码类字段后的副本；不修改原对象。密码只存在于本次运行内存，绝不落盘。</summary>
    public static WorkflowPreset Sanitize(WorkflowPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var clean = new WorkflowPreset
        {
            Schema = preset.Schema,
            Name = preset.Name,
            ToolId = preset.ToolId,
        };
        foreach (var step in preset.Steps)
        {
            clean.Steps.Add(new WorkflowStep
            {
                Operation = step.Operation,
                Options = step.Options
                    .Where(pair => !IsPasswordKey(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
            });
        }

        return clean;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文按原字符写入（本地预设文件保持可读），不影响解析往返
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WorkflowPreset FromJson(string json)
    {
        var preset = JsonSerializer.Deserialize<WorkflowPreset>(json)
                     ?? throw new InvalidDataException("预设文件为空或格式不正确。");
        if (preset.Schema != CurrentSchema)
        {
            throw new InvalidDataException($"预设版本不支持（schema {preset.Schema}）。");
        }

        return preset;
    }
}

/// <summary>预设存储：本机用户目录 %APPDATA%\AIPDF\presets\（不入仓库、不上传）；测试可注入临时目录。</summary>
public sealed class WorkflowPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public WorkflowPresetStore(string? directory = null)
        => PresetDirectory = directory
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIPDF", "presets");

    public string PresetDirectory { get; }

    /// <summary>列出全部预设名（不含扩展名，按名称排序）；目录不存在时返回空列表。</summary>
    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(PresetDirectory))
        {
            return [];
        }

        return [.. Directory.GetFiles(PresetDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.CurrentCulture)];
    }

    /// <summary>保存预设：先 Sanitize 跳过密码字段，再写入 {名称}.json。</summary>
    public void Save(WorkflowPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset.Name);

        Directory.CreateDirectory(PresetDirectory);
        var clean = WorkflowPreset.Sanitize(preset);
        File.WriteAllText(FilePathFor(preset.Name), clean.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>按名称加载预设；文件不存在抛 FileNotFoundException，版本不支持抛 InvalidDataException。</summary>
    public WorkflowPreset Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = FilePathFor(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到预设：{name}", path);
        }

        return WorkflowPreset.FromJson(File.ReadAllText(path));
    }

    public void Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = FilePathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string FilePathFor(string name) => Path.Combine(PresetDirectory, SafeFileName(name) + ".json");

    /// <summary>预设名转安全文件名：非法字符替换为“-”（跨平台集合），并去除首尾空白。</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Trim().Length);
        foreach (var ch in name.Trim())
        {
            builder.Append(invalid.Contains(ch) ? '-' : ch);
        }

        return builder.Length == 0 ? "未命名预设" : builder.ToString();
    }
}

/// <summary>批量队列项状态机：待处理 → 进行中 → 完成 / 失败 / 已取消。</summary>
public enum BatchItemState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>批量队列中的一项：一组输入文件按当前工具顺序执行。逐项错误不中断批次。</summary>
public partial class BatchItem : ObservableObject
{
    public BatchItem(IReadOnlyList<string> files, string displayName)
    {
        Files = [.. files];
        DisplayName = displayName;
    }

    public IReadOnlyList<string> Files { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private BatchItemState _state = BatchItemState.Pending;

    [ObservableProperty]
    private string? _outputDir;

    [ObservableProperty]
    private string? _error;

    /// <summary>队列列表里显示的结果摘要：完成显示输出目录，失败显示错误，其余为空。</summary>
    public string ResultText => State switch
    {
        BatchItemState.Completed => OutputDir ?? "",
        BatchItemState.Failed => Error ?? "执行失败。",
        _ => "",
    };

    partial void OnStateChanged(BatchItemState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ResultText));
    }

    partial void OnOutputDirChanged(string? value) => OnPropertyChanged(nameof(ResultText));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(ResultText));

    /// <summary>状态的中文文案。</summary>
    public string StateText => State switch
    {
        BatchItemState.Pending => "待处理",
        BatchItemState.Running => "进行中",
        BatchItemState.Completed => "完成",
        BatchItemState.Failed => "失败",
        BatchItemState.Cancelled => "已取消",
        _ => "",
    };
}

/// <summary>按工具语义把文件分组为队列项：merge 把全部文件合成一次请求（引擎要求多份），
/// 其余工具每个文件一项（顺序逐个执行，遵守引擎单进程契约，批次从不并行）。</summary>
public static class BatchQueueBuilder
{
    public static IReadOnlyList<BatchItem> Build(Tool tool, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var list = files.Where(File.Exists).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        if (tool.Id == "merge")
        {
            var name = $"{list.Count} 个文件（{Path.GetFileName(list[0])} 等）";
            return [new BatchItem(list, name)];
        }

        return [.. list.Select(path => new BatchItem([path], Path.GetFileName(path)))];
    }
}

/// <summary>一次执行的记录：时间 / 工具 / 输入 / 输出 / 结果。会话内存保存，不持久化、不上传（无遥测约定）。</summary>
public sealed record WorkflowHistoryRecord(
    DateTime Time,
    string Tool,
    string Inputs,
    string Outputs,
    string Result,
    string Detail,
    double? ElapsedSeconds);

/// <summary>执行历史（会话内存）。Shared 供流程页、批量页与记录页共用同一个列表。</summary>
public sealed class WorkflowHistory
{
    public static WorkflowHistory Shared { get; } = new();

    public ObservableCollection<WorkflowHistoryRecord> Records { get; } = [];

    public void Add(WorkflowHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Records.Add(record);
    }

    public void Clear() => Records.Clear();

    /// <summary>
    /// 导出 CSV（UTF-8 带 BOM，Excel 打开中文不乱码）。RFC 4180：含逗号/引号/换行的字段加引号，内部引号翻倍。
    /// </summary>
    public void ExportCsv(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var builder = new StringBuilder();
        builder.Append("时间,工具,输入,输出,结果,详情,耗时（秒）\r\n");
        foreach (var record in Records)
        {
            builder.Append(Escape(record.Time.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                .Append(Escape(record.Tool)).Append(',')
                .Append(Escape(record.Inputs)).Append(',')
                .Append(Escape(record.Outputs)).Append(',')
                .Append(Escape(record.Result)).Append(',')
                .Append(Escape(record.Detail)).Append(',')
                .Append(record.ElapsedSeconds is { } seconds ? seconds.ToString("0.0", CultureInfo.InvariantCulture) : "")
                .Append("\r\n");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string? value)
    {
        var text = value ?? "";
        return text.ContainsAny(',', '"', '\r', '\n') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, params char[] chars) => value.IndexOfAny(chars) >= 0;
}

/// <summary>通用参数表单的一个字段：与 catalog 字段一一映射（key/label/value/kind/choices）。</summary>
public partial class OptionFieldViewModel : ObservableObject
{
    public OptionFieldViewModel(ToolField field)
    {
        Key = field.Key;
        Label = field.Label;
        Kind = field.Kind;
        Help = field.Help;
        Choices = field.Choices;
        _value = field.Value;
    }

    public string Key { get; }
    public string Label { get; }
    public string Kind { get; }
    public string Help { get; }
    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    private string _value;

    public bool IsText => Kind is "text" or "number";
    public bool IsMultiLine => Kind == "textarea";
    public bool IsChoice => Kind == "choice";
    public bool IsToggle => Kind == "toggle";
    public bool IsSecret => Kind == "secret";
    public bool IsFile => Kind == "file";
}

/// <summary>工作流程中的一个步骤（视图模型）：选择操作 + 按该工具的 catalog 字段生成参数表单。</summary>
public partial class WorkflowStepViewModel : ObservableObject
{
    private readonly IReadOnlyList<Tool> _allowedOperations;

    public WorkflowStepViewModel(IReadOnlyList<Tool> allowedOperations)
    {
        _allowedOperations = allowedOperations;
        SelectedOperation = allowedOperations.FirstOrDefault();
    }

    public IReadOnlyList<Tool> AllowedOperations => _allowedOperations;

    [ObservableProperty]
    private Tool? _selectedOperation;

    [ObservableProperty]
    private int _index;

    public string Header => $"步骤 {Index}";

    public ObservableCollection<OptionFieldViewModel> Options { get; } = [];

    partial void OnSelectedOperationChanged(Tool? value) => RebuildOptions();

    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(Header));

    /// <summary>按所选工具的 catalog 字段重建参数表单（保留默认值）。</summary>
    public void RebuildOptions()
    {
        Options.Clear();
        if (SelectedOperation is null)
        {
            return;
        }

        foreach (var field in SelectedOperation.Fields)
        {
            Options.Add(new OptionFieldViewModel(field));
        }
    }

    /// <summary>从预设步骤恢复：设置操作并把非密码参数值套到对应字段。</summary>
    public void ApplyStep(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        SelectedOperation = _allowedOperations.FirstOrDefault(tool => tool.Id == step.Operation);
        RebuildOptions();
        foreach (var (key, value) in step.Options)
        {
            if (WorkflowPreset.IsPasswordKey(key))
            {
                continue; // 预设不含密码字段：加载时同样跳过
            }

            if (Options.FirstOrDefault(field => field.Key == key) is { } field)
            {
                field.Value = value;
            }
        }
    }

    /// <summary>表单状态 → 引擎步骤参数（含未改动的默认值；密码类字段保留在内存请求中，不进预设）。</summary>
    public Dictionary<string, string> BuildOptions()
        => Options.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
}

/// <summary>流程工具页（catalog 工具 workflow）：步骤串联 + 本机预设 + 真引擎执行。</summary>
public partial class WorkflowToolViewModel : ObservableObject, IWizardToolAdapter
{
    private readonly EngineBridge? _bridge;
    private readonly WorkflowPresetStore _presets;
    private readonly WorkflowHistory _history;
    private ToolTaskItem? _activeTask;

    public const string OperationId = "workflow";

    /// <summary>引擎 workflow 允许的步骤工具（backend/engine.py 的 allowed 集合，按目录顺序呈现）。</summary>
    private static readonly HashSet<string> EngineAllowedSteps = new(
    [
        "merge", "split", "extract", "remove", "organize", "compress", "repair", "ocr",
        "rotate", "numbers", "watermark", "crop", "unlock", "protect", "redact",
    ]);

    public WorkflowToolViewModel(
        EngineBridge? bridge,
        IReadOnlyList<Tool> catalogTools,
        WorkflowPresetStore? presetStore = null,
        WorkflowHistory? history = null,
        Tool? catalogTool = null)
    {
        _bridge = bridge;
        _presets = presetStore ?? new WorkflowPresetStore();
        _history = history ?? WorkflowHistory.Shared;
        CatalogTool = catalogTool;
        AllowedOperations = [.. catalogTools.Where(tool => EngineAllowedSteps.Contains(tool.Id))];
        RefreshPresets(selectFirst: false);
    }

    /// <summary>目录条目（向导接入需要 Name/Multiple）；由宿主页面在构造时注入，测试可为 null。</summary>
    public Tool? CatalogTool { get; }

    public IReadOnlyList<Tool> AllowedOperations { get; }

    public ObservableCollection<string> Files { get; } = [];

    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = [];

    public ObservableCollection<string> AvailablePresets { get; } = [];

    [ObservableProperty]
    private string _outputDir = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _presetName = "";

    [ObservableProperty]
    private string? _selectedPreset;

    [ObservableProperty]
    private string _statusText = "把多个 PDF 操作按顺序串联，一次完成处理。";

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>全局串行任务队列（V2 T7）；默认进程内单例，测试可注入隔离实例。</summary>
    public ToolTaskQueue Queue { get; set; } = ToolTaskQueue.Shared;

    /// <summary>该入口是否已有排队或执行中的任务（CanExecute 派生，M-8）。</summary>
    public bool HasActiveTask => _activeTask is not null;

    /// <summary>该入口的任务是否正在排队（尚未执行）。</summary>
    public bool IsQueued => _activeTask?.State == ToolTaskState.Queued;

    // ---------- 文件与步骤 ----------

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path) && !Files.Contains(path))
            {
                Files.Add(path);
            }
        }

        if (Files.Count > 0 && string.IsNullOrWhiteSpace(OutputDir))
        {
            OutputDir = Path.GetDirectoryName(Files[0]) ?? "";
        }
    }

    public void ClearFiles() => Files.Clear();

    [RelayCommand]
    private void AddStep() => AddStepInternal();

    public void AddStepInternal()
    {
        var step = new WorkflowStepViewModel(AllowedOperations);
        step.Index = Steps.Count + 1;
        Steps.Add(step);
    }

    [RelayCommand]
    private void MoveStepUp(object? parameter) => MoveStep(parameter, -1);

    [RelayCommand]
    private void MoveStepDown(object? parameter) => MoveStep(parameter, 1);

    private void MoveStep(object? parameter, int delta)
    {
        if (parameter is not WorkflowStepViewModel step)
        {
            return;
        }

        var index = Steps.IndexOf(step);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Steps.Count)
        {
            return;
        }

        Steps.Move(index, target);
        RenumberSteps();
    }

    [RelayCommand]
    private void RemoveStep(object? parameter)
    {
        if (parameter is WorkflowStepViewModel step)
        {
            Steps.Remove(step);
            RenumberSteps();
        }
    }

    private void RenumberSteps()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Index = i + 1;
        }
    }

    // ---------- 预设 ----------

    private void RefreshPresets(bool selectFirst)
    {
        var names = _presets.ListNames();
        AvailablePresets.Clear();
        foreach (var name in names)
        {
            AvailablePresets.Add(name);
        }

        SelectedPreset = selectFirst && AvailablePresets.Count > 0 ? AvailablePresets[0] : SelectedPreset;
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            StatusText = "请输入预设名称再保存。";
            return;
        }

        if (Steps.Count == 0)
        {
            StatusText = "请至少添加一个步骤后再保存预设。";
            return;
        }

        _presets.Save(new WorkflowPreset
        {
            Name = name,
            ToolId = OperationId,
            Steps = [.. Steps.Select(step => new WorkflowStep
            {
                Operation = step.SelectedOperation?.Id ?? "",
                Options = step.BuildOptions(),
            })],
        });
        RefreshPresets(selectFirst: false);
        SelectedPreset = name;
        StatusText = $"已保存预设「{name}」（密码字段已跳过）。";
    }

    [RelayCommand]
    private void LoadPreset()
    {
        if (SelectedPreset is null)
        {
            StatusText = "请先选择要加载的预设。";
            return;
        }

        try
        {
            var preset = _presets.Load(SelectedPreset);
            Steps.Clear();
            foreach (var step in preset.Steps)
            {
                var stepViewModel = new WorkflowStepViewModel(AllowedOperations);
                stepViewModel.ApplyStep(step);
                Steps.Add(stepViewModel);
            }

            RenumberSteps();
            StatusText = $"已加载预设「{preset.Name}」，共 {Steps.Count} 步。";
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException or JsonException)
        {
            StatusText = $"预设加载失败：{error.Message}";
        }
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null)
        {
            StatusText = "请先选择要删除的预设。";
            return;
        }

        _presets.Delete(SelectedPreset);
        RefreshPresets(selectFirst: false);
        StatusText = $"已删除预设「{SelectedPreset}」。";
    }

    // ---------- 构建请求与执行 ----------

    /// <summary>表单状态 → 引擎请求。steps 形状与 backend/engine.py 的 workflow 分支逐字段一致。</summary>
    /// <param name="files">可选文件集覆盖；向导提交用暂存文件（不读 VM 的 <see cref="Files"/>）。</param>
    internal EngineRequest BuildRequest(IReadOnlyList<string>? files = null)
    {
        var options = new Dictionary<string, object?>();
        options["steps"] = Steps
            .Select(step => (object?)new Dictionary<string, object?>
            {
                ["operation"] = step.SelectedOperation?.Id ?? "",
                ["options"] = step.BuildOptions(),
            })
            .ToList();
        if (Password.Length > 0)
        {
            // 密码仅存在于本次运行内存（引擎经 stdin 收取），永不进入预设或日志。
            options["password"] = Password;
        }

        return new EngineRequest
        {
            Operation = OperationId,
            Files = [.. (files ?? Files)],
            Options = options,
            OutputDir = OutputDir,
        };
    }

    // ---------- V3 T8 向导接入（IWizardToolAdapter 最小注入点） ----------

    /// <summary>workflow 多步例外（Q2）：额外参数步标题；每步复用本适配器的参数校验。</summary>
    public IReadOnlyList<string> AdditionalStepTitles() => ["选择预设", "步骤参数"];

    /// <summary>参数步内联校验【ST-25】：按暂存文件复用步骤/输出目录校验（不读 VM 的 <see cref="Files"/>）。</summary>
    public string? ValidateParameters(IReadOnlyList<string> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);

        if (stagedFiles.Count == 0)
        {
            return "请先添加文件。";
        }

        if (Steps.Count == 0)
        {
            return "请至少添加一个工作流程步骤。";
        }

        if (Steps.Count > 20)
        {
            return "每个流程最多 20 步。";
        }

        return WizardAdapterSupport.ResolveOutputDir(OutputDir, stagedFiles, out _);
    }

    /// <summary>提交快照：提交时刻固化 steps/password/输出目录（review M2；密码仅内存、不落盘）。</summary>
    public IReadOnlyList<WizardSubmitItem> BuildSubmitItems(IReadOnlyList<string> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);
        WizardAdapterSupport.ResolveOutputDir(OutputDir, stagedFiles, out var outputDir);
        var tool = CatalogTool ?? new Tool { Id = OperationId, Name = "工作流程", Multiple = true };
        return WizardAdapterSupport.BuildItems(tool, stagedFiles, BuildRequest(stagedFiles).Options, outputDir);
    }

    /// <summary>执行单个已固化快照项：直接调引擎，不回读 VM 可变状态、不触碰 UI 绑定。</summary>
    public async Task<WizardItemResult> ExecuteItemAsync(WizardSubmitItem item, CancellationToken token)
    {
        if (_bridge is null)
        {
            return WizardItemResult.Failure("引擎未就绪：未配置执行桥。");
        }

        var result = await _bridge.ExecuteAsync(WizardAdapterSupport.BuildRequest(OperationId, item), token)
            .ConfigureAwait(true);
        return WizardAdapterSupport.ToResult(result);
    }

    /// <summary>返回 null 表示通过；否则为用户可读的校验提示（不发引擎请求）。</summary>
    public string? Validate()
    {
        if (Files.Count == 0)
        {
            return "请先添加文件。";
        }

        if (Steps.Count == 0)
        {
            return "请至少添加一个工作流程步骤。";
        }

        if (Steps.Count > 20)
        {
            return "每个流程最多 20 步。";
        }

        if (string.IsNullOrWhiteSpace(OutputDir)
            || !Path.IsPathRooted(OutputDir)
            || !Directory.Exists(OutputDir))
        {
            return "请选择有效的输出文件夹。";
        }

        return null;
    }

    /// <summary>取消该入口排队/执行中的任务（执行中由引擎桥 taskkill 子树，半成品保留）。</summary>
    public void Cancel() => Queue.Cancel(_activeTask);

    /// <summary>
    /// 执行入口：校验通过后提交全局串行队列（FR-40），排队期间参数仍可编辑；队列轮到时才置 <see cref="IsRunning"/>（M-8）。
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (_activeTask is not null)
        {
            return; // 防重入：该入口已有排队/执行中的任务
        }

        if (_bridge is null)
        {
            StatusText = "引擎未就绪：未配置执行桥。";
            return;
        }

        if (Validate() is { } validationError)
        {
            StatusText = validationError;
            return;
        }

        var item = Queue.Create("工作流程", RunAsync, this);
        AttachActiveTask(item);
        Queue.Enqueue(item);
        await item.Completion.Task;
        DetachActiveTask(item);
    }

    /// <summary>队列轮到时执行的实体（原 ExecuteAsync 主体）：进度、取消/异常处理与状态复位。</summary>
    private async Task RunAsync(CancellationToken token)
    {
        IsRunning = true;
        StatusText = "正在执行 工作流程…";
        var started = DateTime.Now;

        void OnProgress(object? sender, EngineProgressEventArgs args) => SetStatus(args.Message);

        _bridge!.Progress += OnProgress;
        try
        {
            var result = await _bridge.ExecuteAsync(BuildRequest(), token);
            if (result.Ok)
            {
                StatusText = $"完成 工作流程：{result.Outputs.Count} 个文件 → {result.OutputDir}";
                _history.Add(new WorkflowHistoryRecord(
                    DateTime.Now, "工作流程", DescribeInputs(Files),
                    result.OutputDir ?? "", "成功", "", DateTime.Now.Subtract(started).TotalSeconds));
            }
            else
            {
                StatusText = $"失败：{result.Error}";
                _history.Add(new WorkflowHistoryRecord(
                    DateTime.Now, "工作流程", DescribeInputs(Files),
                    "", "失败", result.Error ?? "", DateTime.Now.Subtract(started).TotalSeconds));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
            _history.Add(new WorkflowHistoryRecord(
                DateTime.Now, "工作流程", DescribeInputs(Files), "", "已取消", "", null));
        }
        catch (EngineBridgeException error)
        {
            StatusText = $"引擎异常：{error.Message}";
            _history.Add(new WorkflowHistoryRecord(
                DateTime.Now, "工作流程", DescribeInputs(Files), "", "失败", error.Message, null));
        }
        finally
        {
            _bridge.Progress -= OnProgress;
            IsRunning = false;
        }
    }

    /// <summary>绑定活动任务并订阅其状态变化（IsQueued 派生，M-8）。</summary>
    private void AttachActiveTask(ToolTaskItem item)
    {
        _activeTask = item;
        item.PropertyChanged += OnActiveTaskPropertyChanged;
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(IsQueued));
    }

    /// <summary>完成/取消后解除活动任务绑定。</summary>
    private void DetachActiveTask(ToolTaskItem item)
    {
        item.PropertyChanged -= OnActiveTaskPropertyChanged;
        _activeTask = null;
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(IsQueued));
    }

    private void OnActiveTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolTaskItem.State))
        {
            OnPropertyChanged(nameof(IsQueued));
        }
    }

    internal static string DescribeInputs(IEnumerable<string> paths)
        => string.Join("; ", paths.Select(Path.GetFileName));

    private void SetStatus(string message)
    {
        // 进度事件来自引擎 stderr 读取线程；应用内经 Dispatcher 回 UI 线程（测试环境无 Application，直接赋值）。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            StatusText = message;
        }
        else
        {
            dispatcher.InvokeAsync(() => StatusText = message);
        }
    }
}

/// <summary>批量处理页：文件队列 → 顺序执行 → 队列状态列表。逐项错误不中断；批次=顺序，从不并行。</summary>
public partial class BatchToolViewModel : ObservableObject
{
    private readonly EngineBridge? _bridge;
    private readonly WorkflowHistory _history;
    private ToolTaskItem? _activeTask;

    public BatchToolViewModel(EngineBridge? bridge, IReadOnlyList<Tool> catalogTools, WorkflowHistory? history = null)
    {
        _bridge = bridge;
        _history = history ?? WorkflowHistory.Shared;
        // 工作流程本身不进批量（批量=单工具逐文件执行；多步流程请在"流程步骤"页使用）
        Tools = [.. catalogTools.Where(tool => tool.Id != WorkflowToolViewModel.OperationId)];
        SelectedTool = Tools.FirstOrDefault();
    }

    /// <summary>全局串行任务队列（V2 T7）；默认进程内单例，测试可注入隔离实例。</summary>
    public ToolTaskQueue TaskQueue { get; set; } = ToolTaskQueue.Shared;

    /// <summary>该入口是否已有排队或执行中的任务（CanExecute 派生，M-8）。</summary>
    public bool HasActiveTask => _activeTask is not null;

    /// <summary>该入口的任务是否正在排队（尚未执行）。</summary>
    public bool IsQueued => _activeTask?.State == ToolTaskState.Queued;

    public IReadOnlyList<Tool> Tools { get; }

    public ObservableCollection<BatchItem> Queue { get; } = [];

    public ObservableCollection<OptionFieldViewModel> Options { get; } = [];

    [ObservableProperty]
    private Tool? _selectedTool;

    [ObservableProperty]
    private string _outputDir = "";

    [ObservableProperty]
    private string _statusText = "选择工具、添加文件，按队列顺序逐个处理。";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private bool _isRunning;

    partial void OnSelectedToolChanged(Tool? value) => RebuildOptions();

    private void RebuildOptions()
    {
        Options.Clear();
        if (SelectedTool is null)
        {
            return;
        }

        foreach (var field in SelectedTool.Fields)
        {
            Options.Add(new OptionFieldViewModel(field));
        }
    }

    /// <summary>
    /// 把文件按工具语义分组加入队列（merge 合成一项，其余逐文件一项）。
    /// V2 T7：批量是「一批一次执行」语义，执行/排队中不能重建队列——显式提示而非静默丢弃。
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        if (HasActiveTask)
        {
            StatusText = "批量任务排队/执行中，暂不能修改文件队列；请等待完成或取消后再添加。";
            return;
        }

        if (SelectedTool is null)
        {
            StatusText = "请先选择要批量执行的工具。";
            return;
        }

        var items = BatchQueueBuilder.Build(SelectedTool, paths);
        if (items.Count == 0)
        {
            return;
        }

        Queue.Clear();
        foreach (var item in items)
        {
            Queue.Add(item);
        }

        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            OutputDir = Path.GetDirectoryName(paths.First(File.Exists)) ?? "";
        }

        StatusText = $"队列已更新：{Queue.Count} 项。";
    }

    public void ClearQueue()
    {
        // 排队/执行中清空会使在途批次与列表不一致：保持禁用（与 AddFiles 同口径）。
        if (!HasActiveTask)
        {
            Queue.Clear();
            StatusText = "队列已清空。";
        }
    }

    internal EngineRequest BuildRequestFor(BatchItem item) => new()
    {
        Operation = SelectedTool?.Id ?? "",
        Files = [.. item.Files],
        Options = Options.ToDictionary(field => field.Key, field => (object?)field.Value, StringComparer.Ordinal),
        OutputDir = OutputDir,
    };

    public string? Validate()
    {
        if (SelectedTool is null)
        {
            return "请先选择要批量执行的工具。";
        }

        if (Queue.Count == 0)
        {
            return "请先添加文件。";
        }

        if (string.IsNullOrWhiteSpace(OutputDir)
            || !Path.IsPathRooted(OutputDir)
            || !Directory.Exists(OutputDir))
        {
            return "请选择有效的输出文件夹。";
        }

        return null;
    }

    /// <summary>取消该入口排队/执行中的批次任务（执行中由引擎桥 taskkill 子树）。</summary>
    public void Cancel() => TaskQueue.Cancel(_activeTask);

    /// <summary>
    /// 执行入口：校验通过后提交全局串行队列（FR-40），排队期间参数仍可编辑；队列轮到时才置 <see cref="IsRunning"/>（M-8）。
    /// </summary>
    public async Task RunAsync()
    {
        if (_activeTask is not null)
        {
            return; // 防重入：该入口已有排队/执行中的任务
        }

        if (_bridge is null)
        {
            StatusText = "引擎未就绪：未配置执行桥。";
            return;
        }

        if (Validate() is { } validationError)
        {
            StatusText = validationError;
            return;
        }

        var item = TaskQueue.Create($"{SelectedTool!.Name}（批量）", RunBatchAsync, this);
        AttachActiveTask(item);
        TaskQueue.Enqueue(item);
        await item.Completion.Task;
        DetachActiveTask(item);
    }

    /// <summary>
    /// 队列轮到时执行的实体（原 RunAsync 主体）：逐项请求引擎；单项失败（Ok=false 或桥异常）记为失败并继续下一项；
    /// 取消批次 → 当前项停止（Cancelled），剩余待处理项全部标为已取消。全程串行，遵守引擎单进程契约。
    /// </summary>
    private async Task RunBatchAsync(CancellationToken token)
    {
        IsRunning = true;
        var total = Queue.Count;
        var succeeded = 0;
        var failed = 0;

        void OnProgress(object? sender, EngineProgressEventArgs args) => SetStatus(args.Message);

        _bridge!.Progress += OnProgress;
        try
        {
            for (var i = 0; i < Queue.Count; i++)
            {
                var item = Queue[i];
                item.State = BatchItemState.Running;
                item.Error = null;
                item.OutputDir = null;
                ProgressText = $"第 {i + 1}/{total} 项";
                var started = DateTime.Now;
                try
                {
                    var result = await _bridge.ExecuteAsync(BuildRequestFor(item), token);
                    if (result.Ok)
                    {
                        item.State = BatchItemState.Completed;
                        item.OutputDir = result.OutputDir;
                        succeeded++;
                        _history.Add(new WorkflowHistoryRecord(
                            DateTime.Now, $"{SelectedTool!.Name}（批量）", WorkflowToolViewModel.DescribeInputs(item.Files),
                            result.OutputDir ?? "", "成功", $"{result.Outputs.Count} 个输出文件",
                            DateTime.Now.Subtract(started).TotalSeconds));
                    }
                    else
                    {
                        item.State = BatchItemState.Failed;
                        item.Error = result.Error;
                        failed++;
                        _history.Add(new WorkflowHistoryRecord(
                            DateTime.Now, $"{SelectedTool!.Name}（批量）", WorkflowToolViewModel.DescribeInputs(item.Files),
                            "", "失败", result.Error ?? "", DateTime.Now.Subtract(started).TotalSeconds));
                    }
                }
                catch (OperationCanceledException)
                {
                    item.State = BatchItemState.Cancelled;
                    _history.Add(new WorkflowHistoryRecord(
                        DateTime.Now, $"{SelectedTool!.Name}（批量）", WorkflowToolViewModel.DescribeInputs(item.Files),
                        "", "已取消", "", null));
                    // 取消批次：当前项已停止，剩余待处理项全部标为已取消，不再发起引擎请求。
                    var remaining = 0;
                    for (var j = i + 1; j < Queue.Count; j++)
                    {
                        if (Queue[j].State == BatchItemState.Pending)
                        {
                            Queue[j].State = BatchItemState.Cancelled;
                            remaining++;
                        }
                    }

                    StatusText = $"批次已取消：{succeeded} 成功，{failed} 失败，{remaining} 项已取消。";
                    return;
                }
                catch (EngineBridgeException error)
                {
                    item.State = BatchItemState.Failed;
                    item.Error = error.Message;
                    failed++;
                    _history.Add(new WorkflowHistoryRecord(
                        DateTime.Now, $"{SelectedTool!.Name}（批量）", WorkflowToolViewModel.DescribeInputs(item.Files),
                        "", "失败", error.Message, null));
                }
            }

            StatusText = $"批量完成：{succeeded} 成功，{failed} 失败。";
        }
        finally
        {
            _bridge.Progress -= OnProgress;
            IsRunning = false;
            ProgressText = "";
        }
    }

    /// <summary>绑定活动任务并订阅其状态变化（IsQueued 派生，M-8）。</summary>
    private void AttachActiveTask(ToolTaskItem item)
    {
        _activeTask = item;
        item.PropertyChanged += OnActiveTaskPropertyChanged;
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(IsQueued));
    }

    /// <summary>完成/取消后解除活动任务绑定。</summary>
    private void DetachActiveTask(ToolTaskItem item)
    {
        item.PropertyChanged -= OnActiveTaskPropertyChanged;
        _activeTask = null;
        OnPropertyChanged(nameof(HasActiveTask));
        OnPropertyChanged(nameof(IsQueued));
    }

    private void OnActiveTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolTaskItem.State))
        {
            OnPropertyChanged(nameof(IsQueued));
        }
    }

    private void SetStatus(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            StatusText = message;
        }
        else
        {
            dispatcher.InvokeAsync(() => StatusText = message);
        }
    }
}

/// <summary>处理记录页：会话内存历史 + CSV 导出。不持久化、不上传（无遥测约定）。</summary>
public partial class HistoryToolViewModel : ObservableObject
{
    private readonly WorkflowHistory _history;

    public HistoryToolViewModel(WorkflowHistory? history = null)
        => _history = history ?? WorkflowHistory.Shared;

    public ObservableCollection<WorkflowHistoryRecord> Records => _history.Records;

    [ObservableProperty]
    private string _statusText = "记录仅保存在本次会话内存中，不上传、不落盘。";

    /// <summary>导出 CSV；返回导出条数（调用方负责弹出保存对话框并回显路径）。</summary>
    public int ExportCsv(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var count = Records.Count;
        _history.ExportCsv(path);
        StatusText = $"已导出 {count} 条记录 → {path}";
        return count;
    }

    [RelayCommand]
    private void Clear()
    {
        var count = Records.Count;
        _history.Clear();
        StatusText = count > 0 ? $"已清空 {count} 条记录。" : StatusText;
    }
}
