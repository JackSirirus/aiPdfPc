// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 生产运行时配置提供者（补齐 T24 §8-① 的阻断级缺口）：
//   - 从 AppSettings.TranslationProviders 读取服务商 / 端点 / 模型，从 TranslationCredentialStore 读取密钥，
//     经 TranslationConsentService 校验云端同意，组装真正的 TranslationRuntimeConfig 交划词 / 整篇 / 批量；
//   - 任一项未就绪时返回带「具体原因」的解析结果（无服务商 / 无端点 / 非 loopback / 无凭据 / 未同意），
//     让 UI 能给出可操作的引导，而不是笼统的「尚未配置」；
//   - 端点标记为「本地」时必须是 loopback（127.0.0.1 / ::1 / localhost），非 loopback 一律拒绝，
//     防止以本地名义绕过云端同意（需求总结 §本地引擎 loopback 校验）；
//   - 云端在「无凭据 / 未同意」时不返回配置 → 调用方不会发起任何云端请求。
//
// 红线（决策 #9 / #21）：本类不写任何持久化；密钥只在「凭据管理器 → 本类 → TranslationRuntimeConfig → 子进程 stdin」
// 之间传递，绝不进 settings / 日志 / argv / 环境变量。同意只落 provider + endpoint + 指纹。
namespace AIPDF.Windows.Services;

/// <summary>
/// 生产运行时配置提供者：读设置 + 凭据管理器 + 同意记录，产出可用的 <see cref="TranslationRuntimeConfig"/>。
/// 目的地由<b>面板 / 调用方选中的供应商</b>决定（v1.2 T7，修复「引擎单选不驱动目的地」）：按稳定
/// <see cref="TranslationProviderConfig.PresetId"/> 定位选中项；本地项必须 loopback，否则拒绝；云端要求端点 + 密钥 + 同意。
/// 未提供选择时保留确定性回退（优先「本地」，否则首个可用云端），供尚未接入选择的调用方沿用。
/// </summary>
public sealed class SettingsTranslationRuntimeConfigProvider : ITranslationRuntimeConfigProvider
{
    private readonly SettingsStore _settings;
    private readonly TranslationCredentialStore _credentials;
    private readonly TranslationConsentService _consent;

