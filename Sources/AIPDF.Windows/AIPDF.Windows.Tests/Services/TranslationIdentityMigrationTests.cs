// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T8 翻译身份迁移测试：
//   - 旧版按「显示名」寻址的凭据 → 迁移到稳定身份键后仍可取到同一密钥（用户无需重填）；
//   - 幂等：重复迁移零动作、零重复条目；
//   - 同意记录 provider 字段改写为稳定 id（含端点兜底命中的旧别名）；
//   - 一致性：凭据寻址与同意匹配同源（TargetName 收敛 == 同意收敛）；
//   - 红线：迁移过程绝不把密钥写入设置 / 日志文本。
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationIdentityMigrationTests
{
    private const string Key = "sk-t8-migration-secret";
    private const string Endpoint = "https://api.deepseek.com/v1";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public SettingsStore Settings { get; }
        public InMemoryCredentialBackend Backend { get; }
        public TranslationCredentialStore Credentials { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-t8-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new SettingsStore(Path.Combine(Root, "settings.json"));
            Backend = new InMemoryCredentialBackend();
            Credentials = new TranslationCredentialStore(Backend);
        }

        public TranslationIdentityMigrator NewMigrator() => new(Settings, Credentials);

        public void SaveProviders(params TranslationProviderConfig[] providers)
        {
            var settings = Settings.Load();
            settings.TranslationProviders = [.. providers];
            Settings.Save(settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>旧版（v1.1）凭据目标名：<c>AIPDF-Translate-{显示名}-{sha256(origin)[:16]}</c>（大小写敏感）。</summary>
    private static string LegacyTarget(string providerName, string endpoint)
    {
        var origin = TranslationConsent.NormalizeEndpoint(endpoint);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(origin));
        var shortHash = Convert.ToHexString(hash).ToLowerInvariant()[..TranslationConsent.FingerprintLength];
        return TranslationCredentialStore.TargetPrefix + providerName + "-" + shortHash;
    }

    // ---------------------------------------------------------------- 凭据迁移

    [Fact]
    public void Migrate_LegacyDisplayNameCredential_RekeysToStableId_KeyPreserved()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "DeepSeek", Endpoint = Endpoint, Model = "deepseek-chat" });
        fx.Backend.Write(LegacyTarget("DeepSeek", Endpoint), Key); // v1.1 旧地址（显示名原样）

        var result = fx.NewMigrator().Migrate();

        Assert.Equal(1, result.Credentials);
        Assert.True(result.Changed);
        Assert.Equal(Key, fx.Credentials.Get("deepseek", Endpoint));      // 稳定 id 可取
        Assert.Equal(Key, fx.Credentials.Get("DeepSeek", Endpoint));      // 显示名（同一收敛）也可取
        Assert.False(fx.Backend.Contains(LegacyTarget("DeepSeek", Endpoint))); // 旧地址已清理
        Assert.Equal(1, fx.Backend.Count);
    }

    [Fact]
    public void Migrate_EndpointMatchedLegacyName_RekeysToPresetId()
    {
        using var fx = new Fixture();
        // 旧数据：名字是别名 "ds"，端点命中 DeepSeek 预设 → T2 回填 PresetId=deepseek。
        fx.SaveProviders(new TranslationProviderConfig { Provider = "ds", Endpoint = Endpoint, Model = "deepseek-chat" });
        Assert.Equal("deepseek", fx.Settings.Load().TranslationProviders[0].PresetId);
        fx.Backend.Write(LegacyTarget("ds", Endpoint), Key);

        var result = fx.NewMigrator().Migrate();

        Assert.Equal(1, result.Credentials);
        Assert.Equal(Key, fx.Credentials.Get("deepseek", Endpoint));
        Assert.False(fx.Backend.Contains(LegacyTarget("ds", Endpoint)));
    }

    [Fact]
    public void Migrate_LegacyCredentialWithoutProviderConfig_FallsBackToPresetNameMapping()
    {
        using var fx = new Fixture();
        const string endpoint = "https://api.openai.com/v1";
        fx.Backend.Write(LegacyTarget("OpenAI", endpoint), Key); // 供应商已从设置移除，只剩凭据

        var result = fx.NewMigrator().Migrate();

        Assert.Equal(1, result.Credentials);
        Assert.Equal(Key, fx.Credentials.Get("openai", endpoint)); // 显示名 "OpenAI" → 稳定 id "openai"
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "DeepSeek", Endpoint = Endpoint, Model = "deepseek-chat" });
        fx.Backend.Write(LegacyTarget("DeepSeek", Endpoint), Key);

        var first = fx.NewMigrator().Migrate();
        var countAfterFirst = fx.Backend.Count;
        var second = fx.NewMigrator().Migrate();

        Assert.Equal(1, first.Credentials);
        Assert.Equal(0, second.Credentials); // 重复迁移零动作
        Assert.False(second.Changed);
        Assert.Equal(countAfterFirst, fx.Backend.Count);
        Assert.Equal(Key, fx.Credentials.Get("deepseek", Endpoint));
    }

    [Fact]
    public void Migrate_NewTargetAlreadyExists_DoesNotOverwrite_ButCleansLegacy()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "DeepSeek", Endpoint = Endpoint, Model = "deepseek-chat" });
        fx.Backend.Write(LegacyTarget("DeepSeek", Endpoint), "sk-old-stale");
        fx.Backend.Write(TranslationCredentialStore.TargetName("deepseek", Endpoint), "sk-new-authoritative");

        var result = fx.NewMigrator().Migrate();

        Assert.Equal(1, result.Credentials);
        Assert.Equal("sk-new-authoritative", fx.Credentials.Get("deepseek", Endpoint)); // 新目标为准，绝不覆盖
        Assert.False(fx.Backend.Contains(LegacyTarget("DeepSeek", Endpoint)));
        Assert.Equal(1, fx.Backend.Count);
    }

    [Fact]
    public void Migrate_SkipsForeignAndMalformedTargets()
    {
        using var fx = new Fixture();
        fx.Backend.Write("Other-App-Credential", "keep-me");
        fx.Backend.Write(TranslationCredentialStore.TargetPrefix + "broken-no-hash", "leave-me");

        var result = fx.NewMigrator().Migrate();

        Assert.Equal(0, result.Credentials);
        Assert.Equal("keep-me", fx.Backend.Read("Other-App-Credential"));
        Assert.Equal("leave-me", fx.Backend.Read(TranslationCredentialStore.TargetPrefix + "broken-no-hash"));
    }

    // ---------------------------------------------------------------- 同意迁移

    [Fact]
    public void Migrate_ConsentRecord_RewritesProviderToStableId()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "ds", Endpoint = Endpoint, Model = "deepseek-chat" });
        var settings = fx.Settings.Load();
        settings.TranslationConsents.Add(new TranslationConsent
        {
            Provider = "ds",
            Endpoint = TranslationConsent.NormalizeEndpoint(Endpoint),
            KeyFingerprint = TranslationConsent.ComputeFingerprint(Key),
            GrantedAt = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            TtlDays = TranslationConsent.DefaultTtlDays,
        });
        fx.Settings.Save(settings);
        Assert.Equal("ds", Assert.Single(fx.Settings.Load().TranslationConsents).Provider); // 迁移前保留旧别名

        var result = fx.NewMigrator().Migrate();

        Assert.Equal(1, result.Consents);
        Assert.Equal("deepseek", Assert.Single(fx.Settings.Load().TranslationConsents).Provider);
    }

    [Fact]
    public void Migrate_Consent_MakesRuntimeConsentCheckSucceedUnderStableId()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "ds", Endpoint = Endpoint, Model = "deepseek-chat" });
        var settings = fx.Settings.Load();
        settings.TranslationConsents.Add(new TranslationConsent
        {
            Provider = "ds",
            Endpoint = TranslationConsent.NormalizeEndpoint(Endpoint),
            KeyFingerprint = TranslationConsent.ComputeFingerprint(Key),
            GrantedAt = DateTimeOffset.UtcNow,
            TtlDays = TranslationConsent.DefaultTtlDays,
        });
        fx.Settings.Save(settings);

        fx.NewMigrator().Migrate();

        var consent = new TranslationConsentService(fx.Settings, fx.Credentials);
        Assert.True(consent.IsConsented("deepseek", Endpoint, Key)); // 迁移后按稳定 id 命中，不再重复征求同意
    }

    [Fact]
    public void Save_CanonicalizesConsentProvider_WithoutMigrator()
    {
        using var fx = new Fixture();
        var settings = fx.Settings.Load();
        settings.TranslationConsents.Add(new TranslationConsent
        {
            Provider = "DeepSeek", // 显示名（大小写混合）
            Endpoint = TranslationConsent.NormalizeEndpoint(Endpoint),
            KeyFingerprint = "0123456789abcdef",
            GrantedAt = DateTimeOffset.UtcNow,
            TtlDays = TranslationConsent.DefaultTtlDays,
        });

        fx.Settings.Save(settings);

        Assert.Equal("deepseek", Assert.Single(fx.Settings.Load().TranslationConsents).Provider);
    }

    [Fact]
    public void Migrate_ConsentsOnly_IsIdempotent()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "ds", Endpoint = Endpoint, Model = "deepseek-chat" });
        var settings = fx.Settings.Load();
        settings.TranslationConsents.Add(new TranslationConsent
        {
            Provider = "ds",
            Endpoint = TranslationConsent.NormalizeEndpoint(Endpoint),
            KeyFingerprint = "0123456789abcdef",
            GrantedAt = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            TtlDays = TranslationConsent.DefaultTtlDays,
        });
        fx.Settings.Save(settings);

        var first = fx.NewMigrator().Migrate();
        var second = fx.NewMigrator().Migrate();

        Assert.Equal(1, first.Consents);
        Assert.Equal(0, second.Consents);
        Assert.False(second.Changed);
    }

    // ---------------------------------------------------------------- 红线 / 一致性

    [Fact]
    public void Migrate_DoesNotWriteSecretIntoSettings()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "DeepSeek", Endpoint = Endpoint, Model = "deepseek-chat" });
        fx.Backend.Write(LegacyTarget("DeepSeek", Endpoint), Key);

        fx.NewMigrator().Migrate();

        var raw = File.ReadAllText(fx.Settings.FilePath);
        Assert.DoesNotContain(Key, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialTarget_AndConsentMatching_ShareIdentitySource()
    {
        using var fx = new Fixture();
        const string endpoint = "https://api.openai.com/v1";

        // 显示名 / 大小写不同的同一预设 → 同一凭据目标名与同一同意命中。
        Assert.Equal(
            TranslationCredentialStore.TargetName("OpenAI", endpoint),
            TranslationCredentialStore.TargetName("openai", endpoint));
        Assert.Equal(
            TranslationCredentialStore.TargetName("openai", endpoint),
            TranslationCredentialStore.TargetName("OPENAI", endpoint));

        fx.Credentials.Store("OpenAI", endpoint, Key);
        fx.NewMigrator(); // 无设置条目：仅走名称兜底映射
        Assert.Equal(Key, fx.Credentials.Get("openai", endpoint));

        var service = new TranslationConsentService(fx.Settings, fx.Credentials);
        service.GrantConsent("OpenAI", endpoint, Key);
        Assert.True(service.IsConsentedUsingStoredKey("openai", endpoint));
        Assert.True(service.IsConsentedUsingStoredKey("OPENAI", endpoint));
    }
}
