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
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            SelectLastProviderEntry(list); // 自定义项排在列表末尾

            var visible = DetailFieldLabels(detail).Where(l => l.Visible).Select(l => l.Text).ToList();
            Assert.Equal(
                new[] { "名称", "provider id（自动生成）", "API Key", "模型", "API 地址", "协议", "连接", "同意状态" },
                visible);
        });
    }
}
