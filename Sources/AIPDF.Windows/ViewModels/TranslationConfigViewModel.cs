// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 共享配置面板视图模型（决策 #10 / #15 / #17，§入口收敛说明）：
//   - 阅读器「整篇」Tab（T15）与批量翻译工具页（T21）复用同一类型，读写共享配置语义；
//   - v1.2（T9）：原「本地 / 云端 LLM」引擎单选被「模型供应商」选择取代（稳定 id：本地 local /
//     14 家预设 / 已保存自定义）；协议（openai / anthropic）由选中供应商派生并支持按模型覆盖
//     （opencode-go 混合协议），供面板展示与运行时装配（T6/T7）消费；
//   - 目标语言必须显式选择（决策 #20），源语言可「自动检测」；
//   - 成本硬上限（决策 #17）与只读回填策略（决策 #16）随配置展示；
//   - 本类只承载可绑定状态与纯逻辑派生，不含 IO / settings / 凭据持久化。
using System.ComponentModel;
using System.Globalization;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels.Translation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>语言下拉项：<see cref="Code"/> 为稳定标识，<see cref="Label"/> 为界面文案。</summary>
public sealed record TranslationLanguageOption(string Code, string Label);

/// <summary>
/// Prompt 模板项：5 类预置（学术 / 法律 / 医学 / 技术 / 通用）+ 自定义。
/// <see cref="HighRisk"/> 标记法律 / 医学（开始时须高风险确认）；<see cref="IsCustom"/> 标记自定义。
/// </summary>
public sealed record TranslationTemplateOption(string Id, string Label, bool HighRisk = false, bool IsCustom = false);

/// <summary>
/// 供应商下拉项：<see cref="Id"/> 为稳定标识（本地 <c>local</c> / 预设 id / 自定义规范名，
/// 与 <see cref="TranslationProviderIdentity"/> 同源），<see cref="DisplayName"/> 为界面文案。
/// <see cref="IsCustom"/> 标记无预设行的自定义供应商（模型列表需经「拉取模型」填充）。
/// </summary>
public sealed record TranslationProviderOption(string Id, string DisplayName, bool IsCustom = false)
{
    /// <summary>是否为本地服务（稳定 id <c>local</c>）。</summary>
    public bool IsLocal => TranslationProviderConfig.IsLocalPresetId(Id);
}

/// <summary>
/// 共享翻译配置面板的视图模型（v1.1 决策 #10 / #15 / #17、§入口收敛说明；v1.2 T9 供应商化）。
/// 由阅读器「整篇」Tab（T15）与批量翻译工具页（T21）共用同一类型；配置对象可整体注入两个宿主。
/// 只承载可绑定状态与校验：默认值、选项集合、只读派生文案、<see cref="Validate"/>。
/// </summary>
public sealed partial class TranslationConfigViewModel : ObservableObject
{
    /// <summary>源语言「自动检测」的稳定标识。</summary>
    public const string AutoDetectCode = "auto";

    /// <summary>目标语言默认标识（中文，显式选择，不参与自动检测）。</summary>
    public const string DefaultTargetCode = "zh";

    /// <summary>默认 Prompt 模板标识（学术）。</summary>
    public const string DefaultTemplateId = "academic";

    /// <summary>云端成本硬上限默认值（2.0M token / 任务）。</summary>
    public const long DefaultCostLimitTokens = 2_000_000;

    /// <summary>只读回填策略说明（决策 #16，详见 翻译-回填排版规格.md）。</summary>
    public const string BackfillStrategyText = "缩字 1.00→0.70 → 溢到页边 → 裁切 + 降级标注（只读，详见回填规格）";

    /// <summary>默认供应商稳定标识：本地服务（决策 #15：本地是默认路径，云端是用户主动选择的升级项）。</summary>
    public const string DefaultProviderId = TranslationProviderConfig.LocalProviderName;

    /// <summary>「OpenAI 兼容」协议徽章文案（别名；唯一来源 <see cref="TranslationProviderCompliance.OpenAiLabel"/>）。</summary>
    public const string ProtocolOpenAiLabel = TranslationProviderCompliance.OpenAiLabel;

