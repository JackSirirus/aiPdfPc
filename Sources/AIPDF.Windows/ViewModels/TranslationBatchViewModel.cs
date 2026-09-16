// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 批量翻译工具页视图模型（纯逻辑，无 WPF 桌面依赖，xunit 可直接覆盖）：
//   - 输入：拖拽 / 多选文件列表（仅 PDF，去重）；
//   - 配置：复用 T13 共享面板 TranslationConfigViewModel（语言 / 模板 / 引擎 / 输出 / 成本 / 回填策略）；
//   - 执行：文件之间【串行】调用引擎 translate op（避免资源争用），UI 不阻塞；
//   - 结果：逐项状态 + 失败可单独重试 / 重试全部失败项 + 汇总计数；
//   - 打开：结果列表【按需打开】，绝不自动开标签（避免 20×2 标签爆炸）。
// 设计依据：设计/v1.1-翻译/翻译-设计稿.html F6 / F6b、翻译-全状态设计参考.html T18 / T19。
// 本类不写 AppSettings / 不落盘密钥；密钥只从运行时配置经 options → stdin 传递，绝不进日志。
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tools;
using AIPDF.Windows.ViewModels.Translation;
using AIPDF.Windows.Views.Wizard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AIPDF.Windows.ViewModels;

/// <summary>批量文件项状态（逐项展示：待处理 / 翻译中 / 完成 / 失败 / 已跳过）。</summary>
public enum BatchFileStatus
{
    /// <summary>待处理。</summary>
    Pending,

    /// <summary>翻译中。</summary>
    Running,

    /// <summary>已完成（已生成 mono / dual）。</summary>
    Done,

    /// <summary>失败（可重试）。</summary>
    Failed,

    /// <summary>用户跳过。</summary>
    Skipped,
}

/// <summary>
/// 批量翻译的单个文件条目：路径 + 逐项状态 + 产物（mono / dual）+ 失败原因。
/// 只保存路径与结果，不读文件内容、不落盘任何状态。
/// </summary>
public sealed partial class BatchTranslationFileItem : ObservableObject
{
    public BatchTranslationFileItem(string path) => Path = path;

    /// <summary>规范化后的完整路径（顺序即处理顺序）。</summary>
    public string Path { get; }

    /// <summary>文件名（列表展示）。</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private BatchFileStatus _status = BatchFileStatus.Pending;

    /// <summary>状态文案（列表徽标）。</summary>
    [ObservableProperty]
    private string _statusText = "待处理";

    /// <summary>纯译文产物路径（成功时非空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMono))]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _monoPath;

    /// <summary>对照产物路径（成功时非空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDual))]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _dualPath;

    /// <summary>降级页数（引擎 report；0 表示无降级）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private int _degradedPages;

    /// <summary>降级原因的用户可见文案（去重保序；复用 <see cref="FullTranslationViewModel.DescribeReason"/>）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private IReadOnlyList<string> _degradationLabels = [];

    /// <summary>溢出页数（引擎 <c>results[].overflow.pages</c> 聚合；0 表示无）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private int _overflowPages;

    /// <summary>其中被裁切的页数（引擎 <c>results[].overflow.clipPages</c> 聚合；0 表示无）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private int _clippedPages;

    /// <summary>失败原因（引擎原文，可直接展示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _error;

    /// <summary>待处理。</summary>
    public bool IsPending => Status == BatchFileStatus.Pending;

    /// <summary>翻译中。</summary>
    public bool IsRunning => Status == BatchFileStatus.Running;

    /// <summary>已完成。</summary>
    public bool IsDone => Status == BatchFileStatus.Done;

    /// <summary>失败（展示错误配色 + 提供重试）。</summary>
    public bool IsFailed => Status == BatchFileStatus.Failed;

    /// <summary>已跳过。</summary>
    public bool IsSkipped => Status == BatchFileStatus.Skipped;

    /// <summary>是否可重试（仅失败项）。</summary>
    public bool CanRetry => Status == BatchFileStatus.Failed;

