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
using System.Windows.Media;
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
    // 对话框 880 宽、左列 236、左右栏通过 Star 行填满剩余高度（不再用固定 MaxHeight=423），
    // 字段序 API Key→模型→端点→协议→连接→同意状态，
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

    private static (Grid Master, Grid Left, ScrollViewer Detail, ListBox List) FindProviderMasterDetail(
        SettingsDialog dialog)
    {
        var master = Descendants(dialog)
            .OfType<Grid>()
            .Single(grid => grid.ColumnDefinitions.Count == 2 && grid.ColumnDefinitions[0].Width.Value == 236);
        var left = master.Children.OfType<Grid>().Single(c => Grid.GetColumn(c) == 0);
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

    /// <summary>详情面板里的「模型」输入框（模型标签所在面板内的第一个 TextBox）。</summary>
    private static TextBox FindModelBox(ScrollViewer detail)
    {
        var label = Descendants(detail).OfType<TextBlock>().First(tb => tb.Text == "模型");
        var panel = (Panel)LogicalTreeHelper.GetParent(label)!;
        return Descendants(panel).OfType<TextBox>().First();
    }

    /// <summary>供应商详情内的「保存」按钮（区别于对话框底部按钮带的「保存」）。</summary>
    private static Button FindProviderSaveButton(SettingsDialog dialog)
        => Descendants(FindPane(dialog, "Pane_translation_providers"))
            .OfType<Button>().Single(b => (b.Content as string) == "保存");

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
            // 执行布局：Measure → Arrange → UpdateLayout，使 Star 行分配真实高度给 ListBox。
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(880, 516));
            content.Arrange(new Rect(0, 0, 880, 516));
            content.UpdateLayout();
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            // 不再断言固定 MaxHeight=423：左右栏通过 Star 行填满剩余高度，不再裁切。
            // 左栏 ListBox 的 ActualHeight 应为正数（Star 行分配了真实高度）。
            Assert.True(list.ActualHeight > 0, $"ListBox ActualHeight 应为正数，实际 {list.ActualHeight}");

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

            // contentHost 是名为 SettingsContentHost 的 Grid（不再有外层 ScrollViewer 包裹）。
            var host = Descendants(dialog)
                .OfType<Grid>()
                .FirstOrDefault(g => g.Name == "SettingsContentHost");
            Assert.NotNull(host);

            // 至少 2 行：Auto（pane head）+ Star（pane body）
            Assert.True(host.RowDefinitions.Count >= 2,
                $"contentHost 应有 >= 2 行，实际 {host.RowDefinitions.Count} 行");

            // PaneHeadTitle 所在面板在 row 0
            var headTitle = Descendants(dialog).OfType<TextBlock>()
                .Single(tb => tb.Name == "PaneHeadTitle");
            var headPanel = LogicalTreeHelper.GetParent(headTitle) as FrameworkElement;
            Assert.NotNull(headPanel);
            Assert.Equal(0, Grid.GetRow(headPanel));

            // Pane_general 在 row 1（被 ScrollViewer 包裹，其 Content 仍是 Pane_general）
            var generalPane = FindPane(dialog, "Pane_general");
            var generalScroller = LogicalTreeHelper.GetParent(generalPane) as ScrollViewer;
            Assert.NotNull(generalScroller);
            Assert.Equal(1, Grid.GetRow(generalScroller));
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

    /// <summary>
    /// 回归测试（供应商列表垂直裁切缺陷）：在 880×560 窗口中，供应商面板必须整体落进 pane body 可视区，
    /// 列表通过自身滚动条把底部条目（opencode go / GLM Coding Plan / Kimi Code）带进可视区。
    /// 缺陷成因：固定 MaxHeight=423 的列表位于外层 ScrollViewer 内，列表下半部永远落在可视区之外。
    /// </summary>
    [Fact]
    public void ProviderList_FitsPaneBody_AndLastRowsReachableByScrolling()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());

            // 导航到模型供应商页
            SelectNavCategory(dialog, "translation.providers");

            // 执行布局：Measure → Arrange → UpdateLayout，使 Star 行分配真实高度给 ListBox。
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(880, 516));
            content.Arrange(new Rect(0, 0, 880, 516));
            content.UpdateLayout();

            var pane = FindPane(dialog, "Pane_translation_providers");
            var (_, left, _, list) = FindProviderMasterDetail(dialog);

            // (a) 左栏整体（搜索框 + 列表 + 添加按钮）必须落在 pane body 可视区内，不得溢出被裁切。
            var paneTop = pane.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            var paneBottom = paneTop + pane.ActualHeight;
            var addButton = left.Children.OfType<Button>().Single();
            Assert.True(paneBottom + 1 >= addButton.TransformToAncestor(content).Transform(new Point(0, addButton.ActualHeight)).Y,
                "「＋ 添加供应商」按钮被 pane body 下边缘裁切");

            // (b) 列表拿到真实高度（Star 行生效），且列表自身无需外部滚动即可整体可见。
            Assert.True(list.ActualHeight > 0, $"ListBox ActualHeight 应为正数，实际 {list.ActualHeight}");
            var listBottom = list.TransformToAncestor(content).Transform(new Point(0, list.ActualHeight)).Y;
            Assert.True(listBottom <= paneBottom + 0.5, $"列表底部 {listBottom:F1} 超出 pane body 底部 {paneBottom:F1}");

            // (c) 列表内容高于自身视口 → 必须有内部滚动条（而不是静默裁切）。
            var scroller = Visuals(list).OfType<ScrollViewer>().FirstOrDefault();
            Assert.NotNull(scroller);
            Assert.True(scroller!.ScrollableHeight > 0, "列表内容高于视口时必须可滚动");

            // (d) 滚到底后最末条目必须落进 pane body 可视区（即底部条目可达，不再被裁切）。
            scroller.ScrollToEnd();
            content.UpdateLayout();

            var lastRow = list.Items.OfType<ListBoxItem>().Last(item => item.Tag is not null);
            var lastBottom = lastRow.TransformToAncestor(content).Transform(new Point(0, lastRow.ActualHeight)).Y;
            Assert.True(lastBottom <= paneBottom + 0.5,
                $"滚到底后末条目底部 {lastBottom:F1} 仍超出 pane body 底部 {paneBottom:F1}");

            // (e) opencode go 与 Kimi Code 都必须在列表数据中（用户报告缺失的条目）。
            Assert.NotNull(FindProviderRow(list, "opencode go"));
            Assert.NotNull(FindProviderRow(list, "Kimi Code"));
        });
    }

    /// <summary>
    /// 回归测试（不可见宿主遮挡输入）：任一分类下，contentHost row 1 里可见的子元素必须恰好只有当前分类一个。
    /// 若只隐藏内层面板、把空 ScrollViewer 留在可视树，它会继续参与命中测试并盖在下方面板之上，
    /// 使供应商列表无法点选、滚动条无法拖动（2026-09-18 回归）。
    /// </summary>
    [Fact]
    public void OnlyActiveCategoryHost_IsVisible_SoHitTestingIsNotBlocked()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());
            var host = Descendants(dialog).OfType<Grid>().Single(g => g.Name == "SettingsContentHost");

            string[] categories =
                ["general", "appearance", "translation.providers", "translation.glossary", "translation.output", "advanced"];
            foreach (var tag in categories)
            {
                SelectNavCategory(dialog, tag);
                var visible = host.Children.OfType<FrameworkElement>()
                    .Where(child => Grid.GetRow(child) == 1 && child.Visibility == Visibility.Visible)
                    .ToList();
                Assert.True(visible.Count == 1,
                    $"分类 {tag} 下 row 1 可见宿主应恰好 1 个，实际 {visible.Count} 个（多余宿主会吞掉鼠标输入）");
            }
        });
    }

    /// <summary>
    /// 回归测试（混合协议供应商：协议必须随模型派生）。场景：
    /// opencode go 先以默认模型 minimax-m3（anthropic）保存，随后把模型改成不在预设模型表内的
    /// <c>deepseek-v4.1-flash</c>（典型来自「拉取模型」下拉或手输）再保存 —— 必须写出
    /// openai 协议 + OpenAI base。修复前手输 / 拉取路径不重算协议，会写出 anthropic 协议 +
    /// Anthropic base，引擎据此把 Anthropic 请求发给 DeepSeek 模型，被网关以 HTTP 403 拒绝。
    /// </summary>
    [Fact]
    public void MixedProtocolPreset_SaveAfterModelChange_DerivesProtocolAndEndpointFromModel()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var store = fx.NewStore();

            // 预置：用户先按默认模型（minimax-m3 → anthropic）保存过 opencode go。
            store.Save(new AppSettings
            {
                TranslationProviders =
                [
                    new TranslationProviderConfig
                    {
                        Provider = "opencode go",
                        Endpoint = "https://opencode.ai/zen/go",
                        Model = "minimax-m3",
                        PresetId = "opencode-go",
                        ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
                        ModelProtocols = TranslationProviderPreset.Find("opencode-go")!.DefaultModelProtocols,
                    },
                ],
            });

            var dialog = NewDialog(store);
            SelectNavCategory(dialog, "translation.providers");
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            list.SelectedItem = FindProviderRow(list, "opencode go");

            // 改模型（等价于「拉取模型」下拉选中 / 手输）：协议与端点必须随之派生。
            FindModelBox(detail).Text = "deepseek-v4.1-flash";

            FindProviderSaveButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var saved = Assert.Single(store.Load().TranslationProviders);
            Assert.Equal("opencode-go", saved.PresetId);
            Assert.Equal("deepseek-v4.1-flash", saved.Model);
            Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, saved.ApiProtocol);
            // openai base 含版本段，引擎据此拼 {base}/chat/completions。
            Assert.Equal("https://opencode.ai/zen/go/v1", saved.Endpoint);
        });
    }

    // ================================================================ Task 5 P1：
    // (A) 「拉取模型」下拉选中路径 OnFetchedModelSelected；(B) 协议反向切换 + 端点回滚。

    /// <summary>详情面板「API 地址」输入框（与 <see cref="FindModelBox"/> 同口径：标签 → 容器 → 首个 TextBox）。</summary>
    private static TextBox FindEndpointBox(ScrollViewer detail)
    {
        var label = Descendants(detail).OfType<TextBlock>().First(tb => tb.Text == "API 地址");
        var panel = (Panel)LogicalTreeHelper.GetParent(label)!;
        return Descendants(panel).OfType<TextBox>().First();
    }

    /// <summary>
    /// 私有字段 <c>_fetchedModelCombo</c>（「拉取模型」下拉）。逻辑树里它与预设下拉同为 ComboBox，
    /// 无法按类型区分；沿用测试工程既有反射先例（FullTranslationViewModelTests 读私有字段）直取，
    /// 不改生产可访问性。
    /// </summary>
    private static ComboBox FindFetchedModelCombo(SettingsDialog dialog)
        => (ComboBox)typeof(SettingsDialog)
            .GetField("_fetchedModelCombo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(dialog)!;

    /// <summary>当前生效协议 <c>_protocol</c>（经 <c>SelectProtocol</c> 归一化；混合协议预设分段按钮双双失活，须直读字段）。</summary>
    private static string GetSelectedProtocol(SettingsDialog dialog)
        => (string)typeof(SettingsDialog)
            .GetField("_protocol",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(dialog)!;

    /// <summary>
    /// 回归测试（「拉取模型」下拉选中路径）：SelectionChanged 接线（SettingsDialog.cs:861）→
    /// <c>OnFetchedModelSelected</c>（:1557）必须把选中项写回 <c>_modelBox</c>，
    /// 再经 TextChanged → <c>OnModelChanged</c> → <c>RefreshProtocolForModel</c> 按所选模型刷新协议与端点。
    /// 历史缺陷：下拉选中不重算协议，anthropic 协议会配 openai 系模型发往网关（HTTP 403）。
    /// </summary>
    [Fact]
    public void OnFetchedModelSelected_ModelDropdown_WritesBackModelAndRefreshesProtocolAndEndpoint()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var store = fx.NewStore();

            // 预置：用户先按默认模型（minimax-m3 → anthropic）保存过 opencode go。
            store.Save(new AppSettings
            {
                TranslationProviders =
                [
                    new TranslationProviderConfig
                    {
                        Provider = "opencode go",
                        Endpoint = "https://opencode.ai/zen/go",
                        Model = "minimax-m3",
                        PresetId = "opencode-go",
                        ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
                        ModelProtocols = TranslationProviderPreset.Find("opencode-go")!.DefaultModelProtocols,
                    },
                ],
            });

            var dialog = NewDialog(store);
            SelectNavCategory(dialog, "translation.providers");
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            list.SelectedItem = FindProviderRow(list, "opencode go");

            // 锚点：anthropic 协议 + Anthropic base（不带 /v1）。
            Assert.Equal("minimax-m3", FindModelBox(detail).Text);
            Assert.Equal("https://opencode.ai/zen/go", FindEndpointBox(detail).Text);
            Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, GetSelectedProtocol(dialog));

            // 模拟「拉取模型」下拉已填充（直写 Items，不发网络请求），选中 openai 系模型。
            var fetchedCombo = FindFetchedModelCombo(dialog);
            var target = new ComboBoxItem { Content = "deepseek-v4.1-flash", Tag = "deepseek-v4.1-flash" };
            fetchedCombo.Items.Add(target);
            fetchedCombo.SelectedItem = target; // → :861 SelectionChanged → OnFetchedModelSelected(:1557)

            // 模型写回 _modelBox（OnFetchedModelSelected 唯一职责）；协议/端点随所选模型刷新：
            // deepseek-v4.1-flash 未命中 DefaultModelProtocols → 回退预设 DefaultProtocol = openai。
            Assert.Equal("deepseek-v4.1-flash", FindModelBox(detail).Text);
            Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, GetSelectedProtocol(dialog));
            // 端点仍是预设默认 base → SyncDefaultEndpointForProtocol 切到 OpenAI base（协议/端点同源，不产生 /v1/v1）。
            Assert.Equal("https://opencode.ai/zen/go/v1", FindEndpointBox(detail).Text);

            FindProviderSaveButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var saved = Assert.Single(store.Load().TranslationProviders);
            Assert.Equal("deepseek-v4.1-flash", saved.Model);
            Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, saved.ApiProtocol);
            Assert.Equal("https://opencode.ai/zen/go/v1", saved.Endpoint);
        });
    }

    /// <summary>
    /// 回归测试（协议反向切换 + 端点回滚）：opencode go 从 anthropic 态切到 openai 系模型后，
    /// <c>SelectProtocol</c> + <c>SyncDefaultEndpointForProtocol</c> 同步切到 OpenAI base；
    /// 再反向切回 anthropic 系模型（minimax-m3）必须把协议与端点一并回滚到 Anthropic base，
    /// 保存后的 apiProtocol 存储与回滚后的对话框态一致（不留 openai 残留）。
    /// </summary>
    [Fact]
    public void ProtocolReverse_SwitchOpenAiModelThenBack_RollsBackProtocolAndEndpoint()
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
                        Provider = "opencode go",
                        Endpoint = "https://opencode.ai/zen/go",
                        Model = "minimax-m3",
                        PresetId = "opencode-go",
                        ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
                        ModelProtocols = TranslationProviderPreset.Find("opencode-go")!.DefaultModelProtocols,
                    },
                ],
            });

            var dialog = NewDialog(store);
            SelectNavCategory(dialog, "translation.providers");
            var (_, _, detail, list) = FindProviderMasterDetail(dialog);
            list.SelectedItem = FindProviderRow(list, "opencode go");

            // 锚点：anthropic 协议 + Anthropic base。
            Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, GetSelectedProtocol(dialog));
            Assert.Equal("https://opencode.ai/zen/go", FindEndpointBox(detail).Text);

            // ① 切到 openai 系模型（DefaultModelProtocols 命中 openai）→ 协议与端点同向刷新。
            FindModelBox(detail).Text = "kimi-k3";
            Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, GetSelectedProtocol(dialog));
            Assert.Equal("https://opencode.ai/zen/go/v1", FindEndpointBox(detail).Text); // OpenAI base

            // ② 反向切回 anthropic 系模型 → SelectProtocol + SyncDefaultEndpointForProtocol 必须回滚协议与端点。
            //    端点恰回到 Anthropic base 本身即协议回滚的证据：仅当 _protocol 归一化为 anthropic 时，
            //    DefaultEndpointForProtocol 才会返回该值（OpenAI base 与之不同）。
            FindModelBox(detail).Text = "minimax-m3";
            Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, GetSelectedProtocol(dialog));
            Assert.Equal("https://opencode.ai/zen/go", FindEndpointBox(detail).Text);

            FindProviderSaveButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var saved = Assert.Single(store.Load().TranslationProviders);
            Assert.Equal("minimax-m3", saved.Model);
            Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, saved.ApiProtocol); // apiProtocol 存储与回滚后一致
            Assert.Equal("https://opencode.ai/zen/go", saved.Endpoint);
        });
    }

    // ================================================================ Task 6 P1：
    // (A) HostPane 宿主可见性 Theory；(B) providers 无宿主护栏；(C) 右栏 MaxHeight 语义。

    /// <summary>
    /// 回归测试（HostPane 宿主可见性）：除 translation.providers 外的 5 个分类各自包一层
    /// HostPane ScrollViewer（SettingsDialog.cs:349-357）。切到某分类时其宿主 Visible、
    /// 其余 row 1 子元素（其余宿主 + providers 直挂面板）全部 Collapsed，且宿主 Content
    /// 恰为对应内层面板；随后切回 general，general 宿主恢复 Visible、其余 Collapsed。
    /// 只翻内层 Visibility 会留下空宿主盖住命中测试（见 OnlyActiveCategoryHost 事实）。
    /// </summary>
    [Theory]
    [InlineData("appearance")]
    [InlineData("translation.glossary")]
    [InlineData("translation.output")]
    [InlineData("advanced")]
    public void HostedCategory_HostVisibleOthersCollapsed_WrapperContentIsPane(string categoryKey)
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());
            var contentHost = Descendants(dialog).OfType<Grid>().Single(g => g.Name == "SettingsContentHost");

            SelectNavCategory(dialog, categoryKey);
            var pane = FindPane(dialog, CategoryToPaneName[categoryKey]);
            var host = Assert.IsType<ScrollViewer>(LogicalTreeHelper.GetParent(pane));
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(Visibility.Visible, pane.Visibility);
            Assert.Same(pane, host.Content); // 宿主 Content = 内层面板（HostPane 包装）
            foreach (var child in contentHost.Children.OfType<FrameworkElement>()
                         .Where(fe => Grid.GetRow(fe) == 1 && !ReferenceEquals(fe, host)))
            {
                Assert.Equal(Visibility.Collapsed, child.Visibility);
            }

            // 切回 general：general 宿主恢复，其余（含刚访问的宿主与 providers 面板）折叠。
            SelectNavCategory(dialog, "general");
            var generalPane = FindPane(dialog, "Pane_general");
            var generalHost = Assert.IsType<ScrollViewer>(LogicalTreeHelper.GetParent(generalPane));
            Assert.Equal(Visibility.Visible, generalHost.Visibility);
            Assert.Same(generalPane, generalHost.Content);
            foreach (var child in contentHost.Children.OfType<FrameworkElement>()
                         .Where(fe => Grid.GetRow(fe) == 1 && !ReferenceEquals(fe, generalHost)))
            {
                Assert.Equal(Visibility.Collapsed, child.Visibility);
            }
        });
    }

    /// <summary>
    /// 回归护栏（providers 无宿主）：translation.providers 不登记进 <c>_paneHosts</c>，
    /// 面板直接挂 contentHost row 1（SettingsDialog.cs:351 注释：主从左右栏自带滚动）。
    /// 若有人给它补一层 HostPane，会重新引入外层滚动 → Star 行退化 → 列表被裁切的缺陷。
    /// </summary>
    [Fact]
    public void ProvidersCategory_HasNoHostPane_RowsDirectlyOnContentHost()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());
            var contentHost = Descendants(dialog).OfType<Grid>().Single(g => g.Name == "SettingsContentHost");

            // (a) _paneHosts 恰为 5 个宿主键，值全是 HostPane ScrollViewer；不含 providers。
            var hosts = (Dictionary<string, FrameworkElement>)typeof(SettingsDialog)
                .GetField("_paneHosts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(dialog)!;
            Assert.Equal(5, hosts.Count);
            foreach (var expected in new[]
                     {
                         "general", "appearance", "translation.glossary", "translation.output", "advanced",
                     })
            {
                Assert.Contains(expected, hosts.Keys);
            }

            Assert.False(hosts.ContainsKey("translation.providers"),
                "providers 不应有 HostPane 宿主：左右栏自带滚动（SettingsDialog.cs:351）");
            Assert.All(hosts.Values, v => Assert.IsType<ScrollViewer>(v));

            // (b) 内容树：providers 面板父级就是 contentHost（Grid），不是 ScrollViewer 宿主。
            var providers = FindPane(dialog, "Pane_translation_providers");
            Assert.Same(contentHost, LogicalTreeHelper.GetParent(providers));
            Assert.Equal(1, Grid.GetRow(providers));
        });
    }

    /// <summary>
    /// 回归测试（MaxHeight 语义）：主从详情两栏都不得再设固定 MaxHeight=423 —— 该值在
    /// 880×560 窗口超出 pane body 可用高度，下半截永远落在可视区外（SettingsDialog.cs:704/:724/:742
    /// 已移除）。同时：contentHost Star 行必须拿到真实高度（否则 Star 退化为 Auto），
    /// 左栏列表视口 > 0 且滚动 extent &gt;= 视口。左栏裁切细节由
    /// <see cref="ProviderList_FitsPaneBody_AndLastRowsReachableByScrolling"/> 覆盖，此事实专守
    /// MaxHeight 回归（右栏 423 不会让左栏事实变红）。
    /// </summary>
    [Fact]
    public void ProviderMasterDetail_NoFixedMaxHeight_StarRowRealHeight_ExtentCoversViewport()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fx = new Fixture();
            var dialog = NewDialog(fx.NewStore());
            SelectNavCategory(dialog, "translation.providers");

            // 执行布局：Measure → Arrange → UpdateLayout，使 Star 行分配真实高度。
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(880, 516));
            content.Arrange(new Rect(0, 0, 880, 516));
            content.UpdateLayout();

            var contentHost = Descendants(dialog).OfType<Grid>().Single(g => g.Name == "SettingsContentHost");
            var pane = FindPane(dialog, "Pane_translation_providers");
            var (_, left, right, list) = FindProviderMasterDetail(dialog);

            // (a) 右栏 ScrollViewer 与左栏 ListBox 不得设固定上限（历史缺陷值 423）。
            //     WPF MaxHeight 默认 +∞（不是 NaN）；未设限 = NaN 或 +∞。
            Assert.NotEqual(423d, right.MaxHeight);
            Assert.True(double.IsNaN(right.MaxHeight) || double.IsPositiveInfinity(right.MaxHeight),
                $"右栏 MaxHeight 应未设限（NaN/∞），实际 {right.MaxHeight}");
            Assert.NotEqual(423d, list.MaxHeight);
            Assert.True(double.IsNaN(list.MaxHeight) || double.IsPositiveInfinity(list.MaxHeight),
                $"列表 MaxHeight 应未设限（NaN/∞），实际 {list.MaxHeight}");

            // (b) contentHost Star 行（row 1）拿到真实高度：当前唯一可见子元素 ActualHeight > 0。
            var visibleRow1 = contentHost.Children.OfType<FrameworkElement>()
                .Single(fe => Grid.GetRow(fe) == 1 && fe.Visibility == Visibility.Visible);
            Assert.True(visibleRow1.ActualHeight > 0, $"Star 行高度应为正，实际 {visibleRow1.ActualHeight}");
            // providers 无宿主 → 可见子元素就是面板本体；右栏随 master Grid 星号列填满。
            Assert.Same(pane, visibleRow1);
            Assert.True(right.ActualHeight > 0, $"右栏 ActualHeight 应为正，实际 {right.ActualHeight}");
            Assert.True(left.ActualHeight > 0, $"左栏 ActualHeight 应为正，实际 {left.ActualHeight}");

            // (c) 列表视口拿到真实高度，且滚动 extent >= 视口。
            var scroller = Visuals(list).OfType<ScrollViewer>().First();
            Assert.True(scroller.ViewportHeight > 0, $"列表视口高度应为正，实际 {scroller.ViewportHeight}");
            Assert.True(scroller.ExtentHeight >= scroller.ViewportHeight,
                $"列表 extent {scroller.ExtentHeight} 应 >= 视口 {scroller.ViewportHeight}");
        });
    }

    /// <summary>按可视树递归遍历（ListBox 内部滚动条等模板部件不在逻辑树中）。</summary>
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Visuals(child)) yield return nested;
        }
    }
}
