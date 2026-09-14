// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 整篇翻译视图模型（纯逻辑，无 WPF 桌面依赖，xunit 直接覆盖）：
//   - 纵向流程：复用 T13 共享配置 → [开始翻译] → 进度（阶段名 + 百分比 + 日志）→ 结果（mono/dual + 披露）；
//   - 取消：CancellationToken → EngineBridge taskkill /T /F，终止子进程树，不采用半成品；
//   - 后台继续：仅本次会话内有效（应用退出即丢失未导出内容），不承诺跨会话持久化；
//   - 受限文档确认门禁：权限加密文档开始前经注入的确认委托弹「确认你有权处理该文档」；
//     译后结果区展示「已绕过源文档的抽取限制（经用户确认）」；
//   - 扫描页提示：文档含无文本层页时显示提示条（由宿主探测后注入计数）。
// 本类不写 AppSettings / 不落盘密钥 / 不持久化任何任务状态。
using System.Collections.ObjectModel;
using System.Windows;
using AIPDF.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.ViewModels.Translation;

/// <summary>整篇成果物（mono / dual）。</summary>
public sealed class FullTranslationArtifactViewModel
{
    public FullTranslationArtifactViewModel(string kind, string path)
    {
        Kind = kind ?? "";
        Path = path ?? "";
    }

    /// <summary>产物类型："mono" / "dual" / 其他。</summary>
    public string Kind { get; }

    /// <summary>产物绝对路径。</summary>
    public string Path { get; }

    /// <summary>产物展示名。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>产物徽标文案（MONO / DUAL）。</summary>
    public string Badge => Kind switch
    {
        "mono" => "MONO",
        "dual" => "DUAL",
        _ => "产物",
    };

    /// <summary>产物中文说明。</summary>
    public string Description => Kind switch
    {
        "mono" => "纯译文",
        "dual" => "原文 / 译文对照",
        _ => "输出文件",
    };
}

/// <summary>
/// 「整篇」Tab 面板模型（v1.1 T15）。宿主注入共享 <see cref="TranslationConfigViewModel"/>、
/// 运行时配置来源与整篇翻译执行器；文档上下文（路径 / 受限 / 扫描页）由宿主在活动标签切换时刷新。
/// </summary>
public sealed partial class FullTranslationViewModel : ObservableObject
{
    /// <summary>后台继续的会话内边界文案（不得改写为跨会话承诺）。</summary>
    public const string SessionScopeNotice = "仅本次会话内有效，应用退出即丢失未导出内容";

    /// <summary>受限文档译后披露兜底文案（引擎未回传披露时使用）。</summary>
    public const string RestrictionDisclosureFallback = "已绕过源文档的抽取限制（经用户确认）";

    /// <summary>默认整篇任务超时（取消/超时必存在，不允许无限等待）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly IFullTranslationService _service;
    private readonly ITranslationRuntimeConfigProvider _configProvider;
    private CancellationTokenSource? _cts;
    private bool _userCancelled;

    public FullTranslationViewModel(
        IFullTranslationService service,
        ITranslationRuntimeConfigProvider? configProvider = null,
        TranslationConfigViewModel? config = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _configProvider = configProvider ?? new NullTranslationRuntimeConfigProvider();
        Config = config ?? new TranslationConfigViewModel();
        Log = new ObservableCollection<string>();
        Artifacts = new ObservableCollection<FullTranslationArtifactViewModel>();
    }

    /// <summary>共享翻译配置面板（T13）。</summary>
    public TranslationConfigViewModel Config { get; }

    /// <summary>
    /// 受限文档确认委托（宿主接模态：勾选「我有权处理该文档」后返回 true）。
    /// 未注入时视为未确认 —— 受限文档不会被静默翻译。
    /// </summary>
    public Func<Task<bool>>? ConfirmRestrictedDocumentAsync { get; set; }

    /// <summary>
    /// 高风险模板确认委托（法律 / 医学；宿主接模态，参数为模板显示名，勾选确认后返回 true）。
    /// 确认框默认未勾选，覆盖准确性免责 + 涉密/特权/受监管数据合规警告（F2b）。
    /// 未注入时视为未确认 —— 高风险模板不会在未确认下开始。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmHighRiskTemplateAsync { get; set; }

    /// <summary>
    /// 产物 PDF 页脚「机器翻译 · 请核对」标记策略（映射设置 <c>TranslationMarkerEnabled</c>）。默认开。
    /// 高风险模板（法律 / 医学）时无论该值如何都强制写标记，与 backend
    /// <c>output.add_marker(high_risk=True)</c> 的强制口径一致（§9.3）。
    /// </summary>
    public Func<bool> FooterMarkerProvider { get; set; } = static () => true;

