// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T17 端到端集成测试（C# 装配链）：
//   面板选择（TranslationConfigViewModel.SelectedProvider）→ Resolve(selectedProviderId)
//   → TranslationRuntimeConfig（endpoint / model / apiProtocol）→ TranslationEngineOptions.Build
//   → 跨语言：引擎侧 tests/test_translate_provider_integration.py 断言最终 method/path/header/body
//     （Anthropic → POST {base}/v1/messages；OpenAI → POST {base}/chat/completions）。
//
// 覆盖（计划 §T17）：
//   - 本地 loopback 全链路（无需密钥 / 同意，拉取模型零鉴权头）；
//   - 云端 Anthropic 全链路（kimi-code / glm-coding-plan，含模型拉取 {base}/v1/models）；
//   - 编程套餐端点告警 → 「改用标准 API」→ 标准端点可解析且旧凭据 / 同意不被静默丢弃；
//   - 旧 v1.1 设置迁移（PresetId 回填 / 端点逐字保留 / ApiProtocol=openai）后直接翻译；
//   - 边界：同意阻断运行时配置（未同意 → 无 Config）、超时重试恰好 3 次、未知模型提示。
//
// 全部 mock：InMemoryCredentialBackend（不碰真实凭据管理器）+ 内存 HttpMessageHandler
// （不发真实网络请求，复用 LocalModelFetcherTests 的 ModelListHandler）；密钥均为可识别假值。
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Settings;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationProviderIntegrationTests
{
    private const string Key = "sk-t17-integration-key-3b91";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public SettingsStore Settings { get; }
        public InMemoryCredentialBackend Backend { get; }
        public TranslationCredentialStore Credentials { get; }
        public TranslationConsentService Consent { get; }
        public DateTimeOffset Now { get; set; } = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-t17-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new SettingsStore(Path.Combine(Root, "settings.json"));
            Backend = new InMemoryCredentialBackend();
            Credentials = new TranslationCredentialStore(Backend);
            Consent = new TranslationConsentService(Settings, Credentials, () => Now);
        }

        /// <summary>生产运行时提供者（设置 + 凭据 + 同意三源合一）。</summary>
        public SettingsTranslationRuntimeConfigProvider NewProvider() => new(Settings, Credentials, Consent);

        /// <summary>面板 VM：宿主从设置读取已保存配置后注入（与生产装配同源，VM 不做 IO）。</summary>
        public TranslationConfigViewModel NewPanel() => new(Settings.Load().TranslationProviders);

        public void SaveProviders(params TranslationProviderConfig[] providers)
        {
            var settings = Settings.Load();
            settings.TranslationProviders = [.. providers];
            Settings.Save(settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>按预设表构造一条「用户在设置页保存」的连接配置（端点按默认协议选取）。</summary>
    private static TranslationProviderConfig PresetConfig(string presetId, string? model = null)
    {
        var preset = TranslationProviderPreset.Find(presetId)!;
        var endpoint = string.Equals(preset.DefaultProtocol, TranslationProviderConfig.ProtocolAnthropic,
                StringComparison.OrdinalIgnoreCase)
            ? preset.AnthropicBaseUrl!
            : preset.OpenAiBaseUrl!;
        return new TranslationProviderConfig
        {
            PresetId = preset.Id,
            Provider = preset.DisplayName,
            Endpoint = endpoint,
            Model = model ?? preset.DefaultModels.FirstOrDefault() ?? "",
            ApiProtocol = preset.DefaultProtocol,
            ModelProtocols = preset.DefaultModelProtocols,
        };
    }

    /// <summary>把运行时配置送入唯一装配点（与宿主 BuildOptions 同参）。</summary>
    private static Dictionary<string, object?> BuildOptions(TranslationRuntimeConfig config)
        => TranslationEngineOptions.Build(
            config.Endpoint, config.Model, config.ApiKey, config.ApiProtocol,
            config.SourceLang, config.TargetLang, config.Template,
            generateMono: true, generateDual: true, exportMarkdown: false, marker: true);

    /// <summary>按稳定身份键存密钥并授予同意（等价于设置页「保存到凭据管理器」+ F5 同意）。</summary>
    private static void StoreCredentialAndConsent(Fixture fx, TranslationProviderConfig provider)
    {
        var identity = TranslationProviderIdentity.For(provider);
        fx.Credentials.Store(identity, provider.Endpoint, Key);
        fx.Consent.GrantConsent(identity, provider.Endpoint, Key);
    }

    // ---------------------------------------------------------------- 面板选择 → 运行时目的地 / 协议

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "openai")]
    [InlineData("anthropic", "https://api.anthropic.com", "anthropic")]
    [InlineData("glm-coding-plan", "https://open.bigmodel.cn/api/anthropic", "anthropic")]
    [InlineData("kimi-code", "https://api.kimi.com/coding", "anthropic")]
    public void PanelSelection_DrivesRuntimeDestination_ThenAssemblesOptions(
        string presetId, string expectedEndpoint, string expectedProtocol)
    {
        using var fx = new Fixture();
        var provider = PresetConfig(presetId);
        fx.SaveProviders(provider);
        StoreCredentialAndConsent(fx, provider);

        var panel = fx.NewPanel();
        panel.SelectedProvider = presetId;

        // 运行时：SelectedProvider → Resolve → 目的地/协议严格等于选中项（修复「引擎单选不驱动目的地」）。
        var resolution = fx.NewProvider().Resolve(panel.SelectedProvider);

        Assert.True(resolution.IsConfigured);
        Assert.True(resolution.IsCloud);
        Assert.Equal(expectedEndpoint, resolution.Config!.Endpoint);
        Assert.Equal(panel.SelectedModel, resolution.Config.Model);
        Assert.Equal(panel.Protocol, resolution.Config.ApiProtocol); // 面板协议 == 运行时协议
        Assert.Equal(expectedProtocol, resolution.Config.ApiProtocol);

        // 装配：options 键值与面板 / 运行时一致；provider 名不进入 options。
        var options = BuildOptions(resolution.Config!);
        Assert.Equal(expectedEndpoint, options["endpoint"]);
        Assert.Equal(panel.SelectedModel, options["model"]);
        Assert.Equal(expectedProtocol, options["apiProtocol"]);
        Assert.Equal(Key, options["apiKey"]);
        Assert.DoesNotContain("provider", options.Keys);
        Assert.DoesNotContain(provider.Provider, options.Values.OfType<string>());
    }

    [Fact]
    public void PanelProviderSwitch_MovesRuntimeDestination_NotFirstSaved()
    {
        using var fx = new Fixture();
        // deepseek 先保存（旧 local-first 规则会回落首个云端），随后 kimi-code 与本地服务。
        var deepseek = PresetConfig("deepseek");
        var kimiCode = PresetConfig("kimi-code");
        var local = new TranslationProviderConfig
        {
            PresetId = "local",
            Provider = "本地服务",
            Endpoint = "http://127.0.0.1:11434/v1",
            Model = "qwen2.5:7b",
        };
        fx.SaveProviders(deepseek, kimiCode, local);
        StoreCredentialAndConsent(fx, deepseek);
        StoreCredentialAndConsent(fx, kimiCode);

        var panel = fx.NewPanel();
        var runtime = fx.NewProvider();

        panel.SelectedProvider = "kimi-code";
        var kimi = runtime.Resolve(panel.SelectedProvider);
        Assert.True(kimi.IsConfigured);
        Assert.Equal("https://api.kimi.com/coding", kimi.Config!.Endpoint); // 不是首个保存的 deepseek
        Assert.Equal(panel.Protocol, kimi.Config.ApiProtocol);
        Assert.Equal("anthropic", kimi.Config.ApiProtocol);

        panel.SelectedProvider = "deepseek";
        var deep = runtime.Resolve(panel.SelectedProvider);
        Assert.True(deep.IsConfigured);
        Assert.Equal("https://api.deepseek.com/v1", deep.Config!.Endpoint);
        Assert.Equal(panel.Protocol, deep.Config.ApiProtocol);
        Assert.Equal("openai", deep.Config.ApiProtocol);

        panel.SelectedProvider = "local";
        var localResolution = runtime.Resolve(panel.SelectedProvider);
        Assert.True(localResolution.IsConfigured);
        Assert.False(localResolution.IsCloud);
        Assert.Equal("http://127.0.0.1:11434/v1", localResolution.Config!.Endpoint);
        Assert.Equal("", localResolution.Config.ApiKey); // 本地 loopback 无需密钥
    }

    [Fact]
    public void MixedProtocolPreset_SavedWithModelProtocol_KeepsPanelAndRuntimeAligned()
    {
        // 设置页对混合协议供应商按当前模型自动切换协议（RefreshProtocolForModel → 保存 ApiProtocol）；
        // 这里以 ModelProtocol 的计算结果作为保存值，验证面板与运行时一致。
        var preset = TranslationProviderPreset.Find("opencode-go")!;
        const string model = "minimax-m3";
        var savedProtocol = TranslationProviderCompliance.ModelProtocol(preset.Id, model, preset.DefaultModelProtocols);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, savedProtocol);

        using var fx = new Fixture();
        // 翻译路线直接使用已保存端点（引擎层拼 /v1/messages），故端点必须与协议匹配：
        // Anthropic base 不含 /v1；设置页拉取路线则会经 NormalizeRequestBase 按协议归一化。
        var provider = PresetConfig("opencode-go", model);
        provider.ApiProtocol = savedProtocol!;
        provider.Endpoint = preset.AnthropicBaseUrl!;
        Assert.Equal(preset.AnthropicBaseUrl, ProviderUrl.NormalizeRequestBase(preset.OpenAiBaseUrl, "anthropic"));
        fx.SaveProviders(provider);
        StoreCredentialAndConsent(fx, provider);

        var panel = fx.NewPanel();
        panel.SelectedProvider = "opencode-go";
        Assert.Equal(model, panel.SelectedModel);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, panel.Protocol); // 面板按模型派生

        var resolution = fx.NewProvider().Resolve(panel.SelectedProvider);
        Assert.True(resolution.IsConfigured);
        Assert.Equal("https://opencode.ai/zen/go", resolution.Config!.Endpoint); // Anthropic base 不含 /v1
        Assert.Equal(panel.Protocol, resolution.Config.ApiProtocol);

        var options = BuildOptions(resolution.Config!);
        Assert.Equal("https://opencode.ai/zen/go", options["endpoint"]);
        Assert.Equal(panel.Protocol, options["apiProtocol"]);
    }

    // ---------------------------------------------------------------- 本地 loopback 全链路

    [Fact]
    public async Task LocalLoopback_ResolvesAndFetchesModels_WithoutAnyAuthHeader()
    {
        using var fx = new Fixture();
        var local = new TranslationProviderConfig
        {
            PresetId = "local",
            Provider = "本地服务",
            Endpoint = "http://127.0.0.1:11434/v1",
            Model = "qwen2.5:7b",
        };
        fx.SaveProviders(local);

        var panel = fx.NewPanel(); // 默认选中本地服务（决策 #15）
        Assert.Equal("local", panel.SelectedProvider);

        var resolution = fx.NewProvider().Resolve(panel.SelectedProvider);
        Assert.True(resolution.IsConfigured);
        Assert.False(resolution.IsCloud);
        Assert.Equal(local.Endpoint, resolution.Config!.Endpoint);
        Assert.Equal("", resolution.Config.ApiKey); // loopback：未存密钥也放行

        var options = BuildOptions(resolution.Config!);
        Assert.Equal(local.Endpoint, options["endpoint"]);
        Assert.Equal("openai", options["apiProtocol"]);
        Assert.Equal("", options["apiKey"]);

        // 拉取模型：GET {请求基址}/models，本地零密钥外发（T5 规则 7 的 loopback 分支）。
        var handler = new ModelListHandler()
            .ReplyJson("""{"data":[{"id":"qwen2.5:7b"},{"id":"llama3.2:3b"}]}""");
        var fetcher = new LocalModelFetcher(handler);
        var models = await fetcher.FetchAsync(
            ProviderUrl.NormalizeRequestBase(resolution.Config.Endpoint, resolution.Config.ApiProtocol),
            resolution.Config.ApiKey, resolution.Config.ApiProtocol);

        Assert.Equal(new[] { "qwen2.5:7b", "llama3.2:3b" }, models);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("http://127.0.0.1:11434/v1/models", request.Uri);
        Assert.DoesNotContain("/v1/v1", request.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));

        // 拉取结果回填面板（UI 的 SetFetchedModels 路径）：已保存模型保持，列表按服务端结果呈现。
        panel.SetFetchedModels(models);
        Assert.Equal("qwen2.5:7b", panel.SelectedModel);
        Assert.Equal(new[] { "qwen2.5:7b", "llama3.2:3b" }, panel.AvailableModels);
    }

    // ---------------------------------------------------------------- 云端 Anthropic：拉取路线形状

    [Fact]
    public async Task AnthropicPreset_Fetch_UsesV1ModelsPath_WithXApiKey()
    {
        using var fx = new Fixture();
        var provider = PresetConfig("kimi-code");
        fx.SaveProviders(provider);
        StoreCredentialAndConsent(fx, provider); // 已保存端点 + 已同意 → 携带密钥

        var resolution = fx.NewProvider().Resolve("kimi-code");
        Assert.True(resolution.IsConfigured);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, resolution.Config!.ApiProtocol);

        var handler = new ModelListHandler().ReplyJson("""{"data":[{"id":"k3"}]}""");
        var fetcher = new LocalModelFetcher(handler);
        var models = await fetcher.FetchAsync(
            ProviderUrl.NormalizeRequestBase(resolution.Config.Endpoint, resolution.Config.ApiProtocol),
            resolution.Config.ApiKey, resolution.Config.ApiProtocol);

        Assert.Equal(new[] { "k3" }, models);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.kimi.com/coding/v1/models", request.Uri); // Anthropic base 补 /v1/models
        Assert.Equal(Key, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Null(request.Header("Authorization"));
    }

    // ---------------------------------------------------------------- 同意门禁 + 超时重试

    [Fact]
    public async Task UnconsentedCloud_BlocksRuntimeConfig_UntilConsentGranted()
    {
        using var fx = new Fixture();
        var provider = PresetConfig("openai");
        fx.SaveProviders(provider);
        var identity = TranslationProviderIdentity.For(provider);
        fx.Credentials.Store(identity, provider.Endpoint, Key); // 已存密钥，但未同意

        // 同意判定链：IsConsented == false → Resolve 返回 NoConsent 且不产出 Config
        //（翻译与任何需要运行时配置的云端动作都被阻断，不存在绕过）。
        Assert.False(fx.Consent.IsConsented(identity, provider.Endpoint, Key));
        Assert.False(fx.Consent.IsConsentedUsingStoredKey(identity, provider.Endpoint));

        var runtime = fx.NewProvider();
        var blocked = runtime.Resolve("openai");
        Assert.Equal(TranslationRuntimeConfigStatus.NoConsent, blocked.Status);
        Assert.True(blocked.NeedsConsent);
        Assert.Null(blocked.Config);

        // 宿主弹 F5 → 用户拒绝 → 仍不可用。
        var declined = await TranslationRuntimeConsentFlow.EnsureAsync(
            runtime, _ => Task.FromResult(false), "openai");
        Assert.Equal(TranslationRuntimeConfigStatus.NoConsent, declined.Status);
        Assert.Null(declined.Config);

        // 用户同意（remember: false 仅本次会话）→ 重新解析可用，目的地 == 选中项。
        var granted = await TranslationRuntimeConsentFlow.EnsureAsync(
            runtime,
            request =>
            {
                fx.Consent.GrantConsent(request.ProviderId, request.Endpoint, Key, remember: false);
                return Task.FromResult(true);
            },
            "openai");

        Assert.True(granted.IsConfigured);
        Assert.Equal(provider.Provider, granted.Config!.Provider);
        Assert.Equal(provider.Endpoint, granted.Config.Endpoint);
        Assert.Equal("openai", granted.Config.ApiProtocol);
    }

    [Fact]
    public async Task Fetch_UnconfirmedCloudEndpoint_SendsNoAuthHeaders_AndNoDocumentContent()
    {
        using var fx = new Fixture();
        var provider = PresetConfig("deepseek");
        fx.SaveProviders(provider); // 未存密钥 = 未经用户确认的端点

        var handler = new ModelListHandler().ReplyJson("""{"data":[]}""");
        var fetcher = new LocalModelFetcher(handler);

        var models = await fetcher.FetchAsync(
            ProviderUrl.NormalizeRequestBase(provider.Endpoint, provider.ApiProtocol), null, provider.ApiProtocol);

        Assert.Empty(models); // 空列表照实呈现（UI 提示手动输入；不静默填充）
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Null(request.Header("Authorization")); // 规则 7：不向未确认端点发送密钥
        Assert.Null(request.Header("x-api-key"));
        Assert.DoesNotContain(Key, request.Uri);
    }

    [Fact]
    public async Task CloudFetchTimeout_RetriesExactlyThreeAttempts_ThenStructuredError()
    {
        using var fx = new Fixture();
        var provider = PresetConfig("deepseek");
        fx.SaveProviders(provider);
        StoreCredentialAndConsent(fx, provider);

        var resolution = fx.NewProvider().Resolve("deepseek");
        Assert.True(resolution.IsConfigured);

        var handler = new ModelListHandler().Throw(_ => new OperationCanceledException("模拟超时"));
        var fetcher = new LocalModelFetcher(handler);
        var exception = await Assert.ThrowsAsync<LocalModelFetchException>(() => fetcher.FetchAsync(
            ProviderUrl.NormalizeRequestBase(resolution.Config!.Endpoint, resolution.Config.ApiProtocol),
            resolution.Config.ApiKey, resolution.Config.ApiProtocol));

        Assert.Equal(LocalModelFetchFailureKind.Timeout, exception.Kind);
        Assert.Equal(3, handler.Requests.Count); // 1 次 + 2 次重试
        Assert.Equal(LocalModelFetcher.MaxAttempts, handler.Requests.Count);
        Assert.DoesNotContain(Key, exception.ToString());
    }

    // ---------------------------------------------------------------- 编程套餐告警 → 改用标准 API

    [Fact]
    public void CodingPlan_WarningThenSwitchToStandardApi_KeepsOldCredentials_AndResolvesStandard()
    {
        using var fx = new Fixture();
        var coding = PresetConfig("glm-coding-plan");
        fx.SaveProviders(coding);
        StoreCredentialAndConsent(fx, coding);

        // 常驻合规警示来自预设表（唯一权威来源）；套餐端点先记录旧凭据 / 同意。
        const string codingEndpoint = "https://open.bigmodel.cn/api/anthropic";
        Assert.Equal(TranslationProviderPresetKind.CodingPlan,
            TranslationProviderPreset.Find("glm-coding-plan")!.Kind);
        Assert.True(TranslationProviderCompliance.RequiresComplianceNotice("glm-coding-plan"));
        Assert.Contains("标准 API", TranslationProviderCompliance.ComplianceNotice("glm-coding-plan")!);
        Assert.Equal(Key, fx.Credentials.Get("glm-coding-plan", codingEndpoint));

        // 一键切换：套餐端点 → 同族标准 API（zhipu）；不静默丢弃旧凭据 / 同意。
        Assert.True(TranslationProviderCompliance.TrySwitchToStandardApi(coding));
        Assert.Equal("zhipu", coding.PresetId);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", coding.Endpoint);
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, coding.ApiProtocol);
        Assert.Null(coding.ModelProtocols);
        Assert.Equal(Key, fx.Credentials.Get("glm-coding-plan", codingEndpoint)); // 旧套餐凭据原样保留
        Assert.True(fx.Consent.IsConsented("glm-coding-plan", codingEndpoint, Key));
        Assert.False(string.IsNullOrWhiteSpace(TranslationProviderCompliance.StandardApiCredentialNotice));

        // 切换后的配置落盘 → 标准端点独立凭据 + 同意 → 面板选中 zhipu → 目的地 == 标准端点。
        fx.SaveProviders(coding);
        StoreCredentialAndConsent(fx, coding);

        var panel = fx.NewPanel();
        panel.SelectedProvider = "zhipu";
        var resolution = fx.NewProvider().Resolve(panel.SelectedProvider);

        Assert.True(resolution.IsConfigured);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", resolution.Config!.Endpoint);
        Assert.Equal(panel.Protocol, resolution.Config.ApiProtocol);
        Assert.Equal("openai", resolution.Config.ApiProtocol);

        var options = BuildOptions(resolution.Config!);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", options["endpoint"]);
        Assert.Equal("openai", options["apiProtocol"]);
        Assert.Equal(panel.SelectedModel, options["model"]);
    }

    // ---------------------------------------------------------------- 旧 v1.1 设置迁移后翻译

    [Fact]
    public void LegacyV11Settings_Migrates_ThenResolvesToMigratedDestination()
    {
        using var fx = new Fixture();
        const string kimiEndpoint = "https://api.moonshot.cn/v1";

        // v1.1 形状：无 presetId / apiProtocol / modelProtocols；同意只存指纹（此处用真实密钥指纹）。
        var legacyJson = $$"""
        {
          "schemaVersion": 2,
          "translationProviders": [
            { "provider": "月之暗面 Kimi", "endpoint": "{{kimiEndpoint}}", "model": "kimi-k3" },
            { "provider": "Corp 网关", "endpoint": "https://gw.corp.example.com/llm/v1", "model": "my-model" }
          ],
          "translationConsents": [
            {
              "provider": "月之暗面 Kimi",
              "endpoint": "https://api.moonshot.cn",
              "keyFingerprint": "{{TranslationConsent.ComputeFingerprint(Key)}}",
              "grantedAt": "2026-09-01T00:00:00+08:00",
              "ttlDays": 180
            }
          ]
        }
        """;
        File.WriteAllText(fx.Settings.FilePath, legacyJson);

        // v1.1 凭据：按显示名寻址（target 名保留原始大小写 / 中文），与稳定 id 不同。
        var legacyTarget = LegacyCredentialTarget("月之暗面 Kimi", kimiEndpoint);
        fx.Backend.Write(legacyTarget, Key);
        Assert.NotEqual(TranslationCredentialStore.TargetName("kimi", kimiEndpoint), legacyTarget);

        // 载入即迁移：PresetId 按显示名回填、端点逐字保留、协议缺省 openai、同意指纹保留。
        var loaded = fx.Settings.Load();
        Assert.Equal(2, loaded.TranslationProviders.Count);
        Assert.Equal("kimi", loaded.TranslationProviders[0].PresetId);
        Assert.Equal(kimiEndpoint, loaded.TranslationProviders[0].Endpoint);
        Assert.Equal("openai", loaded.TranslationProviders[0].ApiProtocol);
        Assert.Null(loaded.TranslationProviders[0].ModelProtocols);
        Assert.Null(loaded.TranslationProviders[1].PresetId); // 自定义网关：endpoint 路径不塌陷
        Assert.Equal("https://gw.corp.example.com/llm/v1", loaded.TranslationProviders[1].Endpoint);
        var consent = Assert.Single(loaded.TranslationConsents);
        Assert.Equal("kimi", consent.Provider); // 同意 provider 收敛为稳定 id

        // 身份迁移：旧名凭据 → 稳定 id；密钥零丢失、旧 target 清理、重复迁移幂等。
        var migration = new TranslationIdentityMigrator(fx.Settings, fx.Credentials).Migrate();
        Assert.True(migration.Changed);
        Assert.Equal(Key, fx.Credentials.Get("kimi", kimiEndpoint));
        Assert.False(fx.Backend.Contains(legacyTarget));
        Assert.Equal(0, new TranslationIdentityMigrator(fx.Settings, fx.Credentials).Migrate().Credentials);

        // 迁移后直接翻译：面板选中 kimi → 运行时目的地 / 协议正确；同意按既有指纹直接命中（无需重新同意）。
        var panel = fx.NewPanel();
        panel.SelectedProvider = "kimi";
        var resolution = fx.NewProvider().Resolve(panel.SelectedProvider);

        Assert.True(resolution.IsConfigured);
        Assert.Equal(kimiEndpoint, resolution.Config!.Endpoint);
        Assert.Equal(Key, resolution.Config.ApiKey);
        Assert.Equal(panel.Protocol, resolution.Config.ApiProtocol);
        Assert.Equal("openai", resolution.Config.ApiProtocol);

        var options = BuildOptions(resolution.Config!);
        Assert.Equal(kimiEndpoint, options["endpoint"]);
        Assert.Equal("openai", options["apiProtocol"]);
        Assert.Equal(Key, options["apiKey"]);
        Assert.DoesNotContain("月之暗面 Kimi", options.Values.OfType<string>()); // 显示名不进入 options
    }

    // ---------------------------------------------------------------- 未知模型 id

    [Fact]
    public async Task Fetch_EmptyList_KeepsManualModel_AndNonListedModelGetsHint()
    {
        using var fx = new Fixture();
        var custom = new TranslationProviderConfig
        {
            Provider = "Corp 网关",
            Endpoint = "https://gw.corp.example.com/llm/v1",
            Model = "my-model",
        };
        fx.SaveProviders(custom);
        StoreCredentialAndConsent(fx, custom);

        var panel = fx.NewPanel();
        panel.SelectedProvider = "corp 网关"; // 自定义规范名（与凭据 / 同意寻址同源）
        var resolution = fx.NewProvider().Resolve(panel.SelectedProvider);

        Assert.True(resolution.IsConfigured);
        Assert.Equal("https://gw.corp.example.com/llm/v1", resolution.Config!.Endpoint); // 路径逐字保留

        // 拉取返回空列表：不静默替换手填模型（UI 另行提示「请手动输入模型名」）。
        var handler = new ModelListHandler().ReplyJson("""{"data":[]}""");
        var fetcher = new LocalModelFetcher(handler);
        var models = await fetcher.FetchAsync(
            ProviderUrl.NormalizeRequestBase(resolution.Config.Endpoint, resolution.Config.ApiProtocol),
            resolution.Config.ApiKey, resolution.Config.ApiProtocol);

        Assert.Empty(models);
        panel.SetFetchedModels(models);
        Assert.Empty(panel.AvailableModels);
        Assert.Equal("my-model", panel.SelectedModel); // 手填值保留
        Assert.Equal("my-model", panel.ModelLabel);

        // 服务端有列表但手填模型不在其中 → P23 提示（说明仍可保存，不阻断）。
        var hint = CustomProviderDraft.ModelListHint(panel.SelectedModel, new[] { "glm-4-flash", "qwen-plus" });
        Assert.NotNull(hint);
        Assert.Contains("不在服务端返回的列表", hint);
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>v1.1 凭据 target：<c>AIPDF-Translate-{显示名}-{sha256(origin)[:16]}</c>（大小写 / 中文原样）。</summary>
    private static string LegacyCredentialTarget(string legacyName, string endpoint)
    {
        var origin = TranslationConsent.NormalizeEndpoint(endpoint);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(origin));
        var shortHash = Convert.ToHexString(hash).ToLowerInvariant()[..TranslationConsent.FingerprintLength];
        return TranslationCredentialStore.TargetPrefix + legacyName + "-" + shortHash;
    }
}
