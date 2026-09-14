// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端同意判定（决策 #14 / #15；云端配置复用 (c)(d)(g)(i)）：
//   - 同意绑定「provider + endpoint origin + 密钥指纹」三者全等且 TTL 未过期，才视为已同意；
//   - endpoint 主机名 / 协议 / 端口变更（origin 变）或密钥指纹变化 → 未同意，需重新征求；
//   - 仅改模型名（origin 与密钥不变）不重置同意；
//   - 「记住我的选择」勾选 → 落 AppSettings；未勾选 → 仅本次会话内存有效（每次会话首次提示一次）；
//   - 撤销同意可「同时清除凭据」；同意记录只落设置文件，密钥只落凭据管理器。
//
// 红线（决策 #21）：本类不把密钥写入设置 / 日志 / argv / 环境变量；设置里只出现指纹。
namespace AIPDF.Windows.Services;

/// <summary>
/// 云端同意服务：读写 <see cref="AppSettings.TranslationConsents"/>（持久同意）并判定当前请求是否已获同意。
/// 与凭据存储协作完成「撤销同意（可同时清除凭据）」，供 T17（设置 UI）与 T5（LLM 客户端）消费。
/// </summary>
public sealed class TranslationConsentService
{
    private readonly SettingsStore _settings;
    private readonly TranslationCredentialStore _credentials;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, TranslationConsent> _sessionOnly = new(StringComparer.Ordinal);

    /// <summary>默认使用系统时钟；测试注入固定时钟。</summary>
    public TranslationConsentService(
        SettingsStore settings,
        TranslationCredentialStore credentials,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 该 <paramref name="provider"/> + <paramref name="endpoint"/> + <paramref name="apiKey"/> 是否已获同意。
    /// 三者任一不匹配、密钥为空、或记录过期均返回 false。
    /// </summary>
    public bool IsConsented(string provider, string endpoint, string? apiKey)
        => FindValidConsent(provider, endpoint, apiKey) is not null;

    /// <summary>
    /// 用凭据管理器中实际保存的密钥做「漫游校验」（决策 (i)）：同意记录中的指纹与机器上真实密钥不一致
    /// （换机 / 凭据被替换）时视为未同意。keystore 无密钥直接返回 false。
    /// </summary>
    public bool IsConsentedUsingStoredKey(string provider, string endpoint)
    {
        var key = _credentials.Get(provider, endpoint);
        return !string.IsNullOrEmpty(key) && IsConsented(provider, endpoint, key);
    }

    /// <summary>返回命中的有效同意记录（会话内存优先，其次持久记录）；无则 null。</summary>
    public TranslationConsent? FindValidConsent(string provider, string endpoint, string? apiKey)
    {
        var normalizedProvider = provider?.Trim() ?? "";
        var normalizedEndpoint = TranslationConsent.NormalizeEndpoint(endpoint);
        var fingerprint = TranslationConsent.ComputeFingerprint(apiKey);
        if (normalizedProvider.Length == 0 || normalizedEndpoint.Length == 0 || fingerprint.Length == 0)
        {
            return null;
        }

        if (_sessionOnly.TryGetValue(SessionKey(normalizedProvider, normalizedEndpoint), out var session)
            && Matches(session, normalizedProvider, normalizedEndpoint, fingerprint))
        {
            return session;
        }

        foreach (var consent in _settings.Load().TranslationConsents)
        {
            if (Matches(consent, normalizedProvider, normalizedEndpoint, fingerprint))
            {
                return consent;
            }
        }

        return null;
    }

    /// <summary>
    /// 记录一次明确同意。<paramref name="remember"/> 为 true 时写入设置跨会话复用（同目的地旧记录被替换）；
    /// 为 false 时仅本次会话内存有效（决策 (c)：未勾选「记住」则每次会话首次使用提示一次）。
    /// </summary>
    public TranslationConsent GrantConsent(
        string provider,
        string endpoint,
        string? apiKey,
        int? ttlDays = null,
        bool remember = true)
    {
        var normalizedProvider = provider?.Trim() ?? "";
        if (normalizedProvider.Length == 0)
        {
            throw new ArgumentException("服务商标识不能为空。", nameof(provider));
        }

        var normalizedEndpoint = TranslationConsent.NormalizeEndpoint(endpoint);
        if (normalizedEndpoint.Length == 0)
        {
            throw new ArgumentException("端点不能为空。", nameof(endpoint));
        }

        var fingerprint = TranslationConsent.ComputeFingerprint(apiKey);
        if (fingerprint.Length == 0)
        {
            throw new ArgumentException("密钥不能为空。", nameof(apiKey));
        }

        var record = new TranslationConsent
        {
            Provider = normalizedProvider,
            Endpoint = normalizedEndpoint,
            KeyFingerprint = fingerprint,
            GrantedAt = _clock(),
            TtlDays = TranslationConsent.NormalizeTtlDays(ttlDays ?? TranslationConsent.DefaultTtlDays),
        };

        if (remember)
        {
            var settings = _settings.Load();
            settings.TranslationConsents.RemoveAll(c => SameDestination(c, normalizedProvider, normalizedEndpoint));
            settings.TranslationConsents.Add(record);
            _settings.Save(settings);
        }
        else
        {
            _sessionOnly[SessionKey(normalizedProvider, normalizedEndpoint)] = record;
        }

        return record;
    }

    /// <summary>
    /// 撤销某目的地同意。<paramref name="alsoClearCredential"/> 为 true 时同时删除凭据管理器中的密钥（决策 (g)）。
    /// 返回是否确有移除或清除动作。
    /// </summary>
    public bool RevokeConsent(string provider, string endpoint, bool alsoClearCredential = false)
    {
        var normalizedProvider = provider?.Trim() ?? "";
        var normalizedEndpoint = TranslationConsent.NormalizeEndpoint(endpoint);
        if (normalizedProvider.Length == 0 || normalizedEndpoint.Length == 0) return false;

        var removed = _sessionOnly.Remove(SessionKey(normalizedProvider, normalizedEndpoint));

        var settings = _settings.Load();
        var count = settings.TranslationConsents.Count;
        settings.TranslationConsents.RemoveAll(c => SameDestination(c, normalizedProvider, normalizedEndpoint));
        if (settings.TranslationConsents.Count != count)
        {
            _settings.Save(settings);
            removed = true;
        }

        if (alsoClearCredential)
        {
            removed |= _credentials.Delete(normalizedProvider, normalizedEndpoint);
        }

        return removed;
    }

    private bool Matches(TranslationConsent consent, string provider, string endpoint, string fingerprint)
        => !consent.IsExpired(_clock())
           && string.Equals(consent.Provider, provider, StringComparison.OrdinalIgnoreCase)
           && string.Equals(consent.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)
           && string.Equals(consent.KeyFingerprint, fingerprint, StringComparison.Ordinal);

    private static bool SameDestination(TranslationConsent consent, string provider, string endpoint)
        => string.Equals(consent.Provider, provider, StringComparison.OrdinalIgnoreCase)
           && string.Equals(consent.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase);

    private static string SessionKey(string provider, string endpoint)
        => provider.ToLowerInvariant() + "\n" + endpoint;
}