    /// <summary>是否有纯译文产物。</summary>
    public bool HasMono => !string.IsNullOrEmpty(MonoPath);

    /// <summary>是否有对照产物。</summary>
    public bool HasDual => !string.IsNullOrEmpty(DualPath);

    /// <summary>结果说明行（成功：产物 + 逐项降级原因 + 溢出/裁切；失败：原因）。</summary>
    public string ResultText => IsDone
        ? BuildDoneText()
        : IsFailed ? $"失败：{Error}" : "";

    /// <summary>成功结果文案：产物 + 降级页数与逐项原因 + 溢出/裁切计数（缺失项不展示，不伪造）。</summary>
    private string BuildDoneText()
    {
        var parts = new List<string> { "已生成 mono + dual" };
        if (DegradedPages > 0)
        {
            parts.Add(DegradationLabels.Count > 0
                ? $"{DegradedPages} 页降级（{string.Join("、", DegradationLabels)}）"
                : $"{DegradedPages} 页降级");
        }

        if (OverflowPages > 0) parts.Add($"{OverflowPages} 页译文超出范围");
        if (ClippedPages > 0) parts.Add($"{ClippedPages} 页被裁切");
        return string.Join(" · ", parts);
    }

    /// <summary>是否有结果说明（结果行显隐）。</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>进入翻译中：清空上一轮结果并更新文案。</summary>
    public void BeginRunning()
    {
        Status = BatchFileStatus.Running;
        StatusText = "翻译中…";
        Error = null;
        MonoPath = null;
        DualPath = null;
        DegradedPages = 0;
        DegradationLabels = [];
        OverflowPages = 0;
        ClippedPages = 0;
    }

    /// <summary>翻译中进度文案（文件内进度）。</summary>
    public void ReportProgress(double fraction)
        => StatusText = $"翻译中 {Math.Clamp(fraction, 0, 1) * 100:0}%";

    /// <summary>完成：记录 mono / dual、逐项降级原因与溢出/裁切聚合（R5-7）。</summary>
    public void Complete(
        string? mono,
        string? dual,
        IReadOnlyList<FullTranslationPageIssue> pageIssues,
        FullTranslationOverflowSummary? overflow)
    {
        MonoPath = mono;
        DualPath = dual;
        DegradedPages = pageIssues.Count;
        DegradationLabels = [.. pageIssues
            .SelectMany(issue => issue.Reasons)
            .Select(FullTranslationViewModel.DescribeReason)
            .Distinct(StringComparer.Ordinal)];
        OverflowPages = overflow?.OverflowPages ?? 0;
        ClippedPages = overflow?.ClippedPages ?? 0;
        Error = null;
        Status = BatchFileStatus.Done;
        StatusText = "完成";
    }

    /// <summary>失败：记录可读原因（引擎原文）。</summary>
    public void Fail(string? error)
    {
        Error = string.IsNullOrWhiteSpace(error) ? "翻译失败，请稍后重试。" : error;
        Status = BatchFileStatus.Failed;
        StatusText = "失败";
    }

    /// <summary>用户跳过。</summary>
    public void Skip()
    {
        Status = BatchFileStatus.Skipped;
        StatusText = "已跳过";
    }

    /// <summary>取消后回到待处理（不残留「翻译中」）。</summary>
    public void ResetToPending()
    {
        Status = BatchFileStatus.Pending;
        StatusText = "待处理";
    }
}

/// <summary>
/// 批量翻译工具页视图模型（v1.1 T21）。自包含：文件输入 + 共享配置 + 串行执行 + 逐项结果/重试。
/// 同时实现 <see cref="IWizardToolAdapter"/>，使向导通道（<c>translate_batch</c>）的参数步可复用本页。
/// </summary>
public sealed partial class TranslationBatchViewModel : ObservableObject, IToolDocumentReceiver, ICurrentDocumentPinnable, IToolStartGated, IWizardToolAdapter
{
    /// <summary>翻译引擎 operation（catalog id 为 translate_batch，引擎 dispatch 的 op 为 translate）。</summary>
    public const string TranslateOperation = "translate";