    /// <summary>整篇任务超时（默认 <see cref="DefaultTimeout"/>；测试可缩短）。</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>请求打开某产物（宿主接新标签）。</summary>
    public event EventHandler<FullTranslationArtifactViewModel>? OpenArtifactRequested;

    // ---------- 文档上下文（宿主注入） ----------

    /// <summary>当前活动文档路径（空 = 无文档，禁止开始）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private string _activeDocumentPath = "";

    /// <summary>当前文档是否为权限加密（受限）文档：开始前需「确认你有权处理该文档」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresRestrictedConfirmation))]
    private bool _isRestrictedDocument;

    /// <summary>当前文档会话驻留密码（仅内存，经 stdin 传给引擎；不落盘 / 不进日志）。</summary>
    [ObservableProperty]
    private string? _documentPassword;

    /// <summary>探测器报告的扫描页数（&gt; 0 时显示提示条）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowScanNotice))]
    [NotifyPropertyChangedFor(nameof(ScanNoticeText))]
    private int _scannedPageCount;

    /// <summary>文档总页数（展示用）。</summary>
    [ObservableProperty]
    private int _totalPageCount;

    /// <summary>输出文件夹（引擎在其下新建 AIPDF-日期-编号 任务目录，不覆盖源文件）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputDir = "";

    // ---------- 运行状态 ----------

