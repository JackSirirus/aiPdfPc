// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 共享配置面板视图模型（决策 #10 / #15 / #17，§入口收敛说明）：
//   - 阅读器「整篇」Tab（T15）与批量翻译工具页（T21）复用同一类型，读写共享配置语义；
//   - 默认引擎 = 本地（决策 #15）；云端须用户主动选择并走 T16 同意流程；
//   - 目标语言必须显式选择（决策 #20），源语言可「自动检测」；
//   - 成本硬上限（决策 #17）与只读回填策略（决策 #16）随配置展示；
//   - 本类只承载可绑定状态与纯逻辑校验，不含 IO / settings / 凭据持久化。
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>翻译引擎种类：默认本地；云端是用户主动选择的升级项，不是默认路径（决策 #15）。</summary>
public enum TranslationEngineKind
{
    /// <summary>本地引擎（用户自备运行时，须为 loopback）；默认。</summary>
    Local,

    /// <summary>云端 LLM（须显式配置并经首次明确同意）。</summary>
    Cloud,
}

/// <summary>语言下拉项：<see cref="Code"/> 为稳定标识，<see cref="Label"/> 为界面文案。</summary>
public sealed record TranslationLanguageOption(string Code, string Label);

/// <summary>
/// Prompt 模板项：5 类预置（学术 / 法律 / 医学 / 技术 / 通用）+ 自定义。
/// <see cref="HighRisk"/> 标记法律 / 医学（开始时须高风险确认）；<see cref="IsCustom"/> 标记自定义。
/// </summary>
public sealed record TranslationTemplateOption(string Id, string Label, bool HighRisk = false, bool IsCustom = false);

/// <summary>
/// 共享翻译配置面板的视图模型（v1.1 决策 #10 / #15 / #17、§入口收敛说明）。
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

    /// <summary>构造共享配置并填入设计默认值（本地引擎、自动检测源、中文目标、学术模板、mono + dual）。</summary>
    public TranslationConfigViewModel()
    {
        SourceLanguageOptions = SourceLanguages;
        TargetLanguageOptions = TargetLanguages;
        TemplateOptions = Templates;

        _sourceLanguage = SourceLanguages[0];
        _targetLanguage = TargetLanguages[0];
        _selectedTemplate = Templates[0];
    }

    /// <summary>源语言选项（含「自动检测」）。</summary>
    public IReadOnlyList<TranslationLanguageOption> SourceLanguageOptions { get; }

    /// <summary>目标语言选项（不含「自动检测」，必须显式选择）。</summary>
    public IReadOnlyList<TranslationLanguageOption> TargetLanguageOptions { get; }

    /// <summary>Prompt 模板选项（5 类预置 + 自定义）。</summary>
    public IReadOnlyList<TranslationTemplateOption> TemplateOptions { get; }

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

    /// <summary>翻译引擎；默认本地（决策 #15）。</summary>
    [ObservableProperty]
    private TranslationEngineKind _engine = TranslationEngineKind.Local;

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

    /// <summary>引擎是否为本地（RadioButton 双向桥，默认 true）。</summary>
    public bool IsLocalEngine
    {
        get => Engine == TranslationEngineKind.Local;
        set
        {
            if (value)
            {
                Engine = TranslationEngineKind.Local;
            }
        }
    }

    /// <summary>引擎是否为云端（RadioButton 双向桥；选中后由 T16 负责同意流程）。</summary>
    public bool IsCloudEngine
    {
        get => Engine == TranslationEngineKind.Cloud;
        set
        {
            if (value)
            {
                Engine = TranslationEngineKind.Cloud;
            }
        }
    }

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
    /// 执行前配置校验：返回完整（含标点）错误文案，null 表示通过。
    /// 校验项：目标语言显式 / 源≠目标 / 至少一种输出 / 自定义模板有正文 / 成本上限为正 / 术语表为 CSV。
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

    partial void OnEngineChanged(TranslationEngineKind value)
    {
        OnPropertyChanged(nameof(IsLocalEngine));
        OnPropertyChanged(nameof(IsCloudEngine));
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
}
