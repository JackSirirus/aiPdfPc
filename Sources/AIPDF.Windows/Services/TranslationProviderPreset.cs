// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · 内置供应商预设表（T1；决策 #2/#5/#11/#13/#14）：
//   - 14 家云端内置预设（稳定 id + 显示名 + base URL + 环境变量名 + 默认模型 + 协议）硬编码在代码中，
//     不引入远程 catalog / 动态拉取（与 opencode CLI 的 models.dev 模式区别：本表即 catalog）；
//   - 预设只含连接元数据，**绝不含密钥 / 正文**；密钥交 TranslationCredentialStore；
//   - 稳定 id（全小写，如 openai / opencode-go / glm-coding-plan）供凭据寻址、同意定位与运行时判定
//     （T8）；显示名仅供界面展示；两者分离；
//   - 本地服务固定稳定 id "local"，显示名「本地服务」；它不属于云端预设表（<see cref="All"/>），
//     界面将其单独分组固定在首位（无预设端点，端由用户填写并做 loopback 校验）。
//
// 协议约定（T4 引擎契约）：DefaultProtocol ∈ { "openai" | "anthropic" }；Provider 级默认之下，
// opencode-go 通过 DefaultModelProtocols 按模型覆盖。
//
// Anthropic base URL 形状（§Base URL 形状规则）：AnthropicBaseUrl 为【不含 /v1 后缀】的基址，
// 客户端（backend/translate/llm.py）自行拼接 /v1/messages；OpenAI 协议则拼 {base}/chat/completions。
// 例：opencode-go 的 Anthropic 实际端点为 https://opencode.ai/zen/go/v1/messages。
using System.Collections.Generic;

namespace AIPDF.Windows.Services;

/// <summary>
/// 预设类别：区分标准按量计费云 API、订阅网关与编程套餐。
/// 编程套餐 / 订阅端点仅限官方支持的工具使用，界面须常驻合规警示且默认不选中（决策 #14）。
/// </summary>
public enum TranslationProviderPresetKind
{
    /// <summary>订阅网关（如 opencode go）：额度随订阅账户计费，限速与可用模型由服务商侧控制。</summary>
    CloudSubscription,

    /// <summary>标准按量计费云 API（预设表默认形态）。</summary>
    CloudStandard,

    /// <summary>编程套餐端点：自建应用通过 API 调用可能不计入套餐额度，须保留标准 API 作为默认。</summary>
    CodingPlan,

    /// <summary>
    /// 本地服务（用户自填 loopback 端点，无预设 base URL）。本地 / 云端运行判定一律以稳定 id
    /// <c>local</c> 为准（<see cref="TranslationProviderConfig.IsLocalService(string?, string?)"/>），
    /// Kind 只参与展示与合规分类。数值与 <see cref="CloudStandard"/> 相等是<b>有意为之</b>：
    /// 保证既有 switch / 比较（含不可修改的只读消费者与逐字断言测试）零行为变化。
    /// </summary>
    Local = CloudStandard,
}

/// <summary>
/// 内置供应商预设（不可变 data record）：纯连接元数据，不含任何密钥。
/// <see cref="All"/> 为 14 家云端内置预设定表（唯一权威来源），<see cref="Local"/> 为本地服务项；
/// 自定义供应商无预设行，由用户自由填写 provider id + endpoint + model。
/// </summary>
public sealed record TranslationProviderPreset
{
    /// <summary>稳定 id（全小写，如 <c>opencode-go</c> / <c>glm-coding-plan</c>）。持久化 / 凭据 / 同意定位均以它为准。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（中文或其他自然语言形式，仅供界面展示，不参与定位）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>预设类别（标准 / 订阅网关 / 编程套餐 / 本地）。</summary>
    public required TranslationProviderPresetKind Kind { get; init; }

    /// <summary>OpenAI 兼容 base URL（通常含 <c>/v1</c>）；不提供该协议时为 null。</summary>
    public string? OpenAiBaseUrl { get; init; }

    /// <summary>Anthropic Messages base URL（不含 <c>/v1</c> 后缀，客户端拼 <c>/v1/messages</c>）；不提供时为 null。</summary>
    public string? AnthropicBaseUrl { get; init; }

    /// <summary>Provider 级默认协议（<c>openai</c> | <c>anthropic</c>）。</summary>
    public required string DefaultProtocol { get; init; }

    /// <summary>
    /// 对应的环境变量名（用于界面提示既有约定，不读取、不写入）；编程套餐与本地服务无标准环境变量，为空串。
    /// </summary>
    public required string EnvVarName { get; init; }

    /// <summary>预设默认模型列表（第一项为默认选中项）；空数组 = 由用户选择 / 拉取（如 opencode Zen、本地服务）。</summary>
    public required string[] DefaultModels { get; init; }

    private readonly Dictionary<string, string>? _defaultModelProtocols;

