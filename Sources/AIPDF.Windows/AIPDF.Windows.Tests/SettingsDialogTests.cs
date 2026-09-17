// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T2 设置对话框防 clobber 回归（逻辑层，无 WPF 实例化）：
//   SettingsDialog.OnSaveClick 必须走 ApplyDialogValues 在既有 AppSettings 上【就地修改】，
//   保存后翻译连接配置的 v1.2 嵌套字段（presetId / apiProtocol / modelProtocols）、同意记录
//   与其余顶层设置必须原样保留 —— 顶层逐字段重建会把它们重置为默认（历史 clobber 面）。
// T16（Final Verification）追加：
//   F1 预设供应商（DeepSeek / OpenAI 等）与自定义供应商一律可移除（确认弹窗 + 不可逆语义）；
//   F2 凭据 / 同意清理失败必须显式反馈，绝不静默当成功（全部清理成功后才移除配置项）；
//   F3 选定供应商时默认模型 → 默认协议 → 默认端点同源，不产生 /v1/v1 重复版本段。
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tests.Services;
using AIPDF.Windows.Views.Settings;
using Xunit;

namespace AIPDF.Windows.Tests;

public sealed class SettingsDialogTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string SettingsPath { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-v12t2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SettingsPath = Path.Combine(Root, "settings.json");
        }

        public SettingsStore NewStore() => new(SettingsPath);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ApplyDialogValues_ThenSave_PreservesProviderNewFieldsAndUntouchedSettings()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        // 预置既有设置：连接配置含 v1.2 新字段，另有同意记录与翻译默认项（均不由设置对话框管理）。
        store.Save(new AppSettings
        {
            TranslationDefaultSourceLanguage = "en",
            TranslationDefaultTargetLanguage = "ja",
            TranslationTemplateId = "legal",
            TranslationCustomPrompt = "保持术语一致",
            TranslationGlossaryPath = @"C:\dict\terms.csv",
            TranslationCostLimitEnabled = false,
            TranslationCostLimitTokens = 123_456,
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = "opencode go",
                    Endpoint = "https://opencode.ai/zen/go/v1",
                    Model = "minimax-m3",
                    PresetId = "opencode-go",
                    ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
                    ModelProtocols = new Dictionary<string, string>
                    {
                        ["minimax-m3"] = TranslationProviderConfig.ProtocolAnthropic,
                        ["kimi-k3"] = TranslationProviderConfig.ProtocolOpenAi,
                    },
                },
            ],
            TranslationConsents =
            [
                new TranslationConsent
                {
                    Provider = "opencode-go",
                    Endpoint = "https://opencode.ai",
                    KeyFingerprint = "0123456789abcdef",
                    GrantedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                    TtlDays = 180,
                },
            ],
        });

        // 模拟 OnSaveClick：在既有设置上就地应用对话框字段后保存（严禁顶层重建）。
        var existing = store.Load();
        SettingsDialog.ApplyDialogValues(
            existing,
            zoom: "150",
            panelKey: "outline",
            recentFilesLimit: 35,
            theme: "dark",
            printDpi: 300,
            outputDir: @"C:\out",
            renderDpi: 144,
            markerEnabled: false);
        store.Save(existing);

        var saved = store.Load();

        // 对话框管理的 8 项已更新。
        Assert.Equal("150", saved.DefaultZoom);
        Assert.Equal("outline", saved.DefaultPanelKey);
        Assert.Equal(35, saved.RecentFilesLimit);
        Assert.Equal("dark", saved.Theme);
        Assert.Equal(300, saved.PrintDpi);
        Assert.Equal(@"C:\out", saved.DefaultOutputDir);
        Assert.Equal(144, saved.DefaultRenderDpi);
        Assert.False(saved.TranslationMarkerEnabled);

        // 不由对话框管理的字段原样保留（防 clobber 核心断言）。
        Assert.Equal("en", saved.TranslationDefaultSourceLanguage);
        Assert.Equal("ja", saved.TranslationDefaultTargetLanguage);
        Assert.Equal("legal", saved.TranslationTemplateId);
        Assert.Equal("保持术语一致", saved.TranslationCustomPrompt);
        Assert.Equal(@"C:\dict\terms.csv", saved.TranslationGlossaryPath);
        Assert.False(saved.TranslationCostLimitEnabled);
        Assert.Equal(123_456, saved.TranslationCostLimitTokens);

        var provider = Assert.Single(saved.TranslationProviders);
        Assert.Equal("opencode-go", provider.PresetId);   // 新字段：稳定 id
        Assert.Equal("anthropic", provider.ApiProtocol);  // 新字段：协议
        Assert.NotNull(provider.ModelProtocols);          // 新字段：按模型覆盖
        Assert.Equal(2, provider.ModelProtocols!.Count);
        Assert.Equal("anthropic", provider.ModelProtocols["minimax-m3"]);
        Assert.Equal("openai", provider.ModelProtocols["kimi-k3"]);
        Assert.Equal("https://opencode.ai/zen/go/v1", provider.Endpoint); // 端点逐字保留

        var consent = Assert.Single(saved.TranslationConsents);
        Assert.Equal("opencode-go", consent.Provider);
        Assert.Equal("https://opencode.ai", consent.Endpoint);
        Assert.Equal("0123456789abcdef", consent.KeyFingerprint);
    }

    [Fact]
    public void ApplyDialogValues_DoesNotResetUnrelatedSettings_OnDefaultConstruction()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        // 保存过的既有设置 + 本对话框仅覆盖 8 项：其余保持存储值（含翻译默认项）。
        store.Save(new AppSettings
        {
            TranslationTemplateId = "medical",
            TranslationCostLimitTokens = 7_777,
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = "deepseek",
                    Endpoint = "https://api.deepseek.com/v1",
                    Model = "deepseek-chat",
                    PresetId = "deepseek",
                    ApiProtocol = TranslationProviderConfig.ProtocolOpenAi,
                },
            ],
        });

        var settings = store.Load();
        SettingsDialog.ApplyDialogValues(
            settings,
            zoom: settings.DefaultZoom,
            panelKey: settings.DefaultPanelKey,
            recentFilesLimit: settings.RecentFilesLimit,
            theme: settings.Theme,
            printDpi: settings.PrintDpi,
            outputDir: settings.DefaultOutputDir,
            renderDpi: settings.DefaultRenderDpi,
            markerEnabled: settings.TranslationMarkerEnabled);
        store.Save(settings);

        var saved = store.Load();
        Assert.Equal("medical", saved.TranslationTemplateId);
        Assert.Equal(7_777, saved.TranslationCostLimitTokens);
        Assert.Equal("deepseek", Assert.Single(saved.TranslationProviders).PresetId);
    }

    // T12：本地服务端口建议 chips 仅替换端口段，保留主机与路径（设计稿 S2 注④）。
    [Fact]
    public void ApplyPortSuggestionToAddress_ReplacesPort_PreservingHostAndPath()
    {
        Assert.Equal("http://127.0.0.1:8000/v1", SettingsDialog.ApplyPortSuggestionToAddress("http://127.0.0.1:11434/v1", 8000));
        Assert.Equal("http://localhost:1234/llm", SettingsDialog.ApplyPortSuggestionToAddress("http://localhost:11434/llm", 1234));
        Assert.Equal("https://127.0.0.1:8080/v1", SettingsDialog.ApplyPortSuggestionToAddress("https://127.0.0.1:11434/v1/", 8080));
    }

    [Fact]
    public void ApplyPortSuggestionToAddress_FallsBackToLoopbackTemplate_WhenAddressInvalid()
    {
        Assert.Equal("http://127.0.0.1:11434/v1", SettingsDialog.ApplyPortSuggestionToAddress("", 11434));
        Assert.Equal("http://127.0.0.1:8000/v1", SettingsDialog.ApplyPortSuggestionToAddress("localhost:11434", 8000));
    }

    // T12：合法 loopback 才可保存为「本地服务」。
    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://[::1]:11434/v1")]
    public void ValidateLocalEndpoint_AcceptsLoopback(string endpoint)
        => Assert.Null(SettingsDialog.ValidateLocalEndpoint(endpoint));

    // T12：非 loopback / 格式错 / 空地址一律被拒（阻止非 loopback 以「本地」名义绕过云端同意）。
    [Theory]
    [InlineData("")]
    [InlineData("localhost:11434")]              // scheme-less（设计稿 P14 要求显式 http(s)）
    [InlineData("http://192.168.1.20:8000/v1")]  // 非 loopback 明文 http
    [InlineData("https://api.example.com/v1")]   // 非 loopback
    public void ValidateLocalEndpoint_RejectsNonLoopbackOrMalformed(string endpoint)
        => Assert.NotNull(SettingsDialog.ValidateLocalEndpoint(endpoint));

    // T13：自定义供应商经 SettingsStore 归一化后 PresetId 恒为 null（不命中任何内置预设）。
    [Fact]
    public void Custom_ProviderPresetIdIsNull_AfterNormalization()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        // 构造自定义供应商：名称不是任何预设 id / 显示名，端点含自定义路径。
        store.Save(new AppSettings
        {
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = "corp-gateway",
                    Endpoint = "https://gw.corp/llm/v1",
                    Model = "my-model",
                    PresetId = null,
                },
            ],
        });

        var saved = store.Load();
        var provider = Assert.Single(saved.TranslationProviders);

        // 自定义供应商 PresetId 必须为 null（NormalizePresetId 按名称/端点均未命中内置预设）。
        Assert.Null(provider.PresetId);
        Assert.Equal("corp-gateway", provider.Provider);
    }

    // T13：自定义端点带路径保存后路径逐字保留（不塌陷为 origin、不追加 /v1）。
    [Fact]
    public void Custom_EndpointWithPath_PreservedAfterSave()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        const string customEndpoint = "https://gw.corp/llm/v1";

        store.Save(new AppSettings
        {
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = "corp-gateway",
                    Endpoint = customEndpoint,
                    Model = "my-model",
                },
            ],
        });

        var saved = store.Load();
        var provider = Assert.Single(saved.TranslationProviders);

        // 端点逐字保留：路径 /llm/v1 不被塌陷、不追加额外 /v1。
        Assert.Equal(customEndpoint, provider.Endpoint);
        // 连带确认 PresetId 仍为 null（自定义端点不命中任何预设 base URL）。
        Assert.Null(provider.PresetId);
    }

    // ================================================================ T16（Final Verification）：
    // F1 预设供应商可移除（含确认门）/ F2 清理失败不静默 / F3 协议与端点同源。纯逻辑层，无 WPF 实例化。

    /// <summary>移除测试夹具：设置 + 内存凭据后端 + 同意服务（不碰真实凭据管理器）。</summary>
    private sealed class ProviderFixture : IDisposable
    {
        public const string Key = "sk-t16-removal-test-7f2a";

        public string Root { get; }
        public SettingsStore Store { get; }
        public InMemoryCredentialBackend Backend { get; }
        public TranslationCredentialStore Credentials { get; }
        public TranslationConsentService Consent { get; }

        public ProviderFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-v12t16-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new SettingsStore(Path.Combine(Root, "settings.json"));
            Backend = new InMemoryCredentialBackend();
            Credentials = new TranslationCredentialStore(Backend);
            Consent = new TranslationConsentService(Store, Credentials);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Delete 一律抛 <see cref="CredentialStoreUnavailableException"/> 的后端（模拟系统凭据管理器不可用）。</summary>
    private sealed class FailingDeleteBackend : ICredentialBackend
    {
        private readonly InMemoryCredentialBackend _inner = new();

        public string? Read(string target) => _inner.Read(target);

        public void Write(string target, string secret) => _inner.Write(target, secret);

        public bool Delete(string target)
            => throw new CredentialStoreUnavailableException("Windows 凭据管理器不可用（Win32 错误 5，测试注入）。", 5);

        public IReadOnlyList<string> Enumerate(string prefix) => _inner.Enumerate(prefix);
    }

    /// <summary>按预设表构造一条「用户在设置页保存」的连接配置（端点按默认协议选取）。</summary>
    private static TranslationProviderConfig PresetConfig(string presetId)
    {
        var preset = TranslationProviderPreset.Find(presetId)!;
        return new TranslationProviderConfig
        {
            Provider = preset.DisplayName,
            Endpoint = preset.OpenAiBaseUrl ?? preset.AnthropicBaseUrl!,
            Model = preset.DefaultModels.FirstOrDefault() ?? "",
            PresetId = preset.Id,
            ApiProtocol = preset.DefaultProtocol,
            ModelProtocols = preset.DefaultModelProtocols,
        };
    }

    /// <summary>与 <c>SettingsDialog.SaveProviders</c> 同口径：Load → 就地替换列表 → Save。</summary>
    private static void PersistProviders(SettingsStore store, IEnumerable<TranslationProviderConfig> providers)
    {
        var settings = store.Load();
        settings.TranslationProviders = [.. providers];
        store.Save(settings);
    }

    private static List<TranslationProviderConfig> LoadProviders(SettingsStore store)
        => store.Load().TranslationProviders;

    // F1：预设供应商（DeepSeek 等）有配置即可移除，且配置 / 凭据 / 同意三者一并清理。
    [Fact]
    public void Delete_PresetProviderWithConfig_RemovesConfigCredentialAndConsent()
    {
        using var fx = new ProviderFixture();
        var config = PresetConfig("deepseek");
        PersistProviders(fx.Store, [config]);

        var identity = TranslationProviderIdentity.For(config);
        fx.Credentials.Store(identity, config.Endpoint, ProviderFixture.Key);
        fx.Consent.GrantConsent(identity, config.Endpoint, ProviderFixture.Key);
        Assert.True(fx.Consent.IsConsentedUsingStoredKey(identity, config.Endpoint));

        var providers = LoadProviders(fx.Store);
        var target = Assert.Single(providers);

        var outcome = SettingsDialog.RemoveProviderWithConfirmation(
            confirmed: true,
            providers,
            target,
            fx.Credentials,
            fx.Consent,
            () => PersistProviders(fx.Store, providers));

        Assert.NotNull(outcome);
        Assert.True(outcome!.ProviderRemoved);
        Assert.True(outcome.CredentialCleared);
        Assert.True(outcome.ConsentRevoked);
        Assert.False(outcome.CleanupFailed);
        Assert.DoesNotContain("未完成", outcome.Message);

        Assert.Empty(LoadProviders(fx.Store));                       // 预设配置已移除（F1）
        Assert.Null(fx.Credentials.Get(identity, config.Endpoint));  // 凭据已清理（F2：不留残留）
        Assert.Empty(fx.Store.Load().TranslationConsents);           // 同意记录已撤销
        Assert.False(fx.Consent.IsConsented(identity, config.Endpoint, ProviderFixture.Key));
    }

    // F1：删除必须先确认；未确认时配置 / 凭据 / 同意一律原样保留。
    [Fact]
    public void Delete_RequiresConfirmation_NoDeleteWithoutConfirm()
    {
        using var fx = new ProviderFixture();
        var config = PresetConfig("openai");
        PersistProviders(fx.Store, [config]);

        var identity = TranslationProviderIdentity.For(config);
        fx.Credentials.Store(identity, config.Endpoint, ProviderFixture.Key);
        fx.Consent.GrantConsent(identity, config.Endpoint, ProviderFixture.Key);

        // 确认门本身：仅显式「是」视为确认。
        Assert.True(SettingsDialog.IsDeleteConfirmed(MessageBoxResult.Yes));
        Assert.False(SettingsDialog.IsDeleteConfirmed(MessageBoxResult.No));

        var providers = LoadProviders(fx.Store);
        var target = Assert.Single(providers);

        var outcome = SettingsDialog.RemoveProviderWithConfirmation(
            confirmed: false,
            providers,
            target,
            fx.Credentials,
            fx.Consent,
            () => PersistProviders(fx.Store, providers));

        Assert.Null(outcome); // 未确认 → 不做任何修改
        Assert.Equal("openai", Assert.Single(LoadProviders(fx.Store)).PresetId);
        Assert.Equal(ProviderFixture.Key, fx.Credentials.Get(identity, config.Endpoint));
        Assert.True(fx.Consent.IsConsented(identity, config.Endpoint, ProviderFixture.Key));
    }

    // F2：凭据清理失败必须显式反馈并保留配置（避免凭据残留失去清理入口），绝不谎报成功。
    [Fact]
    public void Delete_CleanupFailure_SurfacesError_NotSilentSuccess()
    {
        using var fx = new ProviderFixture();
        var credentials = new TranslationCredentialStore(new FailingDeleteBackend());

        var config = PresetConfig("deepseek");
        PersistProviders(fx.Store, [config]);
        var identity = TranslationProviderIdentity.For(config);
        credentials.Store(identity, config.Endpoint, ProviderFixture.Key);
        fx.Consent.GrantConsent(identity, config.Endpoint, ProviderFixture.Key);

        var providers = LoadProviders(fx.Store);
        var target = Assert.Single(providers);

        var outcome = SettingsDialog.RemoveProviderWithConfirmation(
            confirmed: true,
            providers,
            target,
            credentials,
            fx.Consent,
            () => PersistProviders(fx.Store, providers));

        Assert.NotNull(outcome);
        Assert.True(outcome!.CleanupFailed);        // 失败显式反馈
        Assert.False(outcome.ProviderRemoved);      // 清理未全部成功 → 配置不移除
        Assert.False(outcome.CredentialCleared);
        Assert.Contains("凭据清除失败", outcome.Message);
        Assert.DoesNotContain("已移除供应商", outcome.Message); // 绝不静默当成功

        Assert.Equal("deepseek", Assert.Single(LoadProviders(fx.Store)).PresetId);
        Assert.Equal(ProviderFixture.Key, credentials.Get(identity, config.Endpoint)); // 残留如实可见
    }

    // F1/F2：移除只影响目标供应商；其他供应商的配置、凭据与同意不得被波及。
    [Fact]
    public void Delete_Isolated_DoesNotAffectOtherProviders()
    {
        using var fx = new ProviderFixture();
        const string kimiKey = "sk-t16-kimi-isolated-9c41";

        var deepseek = PresetConfig("deepseek");
        var kimi = PresetConfig("kimi");
        PersistProviders(fx.Store, [deepseek, kimi]);

        var deepseekIdentity = TranslationProviderIdentity.For(deepseek);
        var kimiIdentity = TranslationProviderIdentity.For(kimi);
        fx.Credentials.Store(deepseekIdentity, deepseek.Endpoint, ProviderFixture.Key);
        fx.Credentials.Store(kimiIdentity, kimi.Endpoint, kimiKey);
        fx.Consent.GrantConsent(deepseekIdentity, deepseek.Endpoint, ProviderFixture.Key);
        fx.Consent.GrantConsent(kimiIdentity, kimi.Endpoint, kimiKey);

        var providers = LoadProviders(fx.Store);
        var target = providers.Single(p => p.PresetId == "deepseek");

        var outcome = SettingsDialog.RemoveProviderWithConfirmation(
            confirmed: true,
            providers,
            target,
            fx.Credentials,
            fx.Consent,
            () => PersistProviders(fx.Store, providers));

        Assert.NotNull(outcome);
        Assert.True(outcome!.ProviderRemoved);

        var remaining = Assert.Single(LoadProviders(fx.Store));
        Assert.Equal("kimi", remaining.PresetId);
        Assert.Null(fx.Credentials.Get(deepseekIdentity, deepseek.Endpoint));
        Assert.Equal(kimiKey, fx.Credentials.Get(kimiIdentity, kimi.Endpoint)); // kimi 凭据未被误删
        Assert.True(fx.Consent.IsConsented(kimiIdentity, kimi.Endpoint, kimiKey));
        Assert.Equal("kimi", Assert.Single(fx.Store.Load().TranslationConsents).Provider);
    }

    // F3：选定预设时默认模型 → 默认协议 → 默认端点同源（如 opencode-go + minimax-m3 → Anthropic base）。
    [Theory]
    [InlineData("opencode-go", "minimax-m3", "anthropic", "https://opencode.ai/zen/go")]
    [InlineData("opencode-go", "kimi-k3", "openai", "https://opencode.ai/zen/go/v1")]
    [InlineData("kimi-code", "k3", "anthropic", "https://api.kimi.com/coding")]
    [InlineData("glm-coding-plan", "glm-5.3", "anthropic", "https://open.bigmodel.cn/api/anthropic")]
    [InlineData("deepseek", "deepseek-chat", "openai", "https://api.deepseek.com/v1")]
    public void ProviderSwitch_DefaultProtocolAndEndpoint_StayConsistent(
        string presetId,
        string model,
        string expectedProtocol,
        string expectedEndpoint)
    {
        var preset = TranslationProviderPreset.Find(presetId)!;

        var protocol = SettingsDialog.DefaultProtocolFor(preset, model);
        var endpoint = SettingsDialog.DefaultEndpointForProtocol(preset, protocol);

        Assert.Equal(expectedProtocol, protocol);
        Assert.Equal(expectedEndpoint, endpoint);

        // 请求基址归一化后绝不出现重复版本段（F3 病态：.../v1/v1/messages）。
        var requestBase = ProviderUrl.NormalizeRequestBase(endpoint, protocol);
        Assert.DoesNotContain("/v1/v1", requestBase, StringComparison.OrdinalIgnoreCase);

        if (expectedProtocol == TranslationProviderConfig.ProtocolAnthropic && preset.AnthropicBaseUrl is not null)
        {
            // anthropic 协议必须落到不含 /v1 的 base（引擎拼 /v1/messages），绝不能是 OpenAI base。
            Assert.Equal(preset.AnthropicBaseUrl, endpoint);
            Assert.Equal(preset.AnthropicBaseUrl, requestBase);
            Assert.NotEqual(preset.OpenAiBaseUrl, endpoint);
        }
        else
        {
            Assert.Equal(preset.OpenAiBaseUrl, endpoint);
        }
    }

    // F3 回归锁定：opencode-go 默认模型（minimax-m3）走 anthropic，最终请求路径不得是 /v1/v1/messages。
    [Fact]
    public void ProviderSwitch_OpenCodeGo_DefaultPair_DoesNotDoubleVersionSegment()
    {
        var preset = TranslationProviderPreset.Find("opencode-go")!;
        var defaultModel = preset.DefaultModels[0];

        var protocol = SettingsDialog.DefaultProtocolFor(preset, defaultModel);
        var endpoint = SettingsDialog.DefaultEndpointForProtocol(preset, protocol);

        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, protocol);
        Assert.Equal("https://opencode.ai/zen/go", endpoint);

        // 引擎 anthropic 路线：POST {base}/v1/messages（backend/translate/llm.py）。
        var messagesUrl = ProviderUrl.NormalizeRequestBase(endpoint, protocol) + "/v1/messages";
        Assert.Equal("https://opencode.ai/zen/go/v1/messages", messagesUrl);
        Assert.DoesNotContain("/v1/v1", messagesUrl, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ S1 设计稿对齐（结构回归）：
    // 对话框 880 宽、左列 236、左右 MaxHeight 423、字段序 API Key→模型→端点→协议→连接→同意状态，
    // 列表行只保留「名称 + tag + 状态点」（不再逐行回显 provider id），名称 / provider id 仅自定义项可见。

    /// <summary>S1 详情面板已知字段标签（用于按序断言，不与提示文案混淆）。</summary>
    private static readonly string[] ProviderFieldLabels =
        ["名称", "provider id（自动生成）", "API Key", "模型", "API 地址", "协议", "连接", "同意状态"];

    private static App? _uiApp;

    /// <summary>WPF 控件具有 UI 线程亲和性：构造与遍历统一跑在同一 STA 线程上。</summary>
    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null) return;
        _uiApp = new App();
        _uiApp.InitializeComponent(); // 载入 App.xaml 应用级 token（Brush.* / FieldLabel 等）。
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static (Grid Master, StackPanel Left, ScrollViewer Detail, ListBox List) FindProviderMasterDetail(
        SettingsDialog dialog)
    {
        var master = Descendants(dialog)
            .OfType<Grid>()
            .Single(grid => grid.ColumnDefinitions.Count == 2 && grid.ColumnDefinitions[0].Width.Value == 236);
        var left = master.Children.OfType<StackPanel>().Single(c => Grid.GetColumn(c) == 0);
        var detail = master.Children.OfType<ScrollViewer>().Single(c => Grid.GetColumn(c) == 1);
        var list = left.Children.OfType<ListBox>().Single();
        return (master, left, detail, list);
    }

    /// <summary>详情面板内字段标签文本 + 其字段容器（DetailField）是否可见（按逻辑树先序遍历）。</summary>
    private static List<(string Text, bool Visible)> DetailFieldLabels(ScrollViewer detail)
    {
        var result = new List<(string, bool)>();
        foreach (var text in Descendants(detail).OfType<TextBlock>())
        {
            if (!ProviderFieldLabels.Contains(text.Text)) continue;
            var container = LogicalTreeHelper.GetParent(text) as FrameworkElement;
            result.Add((text.Text, container is null || container.Visibility == Visibility.Visible));
        }

        return result;
    }

    private static ListBoxItem FindProviderRow(ListBox list, string displayName)
        => list.Items.OfType<ListBoxItem>()
            .Where(item => item.Tag is not null)
            .First(item => Descendants(item).OfType<TextBlock>().Any(t => t.Text == displayName));

    private static void SelectLastProviderEntry(ListBox list)
    {
        for (var i = list.Items.Count - 1; i >= 0; i--)
        {
            if (list.Items[i] is ListBoxItem { Tag: not null } item)
            {
                list.SelectedItem = item;
                return;
            }
        }

        Assert.Fail("供应商列表中没有可选择的条目");
    }

    /// <summary>按 Tag 选中导航 ListBox 中的分类项（T3：从 Descendants 查找名为 SettingsNavList 的 ListBox）。</summary>
    private static void SelectNavCategory(SettingsDialog dialog, string tag)
    {
        var navList = Descendants(dialog)
            .OfType<ListBox>()
            .Single(lb => lb.Name == "SettingsNavList");
        foreach (var item in navList.Items)
        {
            if (item is ListBoxItem { Tag: string t } && string.Equals(t, tag, StringComparison.Ordinal))
            {
                navList.SelectedItem = item;
                return;
            }
        }
        Assert.Fail($"导航项 Tag '{tag}' 未找到");
    }

    private static SettingsDialog NewDialog(SettingsStore store)
    {
        var credentials = new TranslationCredentialStore(new InMemoryCredentialBackend());
        var consent = new TranslationConsentService(store, credentials);
        return new SettingsDialog(store, translationConsent: consent, translationCredentials: credentials);
    }

    [Fact]
    public void ProviderPane_DesignS1_MeasurementsAndBuiltInFieldOrder()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            Assert.Equal(880d, dialog.Width); // S1：矩形 880 × 560

            // T3：导航到模型供应商页再断言（翻译段默认折叠）
            SelectNavCategory(dialog, "translation.providers");
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            Assert.Equal(423d, list.MaxHeight);                 // S1：左列表 236 × 423
            Assert.Equal(423d, detail.MaxHeight);               // S1：详情 472 × 423

            // 选中内置云端预设 OpenAI（未配置）：名称 / provider id 字段隐藏，id 改由副标题呈现。
            var openAiRow = FindProviderRow(list, "OpenAI");
            list.SelectedItem = openAiRow;

            var labels = DetailFieldLabels(detail);
            Assert.Contains(labels, l => l is { Text: "名称", Visible: false });
            Assert.Contains(labels, l => l is { Text: "provider id（自动生成）", Visible: false });

            // S1 字段序：API Key → 模型 → API 地址 → 协议 → 连接 → 同意状态。
            var visible = labels.Where(l => l.Visible).Select(l => l.Text).ToList();
            Assert.Equal(
                new[] { "API Key", "模型", "API 地址", "协议", "连接", "同意状态" },
                visible);

            // 列表行「名称 + tag + 状态点」：未配置内置项只有名称一行，不再有 provider id 第二行。
            var rowTexts = Descendants(openAiRow).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(new[] { "OpenAI" }, rowTexts);
        });
    }

    [Fact]
    public void ProviderPane_DesignS3_CustomProviderShowsNameAndIdFields()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var store = fx.NewStore();
            store.Save(new AppSettings
            {
                TranslationProviders =
                [
                    new TranslationProviderConfig
                    {
                        Provider = "我的网关",
                        Endpoint = "https://gw.internal/v1",
                        Model = "my-model",
                    },
                ],
            });

            var dialog = NewDialog(store);
            // T3：导航到模型供应商页再断言（翻译段默认折叠）
            SelectNavCategory(dialog, "translation.providers");
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            SelectLastProviderEntry(list); // 自定义项排在列表末尾

            var visible = DetailFieldLabels(detail).Where(l => l.Visible).Select(l => l.Text).ToList();
            Assert.Equal(
                new[] { "名称", "provider id（自动生成）", "API Key", "模型", "API 地址", "协议", "连接", "同意状态" },
                visible);
        });
    }

    // ================================================================ T3/T4/T5 结构测试

    /// <summary>通过 pane Name 查找面板。</summary>
    private static FrameworkElement FindPane(SettingsDialog dialog, string paneName)
        => Descendants(dialog).OfType<FrameworkElement>()
            .Single(fe => fe.Name == paneName);

    /// <summary>六个分类名 → 对应 pane Name。</summary>
    private static readonly Dictionary<string, string> CategoryToPaneName = new()
    {
        ["general"] = "Pane_general",
        ["appearance"] = "Pane_appearance",
        ["translation.providers"] = "Pane_translation_providers",
        ["translation.glossary"] = "Pane_translation_glossary",
        ["translation.output"] = "Pane_translation_output",
        ["advanced"] = "Pane_advanced",
    };

    /// <summary>所有六个 pane Name。</summary>
    private static readonly string[] AllPaneNames =
        ["Pane_general", "Pane_appearance", "Pane_translation_providers",
         "Pane_translation_glossary", "Pane_translation_output", "Pane_advanced"];

    [Fact]
    public void Navigation_CategoryItemsAndSwitching()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            // 1. 导航项集合 == 设计集合
            var navList = Descendants(dialog)
                .OfType<ListBox>()
                .Single(lb => lb.Name == "SettingsNavList");
            var itemTexts = navList.Items.OfType<ListBoxItem>().Select(i => (string)i.Content).ToList();
            Assert.Equal(new[] { "常规", "外观", "翻译", "模型供应商", "术语表", "输出", "高级" }, itemTexts);

            // 2. 默认选中「常规」（T5）
            var defaultItem = (ListBoxItem)navList.SelectedItem!;
            Assert.Equal("general", defaultItem.Tag);

            // 3. 逐个分类切换 → 断言 pane Name + Visibility，以及 pane head 文本
            var headTitle = Descendants(dialog).OfType<TextBlock>()
                .Single(tb => tb.Name == "PaneHeadTitle");

            // 常规
            Assert.Equal("常规", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_general").Visibility);

            // 外观
            SelectNavCategory(dialog, "appearance");
            Assert.Equal("外观", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_appearance").Visibility);
            Assert.Equal(Visibility.Collapsed, FindPane(dialog, "Pane_general").Visibility);

            // 翻译 → 模型供应商（T3：点击翻译父项跳转模型供应商）
            SelectNavCategory(dialog, "translation");
            Assert.Equal("模型供应商", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_translation_providers").Visibility);
            Assert.Equal(Visibility.Collapsed, FindPane(dialog, "Pane_appearance").Visibility);

            // 术语表
            SelectNavCategory(dialog, "translation.glossary");
            Assert.Equal("术语表", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_translation_glossary").Visibility);
            Assert.Equal(Visibility.Collapsed, FindPane(dialog, "Pane_translation_providers").Visibility);

            // 输出
            SelectNavCategory(dialog, "translation.output");
            Assert.Equal("输出", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_translation_output").Visibility);
            Assert.Equal(Visibility.Collapsed, FindPane(dialog, "Pane_translation_glossary").Visibility);

            // 高级
            SelectNavCategory(dialog, "advanced");
            Assert.Equal("高级", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_advanced").Visibility);
            Assert.Equal(Visibility.Collapsed, FindPane(dialog, "Pane_translation_output").Visibility);

            // 4. 回到常规 → 确认可达，且高级面板已 Collapsed
            SelectNavCategory(dialog, "general");
            Assert.Equal("常规", headTitle.Text);
            Assert.Equal(Visibility.Visible, FindPane(dialog, "Pane_general").Visibility);
            Assert.Equal(Visibility.Collapsed, FindPane(dialog, "Pane_advanced").Visibility);
        });
    }

    [Fact]
    public void Navigation_EachCategoryHasVisibleContent()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            // 每个分类切换后：对应 pane Visible 且含可见 TextBlock；其余五个 pane 均 Collapsed
            var categories = new[]
            {
                "general", "appearance", "translation.providers",
                "translation.glossary", "translation.output", "advanced"
            };
            foreach (var cat in categories)
            {
                SelectNavCategory(dialog, cat);
                var activeName = CategoryToPaneName[cat];
                var activePane = FindPane(dialog, activeName);
                Assert.Equal(Visibility.Visible, activePane.Visibility);

                // 活跃 pane 至少包含一个可见且非空的 TextBlock
                var visibleTextBlocks = Descendants(activePane).OfType<TextBlock>()
                    .Where(tb => tb.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(tb.Text))
                    .ToList();
                Assert.True(visibleTextBlocks.Count > 0,
                    $"分类 '{cat}' 的面板 '{activeName}' 中没有可见的 TextBlock");

                // 其余五个 pane 必须 Collapsed
                var otherPanes = AllPaneNames.Where(n => n != activeName);
                foreach (var otherName in otherPanes)
                {
                    var otherPane = FindPane(dialog, otherName);
                    Assert.Equal(Visibility.Collapsed, otherPane.Visibility);
                }
            }
        });
    }

    [Fact]
    public void Dialog_DesignS1_FixedDimensions()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            Assert.Equal(880d, dialog.Width);
            Assert.Equal(560d, dialog.Height);
            Assert.Equal(SizeToContent.Manual, dialog.SizeToContent);
        });
    }

    [Fact]
    public void Dialog_NavigationColumn_Width170()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            // 中间带 Grid：两列，ColumnDefinitions[0] 宽度 170（导航列）。
            // 必须与翻译 master-detail Grid（ColumnDefinitions[0] = 236）区分。
            var middleBand = Descendants(dialog)
                .OfType<Grid>()
                .FirstOrDefault(g => g.ColumnDefinitions.Count == 2
                                  && g.ColumnDefinitions[0].Width.Value == 170
                                  && g.ColumnDefinitions[1].Width.IsStar);
            Assert.NotNull(middleBand);
        });
    }

    [Fact]
    public void ContentHost_PaneHeadAndBodyOccupyDifferentRows()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            // contentHost 是 ScrollViewer（Auto 垂直 / Disabled 水平）的 Content Grid
            var scrollViewer = Descendants(dialog)
                .OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                                   && sv.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled);
            Assert.NotNull(scrollViewer);
            Assert.IsType<Grid>(scrollViewer.Content);
            var host = (Grid)scrollViewer.Content;

            // 至少 2 行：Auto（pane head）+ Star（pane body）
            Assert.True(host.RowDefinitions.Count >= 2,
                $"contentHost 应有 >= 2 行，实际 {host.RowDefinitions.Count} 行");

            // PaneHeadTitle 所在面板在 row 0
            var headTitle = Descendants(dialog).OfType<TextBlock>()
                .Single(tb => tb.Name == "PaneHeadTitle");
            var headPanel = LogicalTreeHelper.GetParent(headTitle) as FrameworkElement;
            Assert.NotNull(headPanel);
            Assert.Equal(0, Grid.GetRow(headPanel));

            // Pane_general 在 row 1
            var generalPane = FindPane(dialog, "Pane_general");
            Assert.Equal(1, Grid.GetRow(generalPane));
        });
    }

    [Fact]
    public void AllSettingsItems_AreReachableInCategory()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            // 常规面板：默认缩放 / 默认左栏面板 / 最近文件上限 / 默认输出目录
            SelectNavCategory(dialog, "general");
            var general = FindPane(dialog, "Pane_general");
            Assert.Equal(Visibility.Visible, general.Visibility);
            Assert.Contains("默认缩放", Descendants(general).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));
            Assert.Contains("默认左栏面板", Descendants(general).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));
            Assert.Contains("最近文件上限", Descendants(general).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));
            Assert.Contains("默认输出目录", Descendants(general).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));

            // 外观面板：主题
            SelectNavCategory(dialog, "appearance");
            var appearance = FindPane(dialog, "Pane_appearance");
            Assert.Equal(Visibility.Visible, appearance.Visibility);
            Assert.Contains("主题", Descendants(appearance).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));

            // 翻译·模型供应商面板：master-detail 列表存在
            SelectNavCategory(dialog, "translation.providers");
            var providers = FindPane(dialog, "Pane_translation_providers");
            Assert.Equal(Visibility.Visible, providers.Visibility);
            var (_, _, _, list) = FindProviderMasterDetail(dialog);
            Assert.True(list.Items.Count > 0, "模型供应商列表应包含条目");

            // 翻译·输出面板：标记 CheckBox 存在
            SelectNavCategory(dialog, "translation.output");
            var output = FindPane(dialog, "Pane_translation_output");
            Assert.Equal(Visibility.Visible, output.Visibility);
            var markerCheck = Descendants(output).OfType<CheckBox>().FirstOrDefault();
            Assert.NotNull(markerCheck);

            // 高级面板：打印 DPI / 默认渲染 DPI
            SelectNavCategory(dialog, "advanced");
            var advanced = FindPane(dialog, "Pane_advanced");
            Assert.Equal(Visibility.Visible, advanced.Visibility);
            Assert.Contains("打印 DPI", Descendants(advanced).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));
            Assert.Contains("默认渲染 DPI", Descendants(advanced).OfType<TextBlock>()
                .Where(tb => tb.Visibility == Visibility.Visible).Select(tb => tb.Text));
        });
    }
}