    /// <summary>「Anthropic 兼容」协议徽章文案（别名；唯一来源 <see cref="TranslationProviderCompliance.AnthropicLabel"/>）。</summary>
    public const string ProtocolAnthropicLabel = TranslationProviderCompliance.AnthropicLabel;

    /// <summary>未选中模型时的展示文案（本地 / 自定义经「拉取模型」或手填后才有模型）。</summary>
    public const string NoModelLabel = "未选择模型";

    private static readonly IReadOnlyDictionary<string, string> EmptyProtocols =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>已保存配置按身份键（<see cref="TranslationProviderIdentity.For"/>）登记：预设项提供协议覆盖，自定义项提供下拉条目。</summary>
    private readonly Dictionary<string, TranslationProviderConfig> _savedProviders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>经「拉取模型」得到、按供应商 id 缓存的模型列表（会话内有效；UI 调 T5 服务后经 <see cref="SetFetchedModels"/> 填充）。</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _fetchedModels = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> _availableModels = [];
    private IReadOnlyDictionary<string, string> _modelProtocols = EmptyProtocols;

    /// <summary>面板内联入口的只读来源（v1.2 T15）：把本 VM 的当前选择投影为「显示名 · 模型」。</summary>
    private readonly TranslationProviderReadout _inlineReadout;

