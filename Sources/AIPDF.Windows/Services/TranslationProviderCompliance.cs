// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T14 翻译 · 编程套餐合规 + 协议选择的纯逻辑：
//   - 编程套餐 / 订阅端点（glm-coding-plan / kimi-code / opencode-go）的合规警示文案取自
//     TranslationProviderPreset.ComplianceWarning（T1 唯一权威来源），本类只负责「是否/何时」展示；
//   - 「改用标准 API」把套餐端点切到同族标准预设（GLM → zhipu / Kimi → kimi），
//     绝不触碰凭据与同意（T14 Must NOT：不静默丢弃；切换后由界面明确反馈标准端点需单独保存密钥与同意）；
//   - 协议维度：provider 级 apiProtocol + 按模型覆盖 modelProtocols（opencode-go 混合协议）；
//     徽章/展示文案集中在此，供设置页与后续内联面板共用，避免各自拼字符串；
//   - 失败态分类覆盖 P38–P41：协议不匹配（400/404/405）、套餐额度不足（1113）、
//     密钥与端点不匹配（401/403）、已下线模型 id；分类为纯函数，便于 xUnit 直接锁定。
// 本类不读写磁盘 / 凭据 / 网络，也不含任何密钥；只做字符串与预设表推导。
using System.Collections.Generic;
using System.Linq;

namespace AIPDF.Windows.Services;

/// <summary>
/// 供应商调用失败的粗分类（P38–P41）。界面据此选择横幅文案，不据此改变重试策略。
/// </summary>
public enum TranslationProviderFailureKind
{
    /// <summary>无法归类（正常响应或未知错误）。</summary>
    None,

    /// <summary>套餐额度不足（GLM 1113 Insufficient Balance）：套餐端点不计额度。</summary>
    QuotaExceeded,

    /// <summary>协议与端点不匹配（400 / 404 / 405）：路径或请求体格式不被端点接受。</summary>
    ProtocolMismatch,

    /// <summary>密钥与端点不匹配（401 / 403）：套餐专用 Key 与标准 Key 不可互换。</summary>
    CredentialMismatch,

    /// <summary>模型 id 已下线（Kimi 退役清单）：调用会返回错误 / 404。</summary>
    RetiredModel,
}

/// <summary>
/// 编程套餐合规与协议选择的纯逻辑（T14）。无 IO、无状态，可被设置页、内联面板与测试共用。
/// </summary>
public static class TranslationProviderCompliance
{
    /// <summary>
    /// 「OpenAI 兼容」协议展示文案——<b>协议展示标签的唯一常量来源</b>：
    /// <c>TranslationConfigViewModel.ProtocolOpenAiLabel</c> 与 <c>TranslationProviderReadout.OpenAiBadge</c>
    /// 均为其别名；文案不得改动（测试逐字断言）。
    /// </summary>
    public const string OpenAiLabel = "OpenAI 兼容";

    /// <summary>「Anthropic 兼容」协议展示文案（唯一来源，同 <see cref="OpenAiLabel"/>）。</summary>
    public const string AnthropicLabel = "Anthropic 兼容";

    /// <summary>opencode-go 等混合协议供应商的只读展示文案（按模型决定协议，不支持全局切换）。</summary>
    public const string MixedLabel = "混合（按模型）";

    /// <summary>GLM 编程套餐预设 id。</summary>
    public const string GlmCodingPlanPresetId = "glm-coding-plan";

    /// <summary>Kimi Code 编程套餐预设 id。</summary>
    public const string KimiCodePresetId = "kimi-code";

    /// <summary>「改用标准 API」成功后的横幅文案（P42）。</summary>
    public const string StandardApiOkBanner = "已切换到标准 API 端点（按量计费，无套餐限制）。";

    /// <summary>「改用标准 API」成功后的短提示 / Toast 文案（P42 / Toast 汇总）。</summary>
    public const string StandardApiToast = "已切换到标准 API 端点";

    /// <summary>切换标准 API 后的凭据 / 同意语义说明（T14：不静默丢弃，须明确反馈）。</summary>
    public const string StandardApiCredentialNotice =
        "标准 API 端点是独立的凭据与同意目的地：套餐端点的密钥不会自动迁移，首次调用标准端点需重新保存密钥并同意。";

    /// <summary>套餐额度不足横幅（P39）：提示改用标准 API（按量计费）。</summary>
    public const string QuotaExceededBanner =
        "服务端返回 1113 Insufficient Balance：该套餐端点不接受本应用的调用，或套餐额度已用尽。"
        + "请改用标准 API 端点（按量计费），或到服务商后台检查套餐状态。";

