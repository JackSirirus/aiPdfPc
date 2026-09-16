// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端同意判定测试（决策 #14/#15/#21；云端配置复用 (c)(d)(g)(i)）：
//   - provider + endpoint origin + 密钥指纹三者全等且 TTL 未过期才算已同意；
//   - 协议 / 主机 / 端口 / 密钥任一变更 → 未同意；仅 path（模型）变更 → 仍同意；
//   - 撤销同意可同时清除凭据；同意记录只落设置、绝不落密钥；
//   - 漫游：凭据管理器中的 key 与同意指纹不一致 → 视为未同意。
using System.IO;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationConsentServiceTests
{
    private const string Provider = "deepseek";
    private const string Endpoint = "https://api.deepseek.com/v1";
    private const string EndpointSameOrigin = "https://api.deepseek.com/v2/chat";
    private const string EndpointProtocolChanged = "http://api.deepseek.com/v1";
    private const string EndpointPortChanged = "https://api.deepseek.com:8443/v1";
    private const string EndpointHostChanged = "https://api.moonshot.cn/v1";
    private const string Key = "sk-t10-consent-secret";
    private const string OtherKey = "sk-t10-consent-other";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public SettingsStore Settings { get; }
        public InMemoryCredentialBackend Backend { get; }
        public TranslationCredentialStore Credentials { get; }
        public TranslationConsentService Service { get; }
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-t10-consent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new SettingsStore(Path.Combine(Root, "settings.json"));
            Backend = new InMemoryCredentialBackend();
            Credentials = new TranslationCredentialStore(Backend);
            Service = NewService();
        }

        public TranslationConsentService NewService() => new(Settings, Credentials, () => Now);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 默认与授予

    [Fact]
    public void Default_NotConsented()
    {
        using var fx = new Fixture();
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, Key));
        Assert.Null(fx.Service.FindValidConsent(Provider, Endpoint, Key));
    }

    [Fact]
    public void Grant_Then_Consented_AndNormalizesEndpoint()
    {
        using var fx = new Fixture();
        var record = fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.True(fx.Service.IsConsented(Provider, Endpoint, Key));
        Assert.True(fx.Service.IsConsented(Provider, EndpointSameOrigin, Key)); // path 不影响
        Assert.Equal("https://api.deepseek.com", record.Endpoint);
        Assert.Equal(TranslationConsent.ComputeFingerprint(Key), record.KeyFingerprint);
        Assert.Equal(TranslationConsent.DefaultTtlDays, record.TtlDays);
    }

    [Fact]
    public void Grant_DoesNotTouchCredentialStore()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.Equal(0, fx.Backend.Count); // 同意与凭据分离：同意服务不保存密钥
    }

    [Fact]
    public void Grant_RejectsMissingArguments()
    {
        using var fx = new Fixture();
        Assert.Throws<ArgumentException>(() => fx.Service.GrantConsent("", Endpoint, Key));
        Assert.Throws<ArgumentException>(() => fx.Service.GrantConsent(Provider, "", Key));
        Assert.Throws<ArgumentException>(() => fx.Service.GrantConsent(Provider, Endpoint, null));
        Assert.Throws<ArgumentException>(() => fx.Service.GrantConsent(Provider, Endpoint, ""));
    }

    // ---------------------------------------------------------------- 重新同意触发（决策 (d)）

    [Fact]
    public void ChangedKey_RequiresReconsent()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.True(fx.Service.IsConsented(Provider, Endpoint, Key));
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, OtherKey));
    }

    [Fact]
    public void ChangedProtocolHostOrPort_RequiresReconsent()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.False(fx.Service.IsConsented(Provider, EndpointProtocolChanged, Key));
        Assert.False(fx.Service.IsConsented(Provider, EndpointHostChanged, Key));
        Assert.False(fx.Service.IsConsented(Provider, EndpointPortChanged, Key));
    }

    [Fact]
    public void ChangedProvider_RequiresReconsent()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.False(fx.Service.IsConsented("moonshot", Endpoint, Key));
    }

    [Fact]
    public void ProviderMatch_IsCaseInsensitive()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent("DeepSeek", Endpoint, Key);

        Assert.True(fx.Service.IsConsented("deepseek", Endpoint, Key));
        Assert.True(fx.Service.IsConsented("DEEPSEEK", EndpointSameOrigin, Key));
    }

    [Fact]
    public void ProviderDisplayName_MatchesStableId()
    {
        using var fx = new Fixture();
        const string endpoint = "https://api.openai.com/v1";

        // T8：内置预设显示名与稳定 id 收敛为同一身份键 —— 授予与校验互见。
        fx.Credentials.Store("OpenAI", endpoint, Key);
        fx.Service.GrantConsent("OpenAI", endpoint, Key);

        Assert.True(fx.Service.IsConsented("openai", endpoint, Key));
        Assert.True(fx.Service.IsConsented("OPENAI", endpoint, Key));
        Assert.True(fx.Service.IsConsentedUsingStoredKey("openai", endpoint)); // 与凭据寻址同源
        Assert.Equal("openai", Assert.Single(fx.Settings.Load().TranslationConsents).Provider);
    }

    [Fact]
    public void MissingOrBlankKey_NotConsented()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.False(fx.Service.IsConsented(Provider, Endpoint, null));
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, ""));
    }

    // ---------------------------------------------------------------- TTL（决策 (g)）

    [Fact]
    public void Consent_ExpiresAfterTtl()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key, ttlDays: 180);

        fx.Now = fx.Now.AddDays(179);
        Assert.True(fx.Service.IsConsented(Provider, Endpoint, Key));

        fx.Now = fx.Now.AddDays(2); // 共 181 天
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, Key));
    }

    // ---------------------------------------------------------------- 跨会话复用（决策 (a)(c)）

    [Fact]
    public void Consent_PersistsAcrossServiceInstances()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        var reopened = fx.NewService();
        Assert.True(reopened.IsConsented(Provider, EndpointSameOrigin, Key));
    }

    [Fact]
    public void Grant_RememberFalse_IsSessionOnly()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key, remember: false);

        Assert.True(fx.Service.IsConsented(Provider, Endpoint, Key));
        Assert.False(fx.NewService().IsConsented(Provider, Endpoint, Key)); // 未持久化
        Assert.False(File.Exists(fx.Settings.FilePath)); // 也未写设置
    }

    [Fact]
    public void Grant_ReplacesPreviousRecordForSameDestination()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);
        fx.Service.GrantConsent(Provider, Endpoint, OtherKey);

        Assert.Single(fx.Settings.Load().TranslationConsents);
        Assert.True(fx.Service.IsConsented(Provider, Endpoint, OtherKey));
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, Key));
    }

    // ---------------------------------------------------------------- 撤销（决策 (g)）

    [Fact]
    public void Revoke_RemovesConsent()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.True(fx.Service.RevokeConsent(Provider, Endpoint));
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, Key));
        Assert.Empty(fx.Settings.Load().TranslationConsents);
    }

    [Fact]
    public void Revoke_AlsoClearsCredential_WhenRequested()
    {
        using var fx = new Fixture();
        fx.Credentials.Store(Provider, Endpoint, Key);
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        Assert.True(fx.Service.RevokeConsent(Provider, Endpoint, alsoClearCredential: true));
        Assert.Null(fx.Credentials.Get(Provider, Endpoint));
        Assert.False(fx.Service.IsConsented(Provider, Endpoint, Key));
    }

    [Fact]
    public void Revoke_KeepsCredential_WhenNotRequested()
    {
        using var fx = new Fixture();
        fx.Credentials.Store(Provider, Endpoint, Key);
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        fx.Service.RevokeConsent(Provider, Endpoint);

        Assert.Equal(Key, fx.Credentials.Get(Provider, Endpoint)); // 仅补 key 场景：凭据保留
    }

    [Fact]
    public void Revoke_Unknown_ReturnsFalse()
    {
        using var fx = new Fixture();
        Assert.False(fx.Service.RevokeConsent(Provider, Endpoint));
        Assert.False(fx.Service.RevokeConsent("", Endpoint));
    }

    [Fact]
    public void Revoke_IncrementsGenerationOnlyWhenEffective()
    {
        using var fx = new Fixture();
        var baseline = fx.Service.Generation;

        fx.Service.GrantConsent(Provider, Endpoint, Key);
        Assert.Equal(baseline, fx.Service.Generation); // 授予同意不递增（运行中任务无需停止）

        Assert.True(fx.Service.RevokeConsent(Provider, Endpoint));
        Assert.Equal(baseline + 1, fx.Service.Generation); // 撤销生效 → 递增，在途任务据此取消

        Assert.False(fx.Service.RevokeConsent(Provider, Endpoint)); // 无实际变化
        Assert.Equal(baseline + 1, fx.Service.Generation);
    }

    [Fact]
    public void Revoke_AlsoClearsCredential_IncrementsGeneration()
    {
        using var fx = new Fixture();
        fx.Credentials.Store(Provider, Endpoint, Key);
        var baseline = fx.Service.Generation;

        Assert.True(fx.Service.RevokeConsent(Provider, Endpoint, alsoClearCredential: true));

        Assert.Equal(baseline + 1, fx.Service.Generation);
    }

    // ---------------------------------------------------------------- 漫游指纹校验（决策 (i)）

    [Fact]
    public void StoredCredentialFingerprint_MustMatchConsent()
    {
        using var fx = new Fixture();
        fx.Credentials.Store(Provider, Endpoint, Key);
        fx.Service.GrantConsent(Provider, Endpoint, Key);
        Assert.True(fx.Service.IsConsentedUsingStoredKey(Provider, Endpoint));

        // 换机 / 凭据被替换成不同 key → 指纹不匹配 → 视为未同意
        fx.Credentials.Store(Provider, Endpoint, OtherKey);
        Assert.False(fx.Service.IsConsentedUsingStoredKey(Provider, Endpoint));
    }

    [Fact]
    public void MissingStoredCredential_NotConsentedUsingStoredKey()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);
        Assert.False(fx.Service.IsConsentedUsingStoredKey(Provider, Endpoint)); // 凭据不存在
    }

    // ---------------------------------------------------------------- 红线：密钥绝不落盘

    [Fact]
    public void Settings_PersistConsentWithoutSecret()
    {
        using var fx = new Fixture();
        fx.Service.GrantConsent(Provider, Endpoint, Key);

        var raw = File.ReadAllText(fx.Settings.FilePath);
        Assert.DoesNotContain(Key, raw, StringComparison.Ordinal);          // 密钥本体不得出现
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyFingerprint", raw, StringComparison.Ordinal);   // 只允许指纹
        Assert.Contains(TranslationConsent.ComputeFingerprint(Key), raw, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationConsent_Model_HasNoSecretBearingMember()
    {
        var properties = typeof(TranslationConsent).GetProperties();

        Assert.DoesNotContain(properties, p =>
            p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name, "key", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name, "apiKey", StringComparison.OrdinalIgnoreCase));
    }
}