    /// <summary>任务是否进行中（含后台继续）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackgroundContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnToTaskCommand))]
    [NotifyPropertyChangedFor(nameof(ShowTaskView))]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundNotice))]
    private bool _isRunning;

    /// <summary>是否已切到「后台继续」（任务仍在跑，仅视图收起）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackgroundContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnToTaskCommand))]
    [NotifyPropertyChangedFor(nameof(ShowTaskView))]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundNotice))]
    private bool _isBackgrounded;

    /// <summary>当前阶段名（抽取 / OCR / 翻译 / 回填 / 导出）。</summary>
    [ObservableProperty]
    private string _stageText = "";

    /// <summary>进度比例（0..1）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private double _progressFraction;

    /// <summary>状态文案（失败/取消/完成；引擎原文优先）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(IsErrorStatus))]
    private string _statusMessage = "";

    /// <summary>译后受限披露（受限文档非空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRestrictionDisclosure))]
    private string? _restrictionDisclosure;

    /// <summary>降级页数摘要（每页降级报告）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDegradation))]
    private string _degradationSummary = "";

    /// <summary>进度日志（诊断行；不含文档正文 / 密码 / 密钥）。</summary>
    public ObservableCollection<string> Log { get; }

    /// <summary>译后产物（mono / dual）。</summary>
    public ObservableCollection<FullTranslationArtifactViewModel> Artifacts { get; }

    // ---------- 派生状态 ----------

    /// <summary>是否有可翻译文档。</summary>
    public bool HasDocument => !string.IsNullOrEmpty(ActiveDocumentPath);

    /// <summary>是否需要在开始前弹受限确认。</summary>
    public bool RequiresRestrictedConfirmation => IsRestrictedDocument;

    /// <summary>是否显示扫描页提示条。</summary>
    public bool ShowScanNotice => ScannedPageCount > 0;

    /// <summary>扫描页提示条文案（与设计稿 T05 对齐）。</summary>
    public string ScanNoticeText => ScannedPageCount > 0
        ? $"⚠ 检测到 {ScannedPageCount} 页扫描件（无文本层）。将先 OCR 再翻译；仅白底图片页效果好，质量尽力而为。"
        : "";

    /// <summary>进度百分比（整数）。</summary>
    public int ProgressPercent => (int)Math.Round(Math.Clamp(ProgressFraction, 0, 1) * 100);

    /// <summary>进度百分比文案。</summary>
    public string ProgressPercentText => $"{ProgressPercent}%";

    /// <summary>是否有进度日志。</summary>
    public bool HasLog => Log.Count > 0;

    /// <summary>是否有译后产物。</summary>
    public bool HasResults => Artifacts.Count > 0;

    /// <summary>是否有状态文案。</summary>
    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>状态是否为失败/异常（错误配色）。</summary>
    public bool IsErrorStatus => StatusMessage.StartsWith("失败", StringComparison.Ordinal)
        || StatusMessage.StartsWith("引擎异常", StringComparison.Ordinal)
        || StatusMessage.StartsWith("已超时", StringComparison.Ordinal);

    /// <summary>是否显示译后受限披露。</summary>
    public bool ShowRestrictionDisclosure => !string.IsNullOrEmpty(RestrictionDisclosure);

    /// <summary>是否显示降级摘要。</summary>
    public bool HasDegradation => DegradationSummary.Length > 0;

    /// <summary>是否显示任务视图（运行中且未后台）。</summary>
    public bool ShowTaskView => IsRunning && !IsBackgrounded;

    /// <summary>是否显示后台继续的会话内提示（运行中且已后台）。</summary>
    public bool ShowBackgroundNotice => IsRunning && IsBackgrounded;

    /// <summary>会话内边界文案（绑定展示）。</summary>
    public string SessionScopeText => SessionScopeNotice;

    // ---------- 命令 ----------

    private bool CanStart => !IsRunning && HasDocument && !string.IsNullOrWhiteSpace(OutputDir);

    private bool CanCancel => IsRunning;

    private bool CanBackground => IsRunning && !IsBackgrounded;

    private bool CanReturnToTask => IsRunning && IsBackgrounded;

    /// <summary>开始整篇翻译。校验通过后经执行器调用 `translate` op；进度经 stderr 转发。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (Config.Validate() is { } configError)
        {
            StatusMessage = configError;
            return;
        }

        if (!HasDocument)
        {
            StatusMessage = "请先打开一个 PDF 文档。";
            return;
        }

        var output = OutputDir.Trim();
        if (output.Length == 0 || !System.IO.Path.IsPathRooted(output) || !System.IO.Directory.Exists(output))
        {
            StatusMessage = "请选择有效的输出文件夹。";
            return;
        }

        var runtime = _configProvider.Get();
        if (runtime is null)
        {
            StatusMessage = "翻译服务尚未配置，请在「设置 → 翻译」中配置服务商与语言后重试。";
            return;
        }

        if (RequiresRestrictedConfirmation)
        {
            var confirmed = ConfirmRestrictedDocumentAsync is not null
                && await ConfirmRestrictedDocumentAsync();
            if (!confirmed)
            {
                StatusMessage = "已取消：未确认有权处理该受限文档，未开始翻译。";
                return;
            }
        }

        // 高风险模板（法律 / 医学）确认门禁：默认未勾选，必须显式确认才可开始（F2b / §9.3）。
        if (Config.IsHighRiskTemplate)
        {
            var confirmed = ConfirmHighRiskTemplateAsync is not null
                && await ConfirmHighRiskTemplateAsync(Config.SelectedTemplate?.Label ?? "");
            if (!confirmed)
            {
                StatusMessage = "已取消：未确认高风险模板提示，未开始翻译。";
                return;
            }
        }

        var request = BuildRequest(runtime, output);

        RestrictionDisclosure = null;
        DegradationSummary = "";
        StatusMessage = "正在启动整篇翻译…";
        StageText = "准备中";
        ProgressFraction = 0;
        Log.Clear();
        Artifacts.Clear();
        NotifyCollections();

        _userCancelled = false;
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        _cts = linked;

        IsRunning = true;
        IsBackgrounded = false;

        try
        {
            var outcome = await _service.TranslateAsync(request, new InlineProgress(OnProgress), linked.Token);
            ApplyOutcome(outcome, timeoutCts.IsCancellationRequested && !_userCancelled);
        }
        catch (Exception error)
        {
            // 服务契约不应抛，但宿主侧任何意外都不得静默：展示可读原因，不崩溃。
            StatusMessage = $"引擎异常：{error.Message}";
        }
        finally
        {
            _cts = null;
            IsRunning = false;
            IsBackgrounded = false;
        }
    }

    /// <summary>取消整篇翻译：取消令牌 → EngineBridge 终止子进程树，不采用半成品。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _userCancelled = true;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 令牌已释放（任务刚结束）：取消无副作用。
        }
    }

    /// <summary>后台继续：仅收起任务视图，任务在本次会话内继续；不承诺跨会话。</summary>
    [RelayCommand(CanExecute = nameof(CanBackground))]
    private void BackgroundContinue() => IsBackgrounded = true;

    /// <summary>回到任务视图。</summary>
    [RelayCommand(CanExecute = nameof(CanReturnToTask))]
    private void ReturnToTask() => IsBackgrounded = false;

    /// <summary>打开某产物（宿主开新标签，T20 接入）。</summary>
    [RelayCommand]
    private void OpenArtifact(FullTranslationArtifactViewModel? artifact)
    {
        if (artifact is not null)
        {
            OpenArtifactRequested?.Invoke(this, artifact);
        }
    }

    // ---------- 内部 ----------

    private FullTranslationRequest BuildRequest(TranslationRuntimeConfig runtime, string output)
        => new(
            FilePath: ActiveDocumentPath,
            OutputDir: output,
            SourceLang: Config.SourceLanguage?.Code ?? TranslationConfigViewModel.AutoDetectCode,
            TargetLang: Config.TargetLanguage?.Code ?? TranslationConfigViewModel.DefaultTargetCode,
            Template: ToEngineTemplate(Config.SelectedTemplate?.Id),
            Endpoint: runtime.Endpoint,
            Model: runtime.Model,
            ApiKey: runtime.ApiKey,
            GlossaryPath: string.IsNullOrWhiteSpace(Config.GlossaryPath) ? null : Config.GlossaryPath.Trim(),
            MaxTokens: Config.CostLimitEnabled ? (int)Math.Clamp(Config.CostLimitTokens, 1, int.MaxValue) : null,
            GenerateMono: Config.GenerateMono,
            GenerateDual: Config.GenerateDual,
            Password: DocumentPassword,
            // 页脚标记：设置开关；高风险模板（法律 / 医学）强制开启，忽略设置（§9.3，
            // 与 backend output.add_marker(high_risk=True) 的强制口径一致）。
            Marker: FooterMarkerProvider() || Config.IsHighRiskTemplate);

    /// <summary>
    /// T13 模板标识 → 引擎 <c>prompt.PRESET_TEMPLATES</c> 键名。
    /// T13 的「通用」为 <c>general</c>，引擎为 <c>generic</c>；自定义模板引擎暂不支持，回退 <c>generic</c>
    /// （面板同时展示「自定义 Prompt 尚未接入引擎」提示，不静默承诺）。
    /// </summary>
    internal static string ToEngineTemplate(string? id) => id switch
    {
        null or "" => "generic",
        "general" => "generic",
        "custom" => "generic",
        _ => id,
    };

    private void ApplyOutcome(FullTranslationOutcome outcome, bool timedOut)
    {
        if (outcome.Cancelled)
        {
            StageText = timedOut ? "已超时" : "已取消";
            StatusMessage = timedOut
                ? $"已超时（超过 {Timeout.TotalMinutes:0} 分钟），任务已停止，未采用半成品。"
                : "已取消：本次整篇任务已停止，未采用半成品。";
            ProgressFraction = 0;
            return;
        }

        if (!outcome.Ok)
        {
            StageText = "失败";
            StatusMessage = $"失败：{outcome.Error}";
            return;
        }

        foreach (var artifact in outcome.Artifacts)
        {
            Artifacts.Add(new FullTranslationArtifactViewModel(artifact.Kind, artifact.Path));
        }

        if (outcome.SourceProtection.Restricted)
        {
            RestrictionDisclosure = string.IsNullOrWhiteSpace(outcome.SourceProtection.Disclosure)
                ? RestrictionDisclosureFallback
                : outcome.SourceProtection.Disclosure;
        }

        DegradationSummary = BuildDegradationSummary(outcome.PageIssues);
        StageText = "完成";
        ProgressFraction = 1;
        StatusMessage = $"翻译完成：{Artifacts.Count} 个产物 · 用时 {outcome.ElapsedSeconds:0.0}s";
        NotifyCollections();
    }

    private static string BuildDegradationSummary(IReadOnlyList<FullTranslationPageIssue> issues)
    {
        if (issues.Count == 0)
        {
            return "";
        }

        var details = string.Join(" · ", issues.Select(issue =>
            $"p{issue.Page} {string.Join("/", issue.Reasons)}"));
        return $"每页降级：{issues.Count} 页（{details}）";
    }

    private void OnProgress(FullTranslationProgress progress)
    {
        RunOnUi(() =>
        {
            StageText = string.IsNullOrWhiteSpace(progress.Stage) ? StageText : progress.Stage;
            ProgressFraction = Math.Clamp(progress.Fraction, 0, 1);
            var line = $"{DateTime.Now:HH:mm:ss}  {progress.Stage}";
            Log.Add(line);
            while (Log.Count > 200)
            {
                Log.RemoveAt(0);
            }

            OnPropertyChanged(nameof(HasLog));
        });
    }

    private void NotifyCollections()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasLog));
    }

    /// <summary>进度回调来自子进程读循环线程；WPF 要求 UI 线程，测试环境没有 Application（就地执行）。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }

    /// <summary>同步进度实现：直接回调（不依赖 SynchronizationContext），宿主内部自行 marshal。</summary>
    private sealed class InlineProgress(Action<FullTranslationProgress> handler) : IProgress<FullTranslationProgress>
    {
        public void Report(FullTranslationProgress value) => handler(value);
    }
}