    /// <summary>协议不匹配横幅（P38）。</summary>
    public const string ProtocolMismatchBanner =
        "请求失败（400 / 404）：路径或请求格式不被端点接受。该端点可能使用另一协议"
        + "（OpenAI 兼容走 /chat/completions + Bearer；Anthropic 兼容走 /v1/messages + x-api-key），请切换到匹配的协议后重试。";

    /// <summary>密钥与端点不匹配横幅（P41）。</summary>
    public const string CredentialMismatchBanner =
        "401：该密钥不适用于此端点。Kimi Code（sk-kimi-）与开放平台密钥、GLM 套餐密钥与标准 API 密钥互不通用。";

    /// <summary>已下线模型横幅（P40）。</summary>
    public const string RetiredModelBanner =
        "该模型 id 已下线：kimi-k2-0905-preview、kimi-k2-turbo-preview、kimi-latest、kimi-k2.5 调用会返回错误 / 404。"
        + "编程套餐请改用 k3 或 kimi-for-coding；开放平台请改用 kimi-k3 或 kimi-k2.7-code。";

    /// <summary>已退役的 Kimi 模型 id（精确匹配；另见 <see cref="IsRetiredModel"/> 的 <c>moonshot-v1-*</c> 前缀规则）。</summary>
    public static readonly IReadOnlyList<string> RetiredKimiModelIds =
    [
        "kimi-k2-0905-preview",
        "kimi-k2-turbo-preview",
        "kimi-latest",
        "kimi-k2.5",
    ];

    /// <summary>
    /// 协议值归一化：<c>anthropic</c> 大小写不敏感命中即 anthropic，其余（含 null / 未知）按引擎契约回退 <c>openai</c>。
    /// <b>薄委托</b>：唯一真源是 <see cref="TranslationProviderConfig.NormalizeProtocol"/>。
    /// </summary>
    public static string NormalizeProtocol(string? value)
        => TranslationProviderConfig.NormalizeProtocol(value);

    /// <summary>协议展示文案：<see cref="OpenAiLabel"/> / <see cref="AnthropicLabel"/>。</summary>
    public static string ProtocolLabel(string? protocol)
        => NormalizeProtocol(protocol) == TranslationProviderConfig.ProtocolAnthropic ? AnthropicLabel : OpenAiLabel;

    /// <summary>预设是否为混合协议（含按模型覆盖表，当前仅 opencode-go）。</summary>
    public static bool IsMixedProtocol(string? presetId)
        => TranslationProviderPreset.Find(presetId)?.DefaultModelProtocols is { Count: > 0 };

    /// <summary>
    /// 供应商级协议展示：混合协议显示「混合（按模型）」，其余按其默认协议显示；
    /// 自定义供应商（无预设）回退 <see cref="OpenAiLabel"/>。
    /// </summary>
    public static string ProtocolDisplay(string? presetId)
    {
        if (IsMixedProtocol(presetId)) return MixedLabel;
        return ProtocolLabel(TranslationProviderPreset.Find(presetId)?.DefaultProtocol);
    }

    /// <summary>该预设是否需要在详情常驻合规警示（<see cref="TranslationProviderPreset.ComplianceWarning"/> 非空）。</summary>
    public static bool RequiresComplianceNotice(string? presetId) => ComplianceNotice(presetId) is not null;