    /// <summary>
    /// 按模型协议覆盖（仅混合协议供应商非空，当前仅 opencode-go）；未命中的模型回退 <see cref="DefaultProtocol"/>。
    /// 值只允许 <c>openai</c> / <c>anthropic</c>。
    /// <b>读取即返回副本</b>（大小写不敏感）：预设表是不可变权威来源，调用方（如保存配置时
    /// <c>ModelProtocols = preset.DefaultModelProtocols</c>）拿到的是独立可写字典，
    /// 后续修改不会污染 <see cref="All"/> / <see cref="Local"/>；无覆盖时返回 null。
    /// </summary>
    public Dictionary<string, string>? DefaultModelProtocols
    {
        get => _defaultModelProtocols is null
            ? null
            : new Dictionary<string, string>(_defaultModelProtocols, StringComparer.OrdinalIgnoreCase);
        init => _defaultModelProtocols = value;
    }

    /// <summary>
    /// 常驻合规 / 订阅警示文案（仅编程套餐与订阅网关非空）；界面显示为不可关闭提示条。
    /// </summary>
    public string? ComplianceWarning { get; init; }

    /// <summary>
    /// 14 家云端内置预设（顺序即界面列表展示顺序）。本地服务见 <see cref="Local"/>，不在本表内。
    /// </summary>
    public static IReadOnlyList<TranslationProviderPreset> All { get; } =
    [
        new()
        {
            Id = "openai",
            DisplayName = "OpenAI",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://api.openai.com/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "OPENAI_API_KEY",
            DefaultModels = ["gpt-4o"],
        },
        new()
        {
            Id = "anthropic",
            DisplayName = "Anthropic Claude",
            Kind = TranslationProviderPresetKind.CloudStandard,
            AnthropicBaseUrl = "https://api.anthropic.com",
            DefaultProtocol = TranslationProviderConfig.ProtocolAnthropic,
            EnvVarName = "ANTHROPIC_API_KEY",
            DefaultModels = ["claude-sonnet-4-20250514"],
        },
        new()
        {
            Id = "gemini",
            DisplayName = "Google Gemini",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "GOOGLE_API_KEY",
            DefaultModels = ["gemini-2.0-flash"],
        },
        new()
        {
            Id = "deepseek",
            DisplayName = "DeepSeek",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://api.deepseek.com/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "DEEPSEEK_API_KEY",
            DefaultModels = ["deepseek-chat"],
        },
        new()
        {
            Id = "qwen",
            DisplayName = "阿里通义千问",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "DASHSCOPE_API_KEY",
            DefaultModels = ["qwen-plus"],
        },
        new()
        {
            Id = "zhipu",
            DisplayName = "智谱 GLM",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "ZHIPU_API_KEY",
            DefaultModels = ["glm-4-flash"],
        },
        new()
        {
            Id = "kimi",
            DisplayName = "月之暗面 Kimi",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://api.moonshot.cn/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "MOONSHOT_API_KEY",
            // moonshot-v1-* 已退役（§内置供应商预设表），改列当前在售模型；第一项为默认。
            DefaultModels = ["kimi-k3", "kimi-k2.7-code", "kimi-k2.7-code-highspeed", "kimi-k2.6"],
        },
        new()
        {
            Id = "groq",
            DisplayName = "Groq",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://api.groq.com/openai/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "GROQ_API_KEY",
            DefaultModels = ["llama-3.1-70b-versatile"],
        },
        new()
        {
            Id = "minimax",
            DisplayName = "MiniMax",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://api.minimax.chat/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "MINIMAX_API_KEY",
            DefaultModels = ["MiniMax-Text-01"],
        },
        new()
        {
            Id = "doubao",
            DisplayName = "Doubao 豆包",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "ARK_API_KEY",
            DefaultModels = ["doubao-1.5-pro-32k"],
        },
        new()
        {
            Id = "opencode",
            DisplayName = "opencode Zen",
            Kind = TranslationProviderPresetKind.CloudStandard,
            OpenAiBaseUrl = "https://api.opencode.ai/v1",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "OPENCODE_API_KEY",
            DefaultModels = [], // 按实际 catalog 填充 / 由用户拉取
        },
        new()
        {
            Id = "opencode-go",
            DisplayName = "opencode go",
            Kind = TranslationProviderPresetKind.CloudSubscription,
            OpenAiBaseUrl = "https://opencode.ai/zen/go/v1",
            // Anthropic 实际端点 = https://opencode.ai/zen/go/v1/messages（同 base 的 /messages）。
            AnthropicBaseUrl = "https://opencode.ai/zen/go",
            DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
            EnvVarName = "OPENCODE_API_KEY",
            DefaultModels = ["minimax-m3", "kimi-k3", "glm-5.3", "deepseek-v4-pro", "qwen3.8-max", "grok-4.6"],
            // 混合协议：仅允许 openai / anthropic 两值（responses 类模型本期不单独建模）。
            DefaultModelProtocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minimax-m3"] = TranslationProviderConfig.ProtocolAnthropic,
                ["minimax-m2.7"] = TranslationProviderConfig.ProtocolAnthropic,
                ["minimax-m2.5"] = TranslationProviderConfig.ProtocolAnthropic,
                ["qwen3.8-max"] = TranslationProviderConfig.ProtocolAnthropic,
                ["qwen3.8-flash"] = TranslationProviderConfig.ProtocolAnthropic,
                ["qwen3.7-max"] = TranslationProviderConfig.ProtocolAnthropic,
                ["qwen3.7-plus"] = TranslationProviderConfig.ProtocolAnthropic,
                ["qwen3.6-plus"] = TranslationProviderConfig.ProtocolAnthropic,
                ["kimi-k3"] = TranslationProviderConfig.ProtocolOpenAi,
                ["deepseek-v4-pro"] = TranslationProviderConfig.ProtocolOpenAi,
                ["grok-4.6"] = TranslationProviderConfig.ProtocolOpenAi,
            },
            ComplianceWarning =
                "订阅网关：额度随订阅账户计费，限速与可用模型由 opencode 侧控制，本应用不代管。",
        },
        new()
        {
            Id = "glm-coding-plan",
            DisplayName = "GLM Coding Plan",
            Kind = TranslationProviderPresetKind.CodingPlan,
            OpenAiBaseUrl = "https://open.bigmodel.cn/api/coding/paas/v4",
            AnthropicBaseUrl = "https://open.bigmodel.cn/api/anthropic",
            DefaultProtocol = TranslationProviderConfig.ProtocolAnthropic,
            EnvVarName = "", // 套餐专用 Key，无标准环境变量；与标准 API Key 不可互换
            DefaultModels = ["glm-5.3", "glm-5.3-flash"],
            ComplianceWarning =
                "此端点属于订阅 / 编程套餐，仅限官方支持的工具使用。自建应用通过 API 调用可能不计入套餐额度"
                + "（GLM 会返回 1113 Insufficient Balance）。如需在本应用使用，请改用标准 API 端点。",
        },
        new()
        {
            Id = "kimi-code",
            DisplayName = "Kimi Code",
            Kind = TranslationProviderPresetKind.CodingPlan,
            OpenAiBaseUrl = "https://api.kimi.com/coding/v1",
            // Anthropic 实际端点 = https://api.kimi.com/coding/v1/messages。
            AnthropicBaseUrl = "https://api.kimi.com/coding",
            DefaultProtocol = TranslationProviderConfig.ProtocolAnthropic,
            EnvVarName = "", // sk-kimi- 专用 Key，无标准环境变量；与标准平台 Key 不可互换
            DefaultModels = ["k3", "k3-256k", "kimi-for-coding", "kimi-for-coding-highspeed"],
            ComplianceWarning =
                "此端点属于订阅 / 编程套餐，仅限官方支持的工具使用。自建应用通过 API 调用可能不计入套餐额度"
                + "（Kimi Code 要求应用具有真实工具身份）。如需在本应用使用，请改用标准 API 端点。",
        },
    ];

    /// <summary>
    /// 本地服务项（稳定 id <c>local</c>，显示名「本地服务」）：端由用户填写（OpenAI 兼容 base URL），
    /// 必须通过 loopback 校验；不进入云端预设表 <see cref="All"/>。
    /// </summary>
    public static TranslationProviderPreset Local { get; } = new()
    {
        Id = TranslationProviderConfig.LocalProviderName, // "local"
        DisplayName = "本地服务",
        Kind = TranslationProviderPresetKind.Local,
        OpenAiBaseUrl = null,
        AnthropicBaseUrl = null,
        DefaultProtocol = TranslationProviderConfig.ProtocolOpenAi,
        EnvVarName = "",
        DefaultModels = [],
    };

    /// <summary>
    /// 按稳定 id 查预设（含本地服务 <see cref="Local"/>）。id 比较不区分大小写（查找便利，非凭据寻址）；
    /// 未知 / 空 id 返回 null。自定义供应商无预设，返回 null。
    /// </summary>
    public static TranslationProviderPreset? Find(string? id)
    {
        var key = id?.Trim() ?? "";
        if (key.Length == 0) return null;

        if (string.Equals(key, Local.Id, StringComparison.OrdinalIgnoreCase)) return Local;

        return All.FirstOrDefault(preset => string.Equals(preset.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 按「稳定 id 或显示名」解析稳定 id（含本地服务 <see cref="Local"/>），大小写不敏感。
    /// 迁移口径与 <c>SettingsStore.NormalizePresetId</c> 的名称匹配一致：先全表匹配 id，再全表匹配显示名；
    /// 未命中返回 null（自定义供应商）。T8 凭据 / 同意定位与迁移共用本方法，保证身份解析同源。
    /// </summary>
    public static string? FindIdByName(string? nameOrId)
    {
        var key = nameOrId?.Trim() ?? "";
        if (key.Length == 0) return null;

        foreach (var preset in All)
        {
            if (string.Equals(preset.Id, key, StringComparison.OrdinalIgnoreCase)) return preset.Id;
        }

        if (string.Equals(Local.Id, key, StringComparison.OrdinalIgnoreCase)) return Local.Id;

        foreach (var preset in All)
        {
            if (string.Equals(preset.DisplayName, key, StringComparison.OrdinalIgnoreCase)) return preset.Id;
        }

        if (string.Equals(Local.DisplayName, key, StringComparison.OrdinalIgnoreCase)) return Local.Id;

        return null;
    }
}
