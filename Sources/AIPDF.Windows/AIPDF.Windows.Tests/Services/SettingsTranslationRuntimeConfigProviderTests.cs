// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 生产运行时配置提供者测试（补齐 T24 §8-① 阻断缺口）：
//   - 无服务商 / 缺凭据 / 未同意分别给出具体原因，且「未同意」不产出可用配置（→ 不发云端请求）；
//   - 本地服务商必须 loopback，非 loopback 一律拒绝；
//   - 云端凭据 + 同意齐备时组装真实配置，密钥只在内存，绝不落 settings.json。
using System.IO;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class SettingsTranslationRuntimeConfigProviderTests
{
    private const string CloudProvider = "deepseek";
    private const string CloudEndpoint = "https://api.deepseek.com/v1";
    private const string Key = "sk-provider-test-secret";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public SettingsStore Settings { get; }
        public InMemoryCredentialBackend Backend { get; }
        public TranslationCredentialStore Credentials { get; }
        public TranslationConsentService Consent { get; }
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-provider-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new SettingsStore(Path.Combine(Root, "settings.json"));
            Backend = new InMemoryCredentialBackend();
            Credentials = new TranslationCredentialStore(Backend);
            Consent = new TranslationConsentService(Settings, Credentials, () => Now);
        }

        public SettingsTranslationRuntimeConfigProvider NewProvider() => new(Settings, Credentials, Consent);

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

    private static TranslationProviderConfig Cloud() => new()
    {
        Provider = CloudProvider,
        Endpoint = CloudEndpoint,
        Model = "deepseek-chat",
    };

    // ---------------------------------------------------------------- 未配置 / 缺项

    [Fact]
    public void Resolve_NoProviders_ReportsNoProvider()
    {
        using var fx = new Fixture();
        var provider = fx.NewProvider();

        var resolution = provider.Resolve();

        Assert.Equal(TranslationRuntimeConfigStatus.NoProvider, resolution.Status);
        Assert.False(resolution.IsConfigured);
        Assert.Null(provider.Get()); // 未配置 → Get() 仍为 null（既有调用方语义不变）
        Assert.Contains("尚未配置", resolution.Message);
    }

    [Fact]
    public void Resolve_CloudWithoutCredential_ReportsNoCredential()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());

        var resolution = fx.NewProvider().Resolve();

        Assert.Equal(TranslationRuntimeConfigStatus.NoCredential, resolution.Status);
        Assert.False(resolution.IsConfigured);
        Assert.Contains("尚未保存密钥", resolution.Message);
    }

    // ---------------------------------------------------------------- 本地 loopback

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://[::1]:1234/v1")]
    public void Resolve_LocalLoopback_IsConfigured_WithoutConsentOrKey(string endpoint)
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig { Provider = "local", Endpoint = endpoint, Model = "qwen" });

        var resolution = fx.NewProvider().Resolve();

        Assert.True(resolution.IsConfigured);
        Assert.False(resolution.IsCloud);
        Assert.Equal("local", resolution.Config!.Provider);
        Assert.Equal(endpoint, resolution.Config.Endpoint);
        Assert.Equal("", resolution.Config.ApiKey); // 本地运行时无需密钥
    }

    [Fact]
    public void Resolve_LocalNonLoopback_IsRejected_AsLoopbackViolation()
    {
        using var fx = new Fixture();
        fx.SaveProviders(new TranslationProviderConfig
        {
            Provider = "local",
            Endpoint = "https://remote.example.com:443/v1",
            Model = "m",
        });

        var resolution = fx.NewProvider().Resolve();

        Assert.Equal(TranslationRuntimeConfigStatus.NotLoopback, resolution.Status);
        Assert.False(resolution.IsConfigured);
        Assert.Contains("不是本机地址", resolution.Message);
        Assert.Contains("127.0.0.1 / ::1 / localhost", resolution.Message);
    }

    [Fact]
    public void Resolve_PrefersLocalOverCloud()
    {
        using var fx = new Fixture();
        fx.SaveProviders(
            Cloud(),
            new TranslationProviderConfig { Provider = "local", Endpoint = "http://127.0.0.1:11434/v1", Model = "qwen" });

        var resolution = fx.NewProvider().Resolve();

        Assert.True(resolution.IsConfigured);
        Assert.Equal("local", resolution.Config!.Provider);
    }

    // ---------------------------------------------------------------- 云端同意门禁

    [Fact]
    public void Resolve_CloudWithCredentialButNoConsent_BlocksCloudRequest()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);

        var provider = fx.NewProvider();
        var resolution = provider.Resolve();

        Assert.Equal(TranslationRuntimeConfigStatus.NoConsent, resolution.Status);
        Assert.True(resolution.NeedsConsent);
        Assert.False(resolution.IsConfigured);
        Assert.Null(provider.Get()); // 未同意 → 无配置 → 调用方无从发起云端请求
    }

    [Fact]
    public void Resolve_CloudWithCredentialAndConsent_ReturnsConfigWithKey()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);
        fx.Consent.GrantConsent(CloudProvider, CloudEndpoint, Key);

        var resolution = fx.NewProvider().Resolve();

        Assert.True(resolution.IsConfigured);
        Assert.True(resolution.IsCloud);
        Assert.Equal(CloudProvider, resolution.Config!.Provider);
        Assert.Equal("deepseek-chat", resolution.Config.Model);
        Assert.Equal(Key, resolution.Config.ApiKey);
    }

    [Fact]
    public void Resolve_CloudHostChange_DropsOldCredential_ReportsNoCredential()
    {
        using var fx = new Fixture();
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);
        fx.Consent.GrantConsent(CloudProvider, CloudEndpoint, Key);

        // 换主机名 → 凭据按新 origin 寻址，旧 key 不适用 → 视为未配密钥（不沿用旧同意 / 旧凭据）。
        fx.SaveProviders(new TranslationProviderConfig
        {
            Provider = CloudProvider,
            Endpoint = "https://api.moonshot.cn/v1",
            Model = "moonshot-v1",
        });

        var resolution = fx.NewProvider().Resolve();

        Assert.Equal(TranslationRuntimeConfigStatus.NoCredential, resolution.Status);
        Assert.False(resolution.IsConfigured);
    }

    [Fact]
    public void Resolve_KeyNeverReachesSettingsFile()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);
        fx.Consent.GrantConsent(CloudProvider, CloudEndpoint, Key);

        var resolution = fx.NewProvider().Resolve();
        Assert.True(resolution.IsConfigured);
        Assert.Equal(Key, resolution.Config!.ApiKey); // 密钥只在内存配置里

        var raw = File.ReadAllText(fx.Settings.FilePath);
        Assert.DoesNotContain(Key, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 同意流程

    [Fact]
    public async Task Ensure_NoConfirmDelegate_LeavesUnconsented()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);

        var resolution = await TranslationRuntimeConsentFlow.EnsureAsync(fx.NewProvider(), confirmAsync: null);

        Assert.Equal(TranslationRuntimeConfigStatus.NoConsent, resolution.Status);
        Assert.False(resolution.IsConfigured);
    }

    [Fact]
    public async Task Ensure_ConfirmDeclined_DoesNotGrantConsent()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);
        var asked = 0;

        var resolution = await TranslationRuntimeConsentFlow.EnsureAsync(
            fx.NewProvider(),
            _ => { asked++; return Task.FromResult(false); });

        Assert.Equal(1, asked);
        Assert.False(resolution.IsConfigured);
        Assert.False(fx.Consent.IsConsented(CloudProvider, CloudEndpoint, Key));
    }

    [Fact]
    public async Task Ensure_ConfirmAccepted_GrantsAndResolves()
    {
        using var fx = new Fixture();
        fx.SaveProviders(Cloud());
        fx.Credentials.Store(CloudProvider, CloudEndpoint, Key);

        var resolution = await TranslationRuntimeConsentFlow.EnsureAsync(
            fx.NewProvider(),
            request =>
            {
                // 模拟宿主：弹 F5 → 用户同意 → 按指纹授予（未勾选「记住」= 仅本次会话）。
                fx.Consent.GrantConsent(request.Provider, request.Endpoint, Key, remember: false);
                return Task.FromResult(true);
            });

        Assert.True(resolution.IsConfigured);
        Assert.Equal(Key, resolution.Config!.ApiKey);
    }
}