    /// <summary>生产构造：注入设置存储、凭据存储与同意服务（三者共享同一目的地寻址口径）。</summary>
    public SettingsTranslationRuntimeConfigProvider(
        SettingsStore settings,
        TranslationCredentialStore credentials,
        TranslationConsentService consent)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
    }

    /// <inheritdoc />
    public TranslationRuntimeConfig? Get() => Resolve().Config;

    /// <inheritdoc />
    public int ConsentGeneration => _consent.Generation;

    /// <inheritdoc />
    public TranslationRuntimeConfigResolution Resolve(string? selectedProviderId = null)
    {
        var settings = _settings.Load();
        var providers = settings.TranslationProviders;

        TranslationProviderConfig selected;
        if (!string.IsNullOrWhiteSpace(selectedProviderId))
        {
            // 面板 / 调用方显式选择：按稳定 id 精确定位目的地（不再回落其它供应商）。
            var found = FindSelected(providers, selectedProviderId.Trim());
            if (found is null)
            {
                return Missing(
                    TranslationRuntimeConfigStatus.NoProvider, selectedProviderId.Trim(), "", isCloud: false,
                    $"未找到选中的翻译服务商「{selectedProviderId.Trim()}」，请在「设置 → 翻译」中重新选择。");
            }

            selected = found;
        }
        else
        {
            // 兼容回退（尚未接入选择的调用方 / 旧行为）：只采用通过端点校验的条目（IsUsable）——
            // 归一化保留失效条目供用户修正，但运行时绝不采用内嵌密钥 / 非法 scheme 的端点（R5-3）。
            var usable = providers.Where(p => p is { IsUsable: true }).ToList();
            if (usable.Count == 0)
            {
                return Missing(
                    TranslationRuntimeConfigStatus.NoProvider, "", "", isCloud: false,
                    "翻译服务尚未配置，请在「设置 → 翻译」中添加服务商与端点后重试。");
            }

            selected = usable.FirstOrDefault(p => p.IsLocalService()) ?? usable[0];
        }

        // 本地（稳定 id local）：端点必须 loopback，否则拒绝（防以本地名义绕过云端同意）；密钥可空。
        if (selected.IsLocalService())
        {
            if (string.IsNullOrWhiteSpace(selected.Endpoint))
            {
                return Missing(
                    TranslationRuntimeConfigStatus.NoEndpoint, selected.Provider, "", isCloud: false,
                    $"服务商「{selected.Provider}」缺少端点地址，请在「设置 → 翻译」中补全后重试。");
            }

            if (!TranslationProviderConfig.IsLoopbackEndpoint(selected.Endpoint))
            {
                return Missing(
                    TranslationRuntimeConfigStatus.NotLoopback, selected.Provider, selected.Endpoint, isCloud: false,
                    TranslationProviderConfig.LoopbackViolationMessage(selected.Endpoint));
            }

            return Configured(settings, selected, ReadCredential(selected) ?? "");
        }

        // 云端：端点（含通过校验）→ 已存密钥 → 有效同意，逐项给出可操作原因。
        if (string.IsNullOrWhiteSpace(selected.Endpoint))
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoEndpoint, selected.Provider, "", isCloud: true,
                $"服务商「{selected.Provider}」缺少端点地址，请在「设置 → 翻译」中补全后重试。",
                TranslationProviderIdentity.For(selected));
        }

        if (selected.ValidationError is { } endpointError)
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoEndpoint, selected.Provider, selected.Endpoint, isCloud: true,
                endpointError,
                TranslationProviderIdentity.For(selected));
        }

        var key = ReadCredential(selected);
        if (string.IsNullOrEmpty(key))
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoCredential, selected.Provider, selected.Endpoint, isCloud: true,
                $"云端服务商「{selected.Provider}」尚未保存密钥，请在「设置 → 翻译」中填写 API Key 后重试。",
                TranslationProviderIdentity.For(selected));
        }

        if (!_consent.IsConsented(TranslationProviderIdentity.For(selected), selected.Endpoint, key))
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoConsent, selected.Provider, selected.Endpoint, isCloud: true,
                $"云端翻译需要你的明确同意（服务商：{selected.Provider}）。",
                TranslationProviderIdentity.For(selected));
        }

        return Configured(settings, selected, key);
    }

    /// <summary>
    /// 按稳定 id 定位选中供应商：优先匹配 <see cref="TranslationProviderConfig.PresetId"/>；
    /// 自定义 / 旧数据（无 presetId）回退按显示名 <see cref="TranslationProviderConfig.Provider"/> 匹配。
    /// </summary>
    private static TranslationProviderConfig? FindSelected(
        IReadOnlyList<TranslationProviderConfig> providers, string selectedProviderId)
        => providers.FirstOrDefault(p =>
               string.Equals(p.PresetId?.Trim(), selectedProviderId, StringComparison.OrdinalIgnoreCase))
           ?? providers.FirstOrDefault(p =>
               string.Equals(p.Provider?.Trim(), selectedProviderId, StringComparison.OrdinalIgnoreCase));

    private string? ReadCredential(TranslationProviderConfig provider)
    {
        try
        {
            // 凭据寻址按稳定身份键（T8）：与同意匹配同源，显示名 / 大小写不影响定位。
            return _credentials.Get(TranslationProviderIdentity.For(provider), provider.Endpoint);
        }
        catch (CredentialStoreUnavailableException)
        {
            // 系统凭据管理器不可用（GPO / 沙箱 / 服务账户）：视为「无凭据」，由 UI 引导，不崩溃。
            return null;
        }
    }

    private static TranslationRuntimeConfigResolution Configured(
        AppSettings settings, TranslationProviderConfig provider, string apiKey)
    {
        var config = new TranslationRuntimeConfig(
            Provider: provider.Provider.Trim(),
            Endpoint: provider.Endpoint.Trim(),
            Model: provider.Model?.Trim() ?? "",
            ApiKey: apiKey,
            SourceLang: settings.TranslationDefaultSourceLanguage,
            TargetLang: settings.TranslationDefaultTargetLanguage,
            Template: EngineTemplate(settings.TranslationTemplateId),
            // 协议随配置贯通（T4 契约：openai | anthropic；缺省 openai）。
            ApiProtocol: provider.ApiProtocol,
            GlossaryPath: string.IsNullOrWhiteSpace(settings.TranslationGlossaryPath)
                ? null
                : settings.TranslationGlossaryPath.Trim(),
            MaxTokens: settings.TranslationCostLimitEnabled
                ? (int)Math.Clamp(settings.TranslationCostLimitTokens, 1, int.MaxValue)
                : null);

        return new TranslationRuntimeConfigResolution(
            TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint,
            // 云端判定由稳定 id（local → 本地）派生，不再依赖可变的显示名。
            !provider.IsLocalService(), "",
            // 稳定身份键透传宿主：同意弹窗后的凭据读取 / 授予同意必须以它寻址（T8）。
            TranslationProviderIdentity.For(provider));
    }

    private static TranslationRuntimeConfigResolution Missing(
        TranslationRuntimeConfigStatus status, string provider, string endpoint, bool isCloud, string message,
        string providerId = "")
        => new(status, null, provider, endpoint, isCloud, message, providerId);

    /// <summary>
    /// 设置模板标识 → 引擎 <c>prompt.PRESET_TEMPLATES</c> 键名。与 <c>FullTranslationViewModel.ToEngineTemplate</c>
    /// 同口径：设置层「通用」为 <c>general</c>、引擎为 <c>generic</c>；自定义暂回退 <c>generic</c>。
    /// </summary>
    private static string EngineTemplate(string? id) => id switch
    {
        null or "" => "generic",
        "general" => "generic",
        "custom" => "generic",
        _ => id,
    };
}