    private static readonly IReadOnlyList<TranslationLanguageOption> SourceLanguages =
    [
        new(AutoDetectCode, "自动检测"),
        new("zh", "中文"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("fr", "法语"),
        new("de", "德语"),
        new("ru", "俄语"),
        new("es", "西班牙语"),
        new("pt", "葡萄牙语"),
        new("it", "意大利语"),
    ];

    private static readonly IReadOnlyList<TranslationLanguageOption> TargetLanguages =
    [
        new("zh", "中文"),
        new("en", "English"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("ru", "Русский"),
        new("es", "Español"),
        new("pt", "Português"),
        new("it", "Italiano"),
    ];

    private static readonly IReadOnlyList<TranslationTemplateOption> Templates =
    [
        new("academic", "学术"),
        new("legal", "法律", HighRisk: true),
        new("medical", "医学", HighRisk: true),
        new("technical", "技术"),
        new("general", "通用"),
        new("custom", "＋ 自定义", IsCustom: true),
    ];

    /// <summary>
    /// 构造共享配置并填入设计默认值（本地服务、自动检测源、中文目标、学术模板、mono + dual）。
    /// <paramref name="savedProviders"/> 为已保存的供应商配置（由宿主从设置注入，本类不做 IO）：
    /// 预设项用于回填协议 / 按模型覆盖，未命中预设的项作为「自定义」下拉条目展示。
    /// </summary>
    public TranslationConfigViewModel(IEnumerable<TranslationProviderConfig>? savedProviders = null)
    {
        SourceLanguageOptions = SourceLanguages;
        TargetLanguageOptions = TargetLanguages;
        TemplateOptions = Templates;

        _sourceLanguage = SourceLanguages[0];
        _targetLanguage = TargetLanguages[0];
        _selectedTemplate = Templates[0];

        var saved = savedProviders?.Where(config => config is not null).ToList();
        AvailableProviders = BuildProviderOptions(saved);

        // v1.2 后续修复：本地未配置（或不可用）时不再固定选中「本地」，改选设置顺序中的首个可用供应商，
        // 避免 v1.1 只配过云端的用户升级后每次启动都落回不可用的本地项（划词报「缺少端点地址」）。
        _selectedProvider = ResolveInitialProvider(saved);

        RefreshProviderState();

        // v1.2 T15：面板内联快捷入口（折叠 / 展开）只读跟随本 VM 的当前选择；纯逻辑，无 IO。
        _inlineReadout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot(SelectedProvider, ProviderDisplayName, SelectedModel, Protocol),
            this);
        _inlineReadout.Refresh();
        Inline = new TranslationProviderInlineViewModel(_inlineReadout);
    }

    /// <summary>源语言选项（含「自动检测」）。</summary>
    public IReadOnlyList<TranslationLanguageOption> SourceLanguageOptions { get; }

    /// <summary>目标语言选项（不含「自动检测」，必须显式选择）。</summary>
    public IReadOnlyList<TranslationLanguageOption> TargetLanguageOptions { get; }

    /// <summary>Prompt 模板选项（5 类预置 + 自定义）。</summary>
    public IReadOnlyList<TranslationTemplateOption> TemplateOptions { get; }

    /// <summary>供应商选项：本地服务（固定首位）+ 14 家内置预设 + 已保存自定义（按保存顺序，大小写不敏感去重）。</summary>
    public IReadOnlyList<TranslationProviderOption> AvailableProviders { get; }

    /// <summary>
    /// 面板内联快捷入口的展开 / 收起状态机（v1.2 T15，设计 P27–P31）：
    /// 折叠态展示「供应商 · 显示名 · 模型」+「更换 ▾」；展开态就地编辑供应商 / 模型 / Key。
    /// 宿主通过 <c>Config.Inline.IsRunning</c> 推送任务运行状态（运行中禁止展开）。
    /// </summary>
    public TranslationProviderInlineViewModel Inline { get; }

    /// <summary>源语言；默认「自动检测」。</summary>
    [ObservableProperty]
    private TranslationLanguageOption _sourceLanguage;

    /// <summary>目标语言；默认「中文」，必须显式选择。</summary>
    [ObservableProperty]
    private TranslationLanguageOption _targetLanguage;

    /// <summary>Prompt 模板；默认「学术」。</summary>
    [ObservableProperty]
    private TranslationTemplateOption _selectedTemplate;

    /// <summary>自定义 Prompt 正文（模板选「自定义」时使用，冗余为空则由校验拦截）。</summary>
    [ObservableProperty]
    private string _customPrompt = "";

    /// <summary>术语表 CSV 路径；空 = 不使用术语表。</summary>
    [ObservableProperty]
    private string _glossaryPath = "";

    /// <summary>选中供应商的稳定标识（本地 <c>local</c> / 预设 id / 自定义规范名）；默认本地服务。</summary>
    [ObservableProperty]
    private string _selectedProvider = DefaultProviderId;

    /// <summary>选中模型名（预设取默认模型 / 已保存模型，本地与自定义经拉取或手填）。</summary>
    [ObservableProperty]
    private string _selectedModel = "";

    /// <summary>输出纯译文 mono（默认开）。</summary>
    [ObservableProperty]
    private bool _generateMono = true;

    /// <summary>输出对照 dual（默认开）。</summary>
    [ObservableProperty]
    private bool _generateDual = true;

    /// <summary>导出 Markdown（默认关）。</summary>
    [ObservableProperty]
    private bool _exportMarkdown;

    /// <summary>是否启用云端成本硬上限（默认开，2.0M token / 任务）。</summary>
    [ObservableProperty]
    private bool _costLimitEnabled = true;

    /// <summary>成本硬上限的 token 数（启用时须 &gt; 0）。</summary>
    [ObservableProperty]
    private long _costLimitTokens = DefaultCostLimitTokens;

    /// <summary>选中供应商是否为本地服务（v1.2 T9：取代原「引擎二选一」的本地判定）。</summary>
    public bool IsLocalProvider => TranslationProviderConfig.IsLocalPresetId(SelectedProvider);

    /// <summary>选中供应商是否为云端（非本地即云端，含自定义；云端首次使用走 T16 同意流程）。</summary>
    public bool IsCloudProvider => !IsLocalProvider;

    /// <summary>选中供应商的界面显示名（预设显示名 / 自定义名）；未知 id 回退 id 本身。</summary>
    public string ProviderDisplayName
    {
        get
        {
            foreach (var option in AvailableProviders)
            {
                if (string.Equals(option.Id, SelectedProvider, StringComparison.OrdinalIgnoreCase))
                {
                    return option.DisplayName;
                }
            }

            var saved = SelectedSavedProvider?.Provider?.Trim();
            return saved is { Length: > 0 } ? saved : SelectedProvider ?? "";
        }
    }

    /// <summary>当前可用模型列表：预设 = 预设默认模型（或拉取结果），自定义 = 空（UI 经「拉取模型」填充）。</summary>
    public IReadOnlyList<string> AvailableModels => _availableModels;

    /// <summary>
    /// 选中供应商的按模型协议覆盖表（只读）：优先已保存配置，其次预设（opencode-go 混合协议）；
    /// 无覆盖时为只读空表。
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelProtocols => _modelProtocols;

    /// <summary>
    /// 当前传输协议（只读，<c>openai</c> | <c>anthropic</c>）：命中按模型覆盖则取覆盖值，
    /// 否则取供应商默认协议（已保存配置优先于预设，缺省 <c>openai</c>）。
    /// </summary>
    public string Protocol
    {
        get
        {
            var model = SelectedModel?.Trim() ?? "";
            if (model.Length > 0
                && ModelProtocols.TryGetValue(model, out var modelProtocol)
                && !string.IsNullOrWhiteSpace(modelProtocol))
            {
                return NormalizeProtocol(modelProtocol);
            }

            return NormalizeProtocol(EffectiveDefaultProtocol());
        }
    }

    /// <summary>协议徽章文案（只读展示）：OpenAI 兼容 / Anthropic 兼容。</summary>
    public string ProtocolLabel
        => Protocol == TranslationProviderConfig.ProtocolAnthropic ? ProtocolAnthropicLabel : ProtocolOpenAiLabel;

    /// <summary>当前模型展示文案（只读）：模型名，未选择时为「未选择模型」。</summary>
    public string ModelLabel => SelectedModel?.Trim() is { Length: > 0 } model ? model : NoModelLabel;

    /// <summary>成本上限是否「不限」（与 <see cref="CostLimitEnabled"/> 互反，供勾选框双向绑定）。</summary>
    public bool IsCostLimitUnlimited
    {
        get => !CostLimitEnabled;
        set => CostLimitEnabled = !value;
    }

    /// <summary>是否选中「自定义」模板（控制自定义 Prompt 输入框显隐）。</summary>
    public bool IsCustomTemplate => SelectedTemplate.IsCustom;

    /// <summary>是否选中高风险模板（法律 / 医学），宿主据此在开始前弹一次确认（F2b）。</summary>
    public bool IsHighRiskTemplate => SelectedTemplate.HighRisk;

    /// <summary>需要高风险确认（语义别名，供宿主绑定警告条显隐）。</summary>
    public bool RequiresHighRiskConfirmation => IsHighRiskTemplate;

    /// <summary>成本上限摘要文案（界面展示）。</summary>
    public string CostLimitSummary => CostLimitEnabled
        ? $"云端 ≤ {(CostLimitTokens / 1_000_000d).ToString("0.0#", CultureInfo.InvariantCulture)}M token / 任务"
        : "云端不限（不设成本硬上限）";

    /// <summary>只读回填策略展示文案。</summary>
    public string BackfillStrategySummary => BackfillStrategyText;

    /// <summary>当前校验错误文案；通过时为空串。</summary>
    public string ValidationMessage => Validate() ?? "";

    /// <summary>是否存在校验错误（错误行显隐开关）。</summary>
    public bool HasValidationError => ValidationMessage.Length > 0;

    /// <summary>当前配置是否可用于开始（校验通过）。</summary>
    public bool CanStart => Validate() is null;

    /// <summary>
    /// 用「拉取模型」（T5）结果填充当前供应商的模型列表（UI 显式触发；本类不做 IO）。
    /// 空列表照实呈现（不静默回退预设）；当前模型仍非空时保留（可能不在服务端列表，由 UI 另行提示）。
    /// </summary>
    public void SetFetchedModels(IEnumerable<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var list = new List<string>();
        foreach (var model in models)
        {
            var value = model?.Trim() ?? "";
            if (value.Length == 0 || list.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(value);
        }

        _fetchedModels[SelectedProvider ?? ""] = list;
        _availableModels = list;

        if (string.IsNullOrWhiteSpace(SelectedModel) && list.Count > 0)
        {
            SelectedModel = list[0]; // 触发协议 / 展示文案刷新
        }

        OnPropertyChanged(nameof(AvailableModels));
    }

    /// <summary>
    /// 执行前配置校验：返回完整（含标点）错误文案，null 表示通过。
    /// 校验项：目标语言显式 / 源≠目标 / 至少一种输出 / 自定义模板有正文 / 成本上限为正 / 术语表为 CSV。
    /// （供应商 / 协议的运行时可用性由 T7 的 Resolve 负责，不并入本方法。）
    /// </summary>
    public string? Validate()
    {
        if (TargetLanguage is null || string.Equals(TargetLanguage.Code, AutoDetectCode, StringComparison.OrdinalIgnoreCase))
        {
            return "请显式选择目标语言（目标语言不参与自动检测）。";
        }

        if (SourceLanguage is not null
            && !string.Equals(SourceLanguage.Code, AutoDetectCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SourceLanguage.Code, TargetLanguage.Code, StringComparison.OrdinalIgnoreCase))
        {
            return "源语言与目标语言不能相同。";
        }

        if (!GenerateMono && !GenerateDual && !ExportMarkdown)
        {
            return "请至少选择一种输出格式。";
        }

        if (SelectedTemplate is { IsCustom: true } && string.IsNullOrWhiteSpace(CustomPrompt))
        {
            return "已选「自定义」模板，请填写 Prompt 内容。";
        }

        if (CostLimitEnabled && CostLimitTokens <= 0)
        {
            return "成本上限必须大于 0，或勾选「不限」。";
        }

        var glossary = GlossaryPath?.Trim() ?? "";
        if (glossary.Length > 0 && !glossary.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return "术语表需为 CSV 文件（.csv）。";
        }

        return null;
    }

    partial void OnSelectedProviderChanged(string value) => RefreshProviderState();

    partial void OnSelectedModelChanged(string value)
    {
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(ModelLabel));
    }

    partial void OnSelectedTemplateChanged(TranslationTemplateOption value)
    {
        OnPropertyChanged(nameof(IsCustomTemplate));
        OnPropertyChanged(nameof(IsHighRiskTemplate));
        OnPropertyChanged(nameof(RequiresHighRiskConfirmation));
    }

    partial void OnCostLimitEnabledChanged(bool value)
        => OnPropertyChanged(nameof(IsCostLimitUnlimited));

    partial void OnCostLimitTokensChanged(long value)
        => OnPropertyChanged(nameof(CostLimitSummary));

    /// <summary>任意配置项变化后刷新派生的校验/可用状态（配置面板错误行与开始按钮门禁）。</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(ValidationMessage) or nameof(HasValidationError) or nameof(CanStart))
        {
            return;
        }

        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(CanStart));
    }

    /// <summary>已保存配置中命中当前选中供应商的项（未保存 / 自定义无配置时为 null）。</summary>
    private TranslationProviderConfig? SelectedSavedProvider
        => _savedProviders.TryGetValue(SelectedProvider ?? "", out var config) ? config : null;

    /// <summary>当前选中供应商的预设（本地 / 云端预设命中时非 null；自定义为 null）。</summary>
    private TranslationProviderPreset? SelectedPreset => TranslationProviderPreset.Find(SelectedProvider);

    /// <summary>
    /// 供应商下拉数据源：本地服务（固定首位）→ 14 家预设（表内顺序）→ 已保存自定义（按保存顺序）。
    /// 预设 / 本地项只登记配置（供协议覆盖），不重复进列表；自定义项身份键与 T8 凭据寻址同源。
    /// </summary>
    private IReadOnlyList<TranslationProviderOption> BuildProviderOptions(IEnumerable<TranslationProviderConfig>? savedProviders)
    {
        var options = new List<TranslationProviderOption>
        {
            new(TranslationProviderPreset.Local.Id, TranslationProviderPreset.Local.DisplayName),
        };
        options.AddRange(TranslationProviderPreset.All.Select(preset => new TranslationProviderOption(preset.Id, preset.DisplayName)));

        if (savedProviders is null) return options;

        foreach (var config in savedProviders)
        {
            if (config is null) continue;

            var key = TranslationProviderIdentity.For(config);
            if (key.Length == 0) continue;

            _savedProviders.TryAdd(key, config);

            // 预设（含本地）已在列表中，仅以其配置覆盖协议派生。
            if (TranslationProviderPreset.Find(key) is not null) continue;
            if (options.Exists(option => string.Equals(option.Id, key, StringComparison.OrdinalIgnoreCase))) continue;

            var displayName = config.Provider.Trim();
            options.Add(new TranslationProviderOption(key, displayName.Length > 0 ? displayName : key, IsCustom: true));
        }

        return options;
    }

    /// <summary>
    /// 供应商切换后重建全部派生状态：模型列表（拉取缓存 / 预设默认）、按模型协议覆盖、
    /// 默认模型（已保存模型优先，否则首个可用模型），并通知只读展示属性。
    /// 空选择（绑定被清空）归一回默认本地服务，避免下游按空身份寻址。
    /// </summary>
    private void RefreshProviderState()
    {
        if (string.IsNullOrWhiteSpace(SelectedProvider))
        {
            SelectedProvider = DefaultProviderId; // 重入一次后走正常分支
            return;
        }

        _availableModels = BuildAvailableModels();
        _modelProtocols = EffectiveModelProtocols();

        var savedModel = SelectedSavedProvider?.Model?.Trim() ?? "";
        var preferred = savedModel.Length > 0
            ? savedModel
            : _availableModels.Count > 0 ? _availableModels[0] : "";

        if (!string.Equals(SelectedModel, preferred, StringComparison.Ordinal))
        {
            SelectedModel = preferred; // 触发协议 / 模型文案通知
        }

        OnPropertyChanged(nameof(AvailableModels));
        OnPropertyChanged(nameof(ModelProtocols));
        OnPropertyChanged(nameof(ProviderDisplayName));
        OnPropertyChanged(nameof(IsLocalProvider));
        OnPropertyChanged(nameof(IsCloudProvider));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(ModelLabel));
    }

    /// <summary>模型列表：拉取结果（若已拉取）→ 预设默认模型 → 自定义 / 未知为空表。</summary>
    private IReadOnlyList<string> BuildAvailableModels()
    {
        if (_fetchedModels.TryGetValue(SelectedProvider ?? "", out var fetched)) return fetched;
        return SelectedPreset?.DefaultModels ?? [];
    }

    /// <summary>按模型覆盖表：已保存配置优先，其次预设默认覆盖（仅 opencode-go 非空）。</summary>
    private IReadOnlyDictionary<string, string> EffectiveModelProtocols()
    {
        var saved = SelectedSavedProvider?.ModelProtocols;
        if (saved is { Count: > 0 }) return saved;

        var preset = SelectedPreset;
        if (preset?.DefaultModelProtocols is { Count: > 0 } presetProtocols) return presetProtocols;

        return EmptyProtocols;
    }

    /// <summary>供应商默认协议：已保存配置（T14 允许用户改协议）优先，其次预设，最后引擎契约缺省 openai。</summary>
    private string? EffectiveDefaultProtocol()
    {
        var saved = SelectedSavedProvider?.ApiProtocol?.Trim();
        if (!string.IsNullOrEmpty(saved)) return saved;
        return SelectedPreset?.DefaultProtocol;
    }

    /// <summary>
    /// 初始选中供应商：默认本地服务（决策 #15）；仅当设置里没有可用的本地项时，改选设置顺序中的首个可用供应商。
    /// 目的：v1.1 只配置过云端的用户升级后默认选中项依然可用（此前固定 <c>local</c> 导致划词必然报「缺少端点地址」）。
    /// 判定只看配置本身（provider + 端点，<see cref="TranslationProviderConfig.IsUsable"/>），不读凭据、不做 IO；
    /// 找不到可用项时回退本地服务（与既有行为一致）。
    /// </summary>
    private static string ResolveInitialProvider(IReadOnlyList<TranslationProviderConfig>? savedProviders)
    {
        if (savedProviders is null or { Count: 0 })
        {
            return DefaultProviderId;
        }

        var localUsable = false;
        string? firstUsableCloud = null;

        foreach (var config in savedProviders)
        {
            if (!config.IsUsable)
            {
                continue;
            }

            var key = TranslationProviderIdentity.For(config);
            if (key.Length == 0)
            {
                continue;
            }

            if (TranslationProviderConfig.IsLocalPresetId(key)
                || TranslationProviderConfig.IsLocalService(config.PresetId, config.Provider))
            {
                localUsable = true;
                continue;
            }

            firstUsableCloud ??= key;
        }

        return localUsable ? DefaultProviderId : firstUsableCloud ?? DefaultProviderId;
    }

    /// <summary>
    /// 协议规范化（T4 引擎契约：未知值按 openai 处理，展示与传输语义一致）。
    /// <b>薄委托</b>：唯一真源是 <see cref="TranslationProviderConfig.NormalizeProtocol"/>。
    /// </summary>
    private static string NormalizeProtocol(string? value)
        => TranslationProviderConfig.NormalizeProtocol(value);
}