    /// <summary>单次批量建议文件上限（超出仅提示，不阻断）。</summary>
    public const int RecommendedFileLimit = 20;

    /// <summary>云端同意被撤销时停止在途批量的状态文案（F21）。</summary>
    internal const string ConsentRevokedMessage = "已取消：云端同意已被撤销，批量翻译已停止，未采用半成品。";

    private readonly EngineBridge? _bridge;
    private readonly ITranslationRuntimeConfigProvider _configProvider;
    private CancellationTokenSource? _cts;
    private Task? _consentWatch;
    private volatile bool _consentRevoked;
    private int _totalFiles;
    private int _completedFiles;
    private double _currentFileFraction;

    public TranslationBatchViewModel(
        EngineBridge? bridge,
        Tool tool,
        ITranslationRuntimeConfigProvider? configProvider = null,
        TranslationConfigViewModel? config = null,
        Func<TranslationConsentRequest, Task<bool>>? confirmCloudConsentAsync = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _bridge = bridge;
        _configProvider = configProvider ?? new NullTranslationRuntimeConfigProvider();
        ConfirmCloudConsentAsync = confirmCloudConsentAsync;
        Tool = tool;
        Config = config ?? new TranslationConfigViewModel();

        // 配置任一变化 → 重新评估开始按钮可用性与校验行。
        Config.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConfigValidationMessage));
            OnPropertyChanged(nameof(HasConfigValidationError));
            StartCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>目录中的工具条目（名称、副标题、说明）。</summary>
    public Tool Tool { get; }

    /// <summary>共享翻译配置面板（T13）——整篇 Tab 与批量页同源。</summary>
    public TranslationConfigViewModel Config { get; }

    /// <summary>
    /// 云端首次同意委托（宿主接 F5 模态）；未注入则未同意即阻塞，不发起任何云端请求。
    /// 仅直接「开始」路径可弹模态；向导参数步为同步校验，未同意会在该步给出可读原因。
    /// </summary>
    public Func<TranslationConsentRequest, Task<bool>>? ConfirmCloudConsentAsync { get; set; }

    /// <summary>输入文件（顺序即处理顺序）。</summary>
    public ObservableCollection<BatchTranslationFileItem> Files { get; } = [];

    /// <summary>是否有输入文件。</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>是否支持多文件（文件选择对话框的 Multiselect）。</summary>
    public bool SupportsMultipleFiles => true;

    /// <summary>同意代次轮询间隔（F21；测试可缩短以快速验证撤销取消）。</summary>
    internal TimeSpan ConsentCheckInterval { get; set; } = ConsentRevocationWatcher.DefaultInterval;

    /// <summary>文件选择对话框过滤（批量翻译只接受 PDF）。</summary>
    public string FileDialogFilter { get; } = "PDF 文件|*.pdf|所有文件 (*.*)|*.*";

    /// <summary>超过建议上限（仅提示分批，不阻断执行）。</summary>
    public bool ExceedsRecommendedLimit => Files.Count > RecommendedFileLimit;

    /// <summary>输出文件夹（引擎在其下新建 AIPDF-日期-编号 任务目录，不覆盖源文件）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputDir = "";

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

    /// <summary>批量翻译无页范围字段可预填（整篇语义）。</summary>
    public bool SupportsPageRangePrefill => false;

    /// <summary>预填页范围（批量翻译不支持，空实现）。</summary>
    public void PrefillPageRange(int startPage, int endPage)
    {
    }

    /// <summary>
    /// V2 T10 FR-21：启动前门禁（宿主在承载页面时挂接）。批量页不经 T7 队列直接调引擎，
    /// 故门禁挂在执行入口；返回 false（如未保存批注 flush 失败且用户取消）则阻止本次执行。
    /// </summary>
    public Func<CancellationToken, Task<bool>>? StartGate { get; set; }

    // ---------- 运行状态 ----------

    /// <summary>是否正在批量翻译（期间禁用再次开始与文件编辑）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryAllFailedCommand))]
    private bool _isRunning;

    /// <summary>进度文案（当前文件阶段）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressMessage))]
    private string _progressMessage = "";

    /// <summary>是否有进度文案（进度行显隐）。</summary>
    public bool HasProgressMessage => ProgressMessage.Length > 0;

    /// <summary>总进度比例（0..1，按已完成文件数 + 当前文件内进度折算）。</summary>
    [ObservableProperty]
    private double _progressFraction;

    /// <summary>状态文案（完成 / 取消 / 引擎错误原文）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(IsErrorStatus))]
    private string _statusMessage = "";

    /// <summary>是否有状态文案（执行卡片展示开关）。</summary>
    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>状态是否为失败/异常（错误配色展示引擎原文）。</summary>
    public bool IsErrorStatus => StatusMessage.StartsWith("失败", StringComparison.Ordinal)
        || StatusMessage.StartsWith("引擎异常", StringComparison.Ordinal);

    // ---------- 汇总（部分失败） ----------

    /// <summary>成功项数。</summary>
    public int SuccessCount => Files.Count(f => f.IsDone);

    /// <summary>失败项数。</summary>
    public int FailedCount => Files.Count(f => f.IsFailed);

    /// <summary>跳过项数。</summary>
    public int SkippedCount => Files.Count(f => f.IsSkipped);

    /// <summary>是否已有处理结果（展示汇总行）。</summary>
    public bool HasResults => SuccessCount + FailedCount + SkippedCount > 0;

    /// <summary>是否存在部分失败（展示「重试全部失败项」）。</summary>
    public bool HasFailedItems => FailedCount > 0;

    /// <summary>部分失败摘要文案（T19）。</summary>
    public string SummaryText => $"成功 {SuccessCount} · 失败 {FailedCount} · 跳过 {SkippedCount}";

    /// <summary>是否存在未配置/参数校验错误（配置面板错误行）。</summary>
    public string ConfigValidationMessage => Config.Validate() ?? "";

    /// <summary>是否有配置校验错误。</summary>
    public bool HasConfigValidationError => ConfigValidationMessage.Length > 0;

    // ---------- 命令 ----------

    private bool CanStart => !IsRunning && Files.Count > 0;

    private bool CanCancel => IsRunning;

    /// <summary>开始批量翻译：校验后按文件【串行】执行，UI 不阻塞。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (Validate() is { } error)
        {
            StatusMessage = error;
            return;
        }

        if (_bridge is null)
        {
            StatusMessage = "引擎未就绪：未配置执行桥。";
            return;
        }

        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _cts = cts;

        // FR-21：执行前门禁（未保存批注 flush；失败且用户取消 → 阻止启动）。
        if (StartGate is not null && !await StartGate(token))
        {
            _cts = null;
            StatusMessage = "批注保存失败，批量翻译未执行。";
            return;
        }

        // 云端未同意时先弹同意模态（宿主注入）；未同意 / 取消 → 不发起任何请求。
        var resolution = await TranslationRuntimeConsentFlow.EnsureAsync(_configProvider, ConfirmCloudConsentAsync, Config.SelectedProvider);
        if (resolution.Config is not { } runtime)
        {
            _cts = null;
            StatusMessage = resolution.Message;
            return;
        }

        foreach (var file in Files)
        {
            file.ResetToPending();
        }

        RefreshSummary();
        await RunTranslationAsync(
            [.. Files],
            token,
            $"正在批量翻译 {Files.Count} 份文件…",
            () => $"完成：{SummaryText}。结果在下方按需打开。",
            "已取消：批量翻译已停止，未采用半成品。",
            watchConsent: resolution.IsCloud); // v1.2 T7：本地 / 云端由解析结果派生（稳定 id local）
    }

    /// <summary>取消批量翻译：取消令牌 → EngineBridge 终止当前子进程树，不采用半成品。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 令牌已释放（任务刚结束）：取消无副作用。
        }
    }

    /// <summary>浏览文件（多选，仅 PDF）。</summary>
    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"为{Tool.Name}选择文件",
            Filter = FileDialogFilter,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    /// <summary>移除单个文件（执行中不动作）。</summary>
    [RelayCommand]
    private void RemoveFile(BatchTranslationFileItem? item)
    {
        if (item is not null && !IsRunning && Files.Remove(item))
        {
            RefreshSummary();
        }
    }

    /// <summary>选择输出文件夹。</summary>
    [RelayCommand]
    private void BrowseOutputDir()
    {
        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        if (OutputDir.Trim().Length > 0 && Directory.Exists(OutputDir.Trim()))
        {
            dialog.InitialDirectory = OutputDir.Trim();
        }

        if (dialog.ShowDialog() == true)
        {
            OutputDir = dialog.FolderName;
        }
    }

    /// <summary>单独重试某个失败项（不打断其他文件；执行中不可用）。</summary>
    [RelayCommand(CanExecute = nameof(CanRetryFile))]
    private async Task RetryFileAsync(BatchTranslationFileItem? item)
    {
        if (item is null || IsRunning)
        {
            return;
        }

        if (Validate() is { } error)
        {
            StatusMessage = error;
            return;
        }

        if (_bridge is null)
        {
            StatusMessage = "引擎未就绪：未配置执行桥。";
            return;
        }

        // F23：用 Resolve() 区分「无服务商 / 未同意 / 无凭据 / 非 loopback」，直接展示具体原因，
        // 不再用笼统的「尚未配置或同意已撤销」把不同缺口混为一谈。
        var resolution = _configProvider.Resolve(Config.SelectedProvider);
        if (resolution.Config is not { } runtime)
        {
            StatusMessage = resolution.Message;
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        await RunTranslationAsync(
            [item],
            cts.Token,
            $"正在重试 {item.Name}…",
            () => item.IsDone ? $"重试成功：{item.Name}。" : $"重试失败：{item.ResultText}",
            "已取消：重试已停止。",
            watchConsent: resolution.IsCloud); // v1.2 T7：本地 / 云端由解析结果派生（稳定 id local）
    }

    private bool CanRetryFile(BatchTranslationFileItem? item) => !IsRunning && item is { CanRetry: true };

    /// <summary>一键重试全部失败项（串行，限当前会话）。</summary>
    [RelayCommand(CanExecute = nameof(CanRetryAll))]
    private async Task RetryAllFailedAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var failures = Files.Where(f => f.IsFailed).ToList();
        if (failures.Count == 0)
        {
            return;
        }

        if (Validate() is { } error)
        {
            StatusMessage = error;
            return;
        }

        if (_bridge is null)
        {
            StatusMessage = "引擎未就绪：未配置执行桥。";
            return;
        }

        // F23：同单文件重试，用 Resolve() 暴露具体未就绪原因。
        var resolution = _configProvider.Resolve(Config.SelectedProvider);
        if (resolution.Config is not { } runtime)
        {
            StatusMessage = resolution.Message;
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        await RunTranslationAsync(
            failures,
            cts.Token,
            $"正在重试 {failures.Count} 个失败项…",
            () => $"重试完成：{SummaryText}。",
            "已取消：重试已停止。",
            watchConsent: resolution.IsCloud); // v1.2 T7：本地 / 云端由解析结果派生（稳定 id local）
    }

    private bool CanRetryAll => !IsRunning && HasFailedItems;

    /// <summary>打开纯译文（按需；产物缺失给出可读原因，不静默打开空白标签）。</summary>
    [RelayCommand]
    private void OpenMono(BatchTranslationFileItem? item) => OpenArtifact(item, item?.MonoPath, "纯译文");

    /// <summary>打开对照（按需）。</summary>
    [RelayCommand]
    private void OpenDual(BatchTranslationFileItem? item) => OpenArtifact(item, item?.DualPath, "对照");

    /// <summary>打开输出文件夹（查看全部产物）。</summary>
    [RelayCommand]
    private void OpenOutputDir()
    {
        var folder = OutputDir.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            StatusMessage = "输出文件夹不存在。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            StatusMessage = $"打开输出文件夹失败：{error.Message}";
        }
    }

    // ---------- 文件输入 ----------

    /// <summary>
    /// 拖拽 / 对话框统一入口：按扩展名过滤（仅 PDF）、去重；首个文件默认其所在目录为输出位置。
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
                continue; // 路径字符串非法（防御），忽略用户没选中的项
            }

            var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!allowed.Contains(extension))
            {
                continue; // 不在允许的扩展名里，同源目录忽略越界文件
            }

            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Files.Add(new BatchTranslationFileItem(path));
            if (OutputDir.Trim().Length == 0)
            {
                // 默认输出目录 = 源文件所在目录（最终会在其下新建任务子目录，不覆盖源文件）
                OutputDir = System.IO.Path.GetDirectoryName(path) ?? "";
            }
        }

        RefreshSummary();
    }

    /// <summary>把当前文档加入并置顶、标记「当前文档」（浮菜单子菜单入口）。</summary>
    public void PinCurrentDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return;
        }

        AddFiles([fullPath]);

        var item = Files.FirstOrDefault(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            var index = Files.IndexOf(item);
            if (index > 0)
            {
                Files.Move(index, 0);
            }
        }

        PinnedDocumentPath = fullPath;
    }

    // ---------- V3 T8 向导接入（IWizardToolAdapter） ----------

    /// <summary>参数步内联校验【ST-25】：文件 + 共享配置 + 输出目录 + 运行时配置（按暂存文件复用既有校验语义）。</summary>
    public string? ValidateParameters(IReadOnlyList<string> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);

        if (stagedFiles.Count == 0)
        {
            return "请先选择文件。";
        }

        if (Config.Validate() is { } configError)
        {
            return configError;
        }

        if (WizardAdapterSupport.ResolveOutputDir(OutputDir, stagedFiles, out _) is { } outputError)
        {
            return outputError;
        }

        return _configProvider.Get() is null
            ? "翻译服务尚未配置，请在「设置 → 翻译」中配置服务商与语言后重试。"
            : null;
    }

    /// <summary>
    /// 提交快照：逐文件一项（每项一个队列任务），保证某份失败不中断其余（T18）。
    /// 提交时刻读取配置与输出目录并固化（review M2）。
    /// </summary>
    public IReadOnlyList<WizardSubmitItem> BuildSubmitItems(IReadOnlyList<string> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);
        WizardAdapterSupport.ResolveOutputDir(OutputDir, stagedFiles, out var outputDir);
        var runtime = _configProvider.Get();
        var options = BuildEngineOptions(runtime ?? new TranslationRuntimeConfig("", "", "", "", "", "", ""));
        return [.. stagedFiles.Select(file => new WizardSubmitItem(Tool.Name, [file], options, outputDir))];
    }

    /// <summary>执行单个已固化快照项：直接调引擎，不回读 VM 可变状态。</summary>
    public async Task<WizardItemResult> ExecuteItemAsync(WizardSubmitItem item, CancellationToken token)
    {
        if (_bridge is null)
        {
            return WizardItemResult.Failure("引擎未就绪：未配置执行桥。");
        }

        var result = await _bridge.ExecuteAsync(WizardAdapterSupport.BuildRequest(TranslateOperation, item), token)
            .ConfigureAwait(true);
        return WizardAdapterSupport.ToResult(result);
    }

    // ---------- 内部 ----------

    /// <summary>执行前校验。返回完整（含标点）的错误文案；null 表示通过。</summary>
    private string? Validate()
    {
        if (Files.Count == 0)
        {
            return "请先选择文件。";
        }

        if (Config.Validate() is { } configError)
        {
            return configError;
        }

        var output = OutputDir.Trim();
        if (output.Length == 0 || !Path.IsPathRooted(output) || !Directory.Exists(output))
        {
            return "请选择有效的输出文件夹。";
        }

        // 运行时配置（含云端同意）在 StartAsync 里经 EnsureAsync 解析/征求同意，故不在此同步校验。
        return null;
    }

    /// <summary>
    /// 三个执行入口（开始 / 单文件重试 / 全部重试）共用的运行与收尾骨架：
    /// 置 IsRunning 与运行文案、订阅进度、串行执行给定文件、按取消/完成设状态，
    /// finally 统一退订并复位进度。options / token / 前置校验由调用方各自准备。
    /// </summary>
    private async Task RunTranslationAsync(
        IReadOnlyList<BatchTranslationFileItem> files,
        CancellationToken token,
        string runningMessage,
        Func<string> completionMessage,
        string cancelledMessage,
        bool watchConsent = false)
    {
        IsRunning = true;
        StatusMessage = runningMessage;
        ProgressMessage = "";
        ProgressFraction = 0;

        // F21：云端批量运行期监视同意代次；撤销同意即取消，停止继续向已撤回同意的服务商发送。
        _consentRevoked = false;
        var cts = _cts;
        if (watchConsent && cts is not null)
        {
            var generation = _configProvider.ConsentGeneration;
            _consentWatch = ConsentRevocationWatcher.Start(
                () => _configProvider.ConsentGeneration, generation, cts,
                onRevoked: () => _consentRevoked = true, interval: ConsentCheckInterval).Task;
        }

        _bridge!.Progress += OnBridgeProgress;
        try
        {
            var stopMessage = await RunFilesAsync(files, token);
            if (stopMessage is not null)
            {
                // F22：某文件执行前配置已失效（中途改端点 / 换凭据 / 撤销同意）→ 停止剩余文件并展示具体原因。
                StatusMessage = stopMessage;
            }
            else
            {
                StatusMessage = token.IsCancellationRequested ? cancelledMessage : completionMessage();
                if (_consentRevoked)
                {
                    StatusMessage = ConsentRevokedMessage;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _consentRevoked ? ConsentRevokedMessage : cancelledMessage;
        }
        finally
        {
            _bridge.Progress -= OnBridgeProgress;
            cts?.Cancel();
            if (_consentWatch is not null)
            {
                await _consentWatch;
                _consentWatch = null;
            }

            _cts = null;
            IsRunning = false;
            ProgressMessage = "";
            ProgressFraction = 0;
            RefreshSummary();
        }
    }

    /// <summary>
    /// 按给定文件【串行】执行，UI 不阻塞；任一文件失败不中断其余。
    /// 返回 null 表示全部处理完毕；返回非 null 表示在某个文件前运行时配置已失效，
    /// 值为可直接展示的具体原因（剩余文件不再发送）。
    /// </summary>
    private async Task<string?> RunFilesAsync(IReadOnlyList<BatchTranslationFileItem> files, CancellationToken token)
    {
        _totalFiles = files.Count;
        _completedFiles = 0;
        ProgressFraction = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            // F22：每个文件执行前重新解析运行时配置——批量运行中途改端点 / 换凭据 / 撤销同意即时生效，
            // 不让后续文件沿用启动瞬间的旧配置。未就绪则停止剩余文件并返回具体原因（不静默发送）。
            var resolution = _configProvider.Resolve(Config.SelectedProvider);
            if (resolution.Config is not { } runtime)
            {
                return resolution.Message;
            }

            var options = BuildEngineOptions(runtime);
            _currentFileFraction = 0;
            ProgressMessage = $"正在翻译 {_completedFiles + 1}/{_totalFiles}：{file.Name}";
            await RunOneAsync(file, options, token);
            _completedFiles++;
            ProgressFraction = _totalFiles == 0 ? 0 : (double)_completedFiles / _totalFiles;
        }

        return null;
    }

    /// <summary>翻译单个文件：请求 translate op，解析逐文件产物；失败记录原因不抛。</summary>
    private async Task RunOneAsync(BatchTranslationFileItem file, IReadOnlyDictionary<string, object?> options, CancellationToken token)
    {
        file.BeginRunning();
        _currentFileFraction = 0;
        try
        {
            var request = new EngineRequest
            {
                Operation = TranslateOperation,
                Files = [file.Path],
                Options = new Dictionary<string, object?>(options, StringComparer.Ordinal),
                OutputDir = OutputDir.Trim(),
            };

            var result = await _bridge!.ExecuteAsync(request, token).ConfigureAwait(true);
            if (!result.Ok)
            {
                file.Fail(result.Error);
                return;
            }

            var outcome = EngineFullTranslationService.ParseOutcome(result);
            var mono = outcome.Artifacts.FirstOrDefault(a => a.Kind == "mono")?.Path;
            var dual = outcome.Artifacts.FirstOrDefault(a => a.Kind == "dual")?.Path;
            file.Complete(mono, dual, outcome.PageIssues, outcome.Overflow);
        }
        catch (OperationCanceledException)
        {
            file.ResetToPending();
            throw;
        }
        catch (EngineBridgeException error)
        {
            file.Fail($"引擎异常：{error.Message}");
        }
        catch (Exception error)
        {
            file.Fail(error.Message);
        }
    }

    /// <summary>组装引擎 translate options（值一律字符串；密钥经此进入子进程 stdin）。</summary>
    private Dictionary<string, object?> BuildEngineOptions(TranslationRuntimeConfig runtime)
        => TranslationEngineOptions.Build(
            runtime.Endpoint,
            runtime.Model,
            runtime.ApiKey,
            runtime.ApiProtocol,
            Config.SourceLanguage?.Code ?? TranslationConfigViewModel.AutoDetectCode,
            Config.TargetLanguage?.Code ?? TranslationConfigViewModel.DefaultTargetCode,
            FullTranslationViewModel.ToEngineTemplate(Config.SelectedTemplate?.Id),
            Config.GenerateMono,
            Config.GenerateDual,
            Config.ExportMarkdown,
            // 批量产物始终带「机器翻译 · 请核对」页脚（F6b）；法律/医学由引擎强制，口径一致。
            marker: true,
            glossaryPath: string.IsNullOrWhiteSpace(Config.GlossaryPath) ? null : Config.GlossaryPath.Trim(),
            // 自定义模板正文（决策 #10）：用户填写的 system 指令，随请求发往引擎。
            customPrompt: Config.IsCustomTemplate && !string.IsNullOrWhiteSpace(Config.CustomPrompt)
                ? Config.CustomPrompt.Trim()
                : null,
            maxTokens: Config.CostLimitEnabled ? (int?)Math.Clamp(Config.CostLimitTokens, 1, int.MaxValue) : null);

    private void OpenArtifact(BatchTranslationFileItem? item, string? path, string kindLabel)
    {
        if (item is null || string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            StatusMessage = $"找不到{kindLabel}产物「{System.IO.Path.GetFileName(path)}」。该文件可能已被移动、重命名或所在磁盘已移除。";
            return;
        }

        StatusMessage = "";
        OpenDocumentRequested?.Invoke(this, path);
    }

    /// <summary>请求宿主按需打开某产物（绝不自动触发；仅用户点「打开」时）。</summary>
    public event EventHandler<string>? OpenDocumentRequested;

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(ExceedsRecommendedLimit));
        OnPropertyChanged(nameof(SuccessCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasFailedItems));
        OnPropertyChanged(nameof(SummaryText));
        StartCommand.NotifyCanExecuteChanged();
        RetryFileCommand.NotifyCanExecuteChanged();
        RetryAllFailedCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        // v1.2 T15 / P31：任务运行中禁用面板内联供应商切换，防止「一半用 A、一半用 B」。
        Config.Inline.IsRunning = value;
    }

    private void OnBridgeProgress(object? sender, EngineProgressEventArgs e)
    {
        // 进度事件来自子进程读循环线程；WPF 要求 UI 线程，测试环境没有 Application。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyProgress(e);
        }
        else
        {
            dispatcher.InvokeAsync(() => ApplyProgress(e));
        }
    }

    private void ApplyProgress(EngineProgressEventArgs e)
    {
        ProgressMessage = e.Message;
        _currentFileFraction = Math.Clamp(e.Fraction, 0, 1);
        ProgressFraction = _totalFiles == 0
            ? _currentFileFraction
            : Math.Clamp((_completedFiles + _currentFileFraction) / _totalFiles, 0, 1);
    }
}
