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
/// 目的地选择规则（确定性）：优先「本地」服务商（若存在），否则取首个已保存的云端服务商。
/// 这契合「默认引擎 = 本地」（决策 #15）——本地运行时就绪即用，否则回落云端并强制同意。
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
    public TranslationRuntimeConfigResolution Resolve()
    {
        var settings = _settings.Load();
        var providers = settings.TranslationProviders.Where(p => p is { IsConfigured: true }).ToList();
        if (providers.Count == 0)
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoProvider, "", "", isCloud: false,
                "翻译服务尚未配置，请在「设置 → 翻译」中添加服务商与端点后重试。");
        }

        // 默认本地：存在「本地」服务商时优先它；其端点必须是 loopback，否则拒绝（防同意绕过）。
        var local = providers.FirstOrDefault(p => TranslationProviderConfig.IsLocalProvider(p.Provider));
        if (local is not null)
        {
            if (!TranslationProviderConfig.IsLoopbackEndpoint(local.Endpoint))
            {
                return Missing(
                    TranslationRuntimeConfigStatus.NotLoopback, local.Provider, local.Endpoint, isCloud: false,
                    TranslationProviderConfig.LoopbackViolationMessage(local.Endpoint));
            }

            return Configured(settings, local, ReadCredential(local) ?? "");
        }

        // 云端：首个已保存服务商为目的地；缺密钥 → 引导补 key；有 key 未同意 → 引导同意（宿主弹 F5）。
        var cloud = providers[0];
        if (string.IsNullOrWhiteSpace(cloud.Endpoint))
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoEndpoint, cloud.Provider, "", isCloud: true,
                $"服务商「{cloud.Provider}」缺少端点地址，请在「设置 → 翻译」中补全后重试。");
        }

        var key = ReadCredential(cloud);
        if (string.IsNullOrEmpty(key))
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoCredential, cloud.Provider, cloud.Endpoint, isCloud: true,
                $"云端服务商「{cloud.Provider}」尚未保存密钥，请在「设置 → 翻译」中填写 API Key 后重试。");
        }

        if (!_consent.IsConsented(cloud.Provider, cloud.Endpoint, key))
        {
            return Missing(
                TranslationRuntimeConfigStatus.NoConsent, cloud.Provider, cloud.Endpoint, isCloud: true,
                $"云端翻译需要你的明确同意（服务商：{cloud.Provider}）。");
        }

        return Configured(settings, cloud, key);
    }

    private string? ReadCredential(TranslationProviderConfig provider)
    {
        try
        {
            return _credentials.Get(provider.Provider, provider.Endpoint);
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
            GlossaryPath: string.IsNullOrWhiteSpace(settings.TranslationGlossaryPath)
                ? null
                : settings.TranslationGlossaryPath.Trim(),
            MaxTokens: settings.TranslationCostLimitEnabled
                ? (int)Math.Clamp(settings.TranslationCostLimitTokens, 1, int.MaxValue)
                : null);

        return new TranslationRuntimeConfigResolution(
            TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint,
            !TranslationProviderConfig.IsLocalProvider(config.Provider), "");
    }

    private static TranslationRuntimeConfigResolution Missing(
        TranslationRuntimeConfigStatus status, string provider, string endpoint, bool isCloud, string message)
        => new(status, null, provider, endpoint, isCloud, message);

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
    /// </summary>
    public static async Task<TranslationRuntimeConfigResolution> EnsureAsync(
        ITranslationRuntimeConfigProvider provider,
        Func<TranslationConsentRequest, Task<bool>>? confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var resolution = provider.Resolve();
        if (!resolution.NeedsConsent || confirmAsync is null)
        {
            return resolution;
        }

        var request = new TranslationConsentRequest(resolution.Provider, resolution.Endpoint);
        if (!await confirmAsync(request).ConfigureAwait(true))
        {
            return resolution;
        }

        return provider.Resolve();
    }
}
