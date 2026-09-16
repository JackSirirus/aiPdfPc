// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T13 自定义供应商表单模型（设置页主从双栏右栏）：
//   - 自定义供应商未命中任何内置预设，持久化时 PresetId 恒为 null（不享受预设的端点 / 模型自动填充
//     与合规便利）；
//   - API 地址逐字保留（只 Trim 两端空白，绝不塌陷为 origin）——带路径的自建网关
//     （如 https://gw.corp/llm/v1）必须原样保存；「请求基址」由
//     ProviderUrl.NormalizeRequestBase 在请求期按协议计算（T3 双归一化）；
//   - 协议收敛为 openai / anthropic（与引擎契约、SettingsStore.NormalizeApiProtocol 同口径）；
//   - 名称留空 → 「未命名供应商」（设计稿 S3 规格）；名称同时作为凭据 / 同意寻址的稳定身份
//     （TranslationProviderIdentity.For 对未命中预设的名称取小写规范名）——模型无独立「provider id」
//     字段，故右栏的 provider id 由名称派生展示；
//   - 纯逻辑、无 WPF 依赖，便于单测（WPF 像素级自动化不在范围内）。
using AIPDF.Windows.Services;

namespace AIPDF.Windows.Views.Settings;

/// <summary>
/// 自定义供应商编辑草稿：把右栏表单输入收敛为一条可持久化的
/// <see cref="TranslationProviderConfig"/>（<c>PresetId = null</c>、端点路径保留、协议归一）。
/// </summary>
public sealed class CustomProviderDraft
{
    /// <summary>名称为空时的占位显示名（设计稿 S3：显示「未命名供应商」）。</summary>
    public const string UnnamedDisplayName = "未命名供应商";

    /// <summary>用户填写的供应商名（同时作为定位身份；留空回退 <see cref="UnnamedDisplayName"/>）。</summary>
    public string Name { get; set; } = "";

    /// <summary>API 协议（<c>openai</c> | <c>anthropic</c>；写入时经归一化，未知值按 openai）。</summary>
    public string Protocol { get; set; } = TranslationProviderConfig.ProtocolOpenAi;

    /// <summary>API 地址（自由填写；保存时只 Trim，路径逐字保留）。</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>模型名（自由输入；「拉取」失败不覆盖此值）。</summary>
    public string Model { get; set; } = "";

    /// <summary>由已保存配置回填草稿（编辑既有自定义供应商）。</summary>
    public static CustomProviderDraft FromConfig(TranslationProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new CustomProviderDraft
        {
            Name = config.Provider ?? "",
            Protocol = config.ApiProtocol ?? TranslationProviderConfig.ProtocolOpenAi,
            Endpoint = config.Endpoint ?? "",
            Model = config.Model ?? "",
        };
    }

    /// <summary>界面显示名：名称去空后回退 <see cref="UnnamedDisplayName"/>。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? UnnamedDisplayName : Name.Trim();

    /// <summary>
    /// 派生 provider id（只读展示）：与运行时凭据 / 同意寻址同源
    /// （<see cref="TranslationProviderIdentity.For"/>）。自定义项 <c>PresetId = null</c>，
    /// 故取名称的小写规范名（名称恰为内置预设名时按预设稳定 id 收敛）。
    /// </summary>
    public string ProviderId
        => TranslationProviderIdentity.For(new TranslationProviderConfig { Provider = DisplayName, PresetId = null });

    /// <summary>
    /// 收敛为可持久化配置：<c>PresetId = null</c>；端点仅 Trim（路径保留，绝不塌陷为 origin）；
    /// 协议经 <see cref="SettingsStore.NormalizeApiProtocol"/> 归一为 openai / anthropic。
    /// </summary>
    public TranslationProviderConfig ToConfig()
        => new()
        {
            Provider = DisplayName,
            Endpoint = Endpoint?.Trim() ?? "",
            Model = Model?.Trim() ?? "",
            PresetId = null,
            ApiProtocol = SettingsStore.NormalizeApiProtocol(Protocol),
            ModelProtocols = null,
        };

    /// <summary>校验名称 + 端点（模型可空）；返回 null 表示通过，否则为完整提示文案。</summary>
    public string? Validate() => TranslationProviderConfig.Validate(DisplayName, Endpoint);

    /// <summary>
    /// P23 校验提示：手填模型名不在服务端返回的列表时返回提示文案，否则 null
    /// （模型为空 / 命中列表 / 未拉取或列表为空）。不阻止保存（设计稿 P23：仍可保存）。
    /// </summary>
    public static string? ModelListHint(string? model, IReadOnlyList<string>? serverModels)
    {
        var value = model?.Trim() ?? "";
        if (value.Length == 0 || serverModels is null || serverModels.Count == 0) return null;
        if (serverModels.Contains(value, StringComparer.Ordinal)) return null;

        var preview = string.Join("、", serverModels.Take(3)) + (serverModels.Count > 3 ? "…" : "");
        return $"该模型名不在服务端返回的列表中（列表含：{preview}）。仍可保存，但请确认拼写。";
    }
}