    /// <summary>合规 / 订阅警示原文（仅三家常驻项非空）；空值表示无需展示。</summary>
    public static string? ComplianceNotice(string? presetId)
    {
        var text = TranslationProviderPreset.Find(presetId)?.ComplianceWarning;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>预设是否为编程套餐（<see cref="TranslationProviderPresetKind.CodingPlan"/>）。</summary>
    public static bool IsCodingPlan(string? presetId)
        => TranslationProviderPreset.Find(presetId)?.Kind == TranslationProviderPresetKind.CodingPlan;

    /// <summary>
    /// 编程套餐对应的「同族标准 API」预设：GLM Coding Plan → 智谱 GLM（<c>zhipu</c>）；
    /// Kimi Code → 月之暗面 Kimi（<c>kimi</c>）。其他（含标准预设与自定义）返回 null，不虚构标准端点。
    /// </summary>
    public static TranslationProviderPreset? StandardPresetFor(string? presetId)
    {
        var id = presetId?.Trim() ?? "";
        if (string.Equals(id, GlmCodingPlanPresetId, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationProviderPreset.Find("zhipu");
        }

        if (string.Equals(id, KimiCodePresetId, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationProviderPreset.Find("kimi");
        }

        return null;
    }

    /// <summary>编程套餐对应的标准 API 端点（OpenAI 兼容 base）；无对应标准预设返回 null。</summary>
    public static string? StandardEndpointFor(string? presetId)
        => StandardPresetFor(presetId)?.OpenAiBaseUrl;

    /// <summary>
    /// 「改用标准 API」：把配置就地切到同族标准预设（provider 名 / 端点 / 协议），返回是否发生切换。
    /// 只改连接元数据：<b>不触碰凭据与同意</b>（T14 Must NOT：不静默丢弃）；模型名保留用户当前选择，
    /// 以便标准端点侧沿用同一模型（GLM glm-5.3 / Kimi 需按提示改选）。
    /// </summary>
    public static bool TrySwitchToStandardApi(TranslationProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var standard = StandardPresetFor(config.PresetId);
        if (standard is null || string.IsNullOrEmpty(standard.OpenAiBaseUrl)) return false;

        config.PresetId = standard.Id;
        config.Provider = standard.DisplayName;
        config.Endpoint = standard.OpenAiBaseUrl;
        config.ApiProtocol = TranslationProviderConfig.ProtocolOpenAi;
        config.ModelProtocols = null; // 标准预设为单协议，无按模型覆盖
        return true;
    }

    /// <summary>
    /// 按模型解析生效协议：显式覆盖（配置或预设）优先，未命中回退预设默认协议；
    /// 无预设（自定义）返回 null（不臆造协议）。
    /// </summary>
    public static string? ModelProtocol(
        string? presetId,
        string? model,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var name = model?.Trim() ?? "";
        if (name.Length == 0) return null;

        if (overrides is not null && overrides.TryGetValue(name, out var explicitOverride))
        {
            return NormalizeProtocol(explicitOverride);
        }

        var preset = TranslationProviderPreset.Find(presetId);
        if (preset is null) return null;

        if (preset.DefaultModelProtocols is { Count: > 0 } map && map.TryGetValue(name, out var presetProtocol))
        {
            return NormalizeProtocol(presetProtocol);
        }

        return NormalizeProtocol(preset.DefaultProtocol);
    }

    /// <summary>
    /// 模型行协议徽章（<c>OpenAI</c> / <c>Anthropic</c>）：仅混合协议供应商（或带显式覆盖的配置）需要，
    /// 其余返回 null 表示不展示徽章。已下线模型不臆造徽章（返回 null）。
    /// </summary>
    public static string? ModelProtocolBadge(
        string? presetId,
        string? model,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (IsRetiredModel(model)) return null;
        if (!IsMixedProtocol(presetId) && overrides is not { Count: > 0 }) return null;

        // 空 / 未知模型不臆造徽章（ModelProtocol 对空模型名返回 null）。
        return ModelProtocol(presetId, model, overrides) switch
        {
            null => null,
            TranslationProviderConfig.ProtocolAnthropic => "Anthropic",
            _ => "OpenAI",
        };
    }

    /// <summary>
    /// 模型 id 是否已下线：命中 Kimi 退役清单，或匹配 <c>moonshot-v1-*</c> 前缀（大小写不敏感）。
    /// </summary>
    public static bool IsRetiredModel(string? model)
    {
        var name = model?.Trim() ?? "";
        if (name.Length == 0) return false;

        if (RetiredKimiModelIds.Any(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase))) return true;
        return name.StartsWith("moonshot-v1-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 失败态分类（P38–P41）。优先级：已下线模型 → 1113 额度 → 协议不匹配（400/404/405）→
    /// 密钥不匹配（401/403）→ 无法归类。不读取响应正文之外的任何内容，结论仅用于选择横幅文案。
    /// </summary>
    public static TranslationProviderFailureKind ClassifyFailure(
        int? httpStatus,
        string? serverCodeOrMessage,
        string? model)
    {
        if (IsRetiredModel(model)) return TranslationProviderFailureKind.RetiredModel;

        if ((serverCodeOrMessage ?? "").Contains("1113", StringComparison.Ordinal))
        {
            return TranslationProviderFailureKind.QuotaExceeded;
        }

        return httpStatus switch
        {
            400 or 404 or 405 => TranslationProviderFailureKind.ProtocolMismatch,
            401 or 403 => TranslationProviderFailureKind.CredentialMismatch,
            _ => TranslationProviderFailureKind.None,
        };
    }

    /// <summary>失败分类对应的用户可见横幅文案；<see cref="TranslationProviderFailureKind.None"/> 返回 null。</summary>
    public static string? FailureBanner(TranslationProviderFailureKind kind) => kind switch
    {
        TranslationProviderFailureKind.QuotaExceeded => QuotaExceededBanner,
        TranslationProviderFailureKind.ProtocolMismatch => ProtocolMismatchBanner,
        TranslationProviderFailureKind.CredentialMismatch => CredentialMismatchBanner,
        TranslationProviderFailureKind.RetiredModel => RetiredModelBanner,
        _ => null,
    };
}