/// <summary>
/// 发起云端请求前的同意门禁：解析运行时配置；若因「未同意」阻塞，则经宿主注入的确认委托弹 F5 同意模态，
/// 用户确认后重新解析（宿主已在确认时 <c>GrantConsent</c>）。<b>未同意绝不返回可用配置</b> → 不会发起任何云端请求。
/// </summary>
public static class TranslationRuntimeConsentFlow
{
    /// <summary>
    /// 解析配置并在需要时征求同意。<paramref name="confirmAsync"/> 为 null（测试 / 无 UI）时不做交互，
    /// 直接返回原解析结果（未同意即不可用）。确认后重新解析以拿到刚授予同意的配置。
    /// <paramref name="selectedProviderId"/> 透传给 <see cref="ITranslationRuntimeConfigProvider.Resolve"/>，
    /// 保证同意弹窗定位的是「选中的」目的地而非其它供应商。
    /// </summary>
    public static async Task<TranslationRuntimeConfigResolution> EnsureAsync(
        ITranslationRuntimeConfigProvider provider,
        Func<TranslationConsentRequest, Task<bool>>? confirmAsync,
        string? selectedProviderId = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var resolution = provider.Resolve(selectedProviderId);
        if (!resolution.NeedsConsent || confirmAsync is null)
        {
            return resolution;
        }

        var request = new TranslationConsentRequest(
            resolution.Provider, resolution.Endpoint, ProviderId: resolution.ProviderId);
        if (!await confirmAsync(request).ConfigureAwait(true))
        {
            return resolution;
        }

        return provider.Resolve(selectedProviderId);
    }
}
