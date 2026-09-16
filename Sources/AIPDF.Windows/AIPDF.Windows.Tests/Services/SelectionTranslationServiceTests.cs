// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译服务层单测：未配置返回可读错误且不发请求 / 配置就绪经 translate_text 传参 /
// 引擎失败透传原因 / sidecar 占位不假装成功。
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public class SelectionTranslationServiceTests
{
    /// <summary>stub 桥：不启动子进程，只记录最后一次请求并返回预置结果。</summary>
    private sealed class StubBridge : EngineBridge
    {
        private readonly EngineResult _result;

        public StubBridge(EngineResult result) : base("python", "engine.py") => _result = result;

        public EngineRequest? LastRequest { get; private set; }

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class FixedConfigProvider : ITranslationRuntimeConfigProvider
    {
        private readonly TranslationRuntimeConfig? _config;

        public FixedConfigProvider(TranslationRuntimeConfig? config) => _config = config;

        public TranslationRuntimeConfig? Get() => _config;
    }

    /// <summary>
    /// 模拟生产 <c>SettingsTranslationRuntimeConfigProvider</c> 的寻址语义：按选中稳定 id 选择目的地；
    /// id 为 null / 未知时回退 <see cref="Fallback"/>（本地优先）。
    /// </summary>
    private sealed class SelectedIdConfigProvider : ITranslationRuntimeConfigProvider
    {
        private readonly IReadOnlyDictionary<string, TranslationRuntimeConfig> _configs;

        public SelectedIdConfigProvider(
            IReadOnlyDictionary<string, TranslationRuntimeConfig> configs,
            TranslationRuntimeConfig fallback)
        {
            _configs = configs;
            Fallback = fallback;
        }

        public TranslationRuntimeConfig Fallback { get; }

        public TranslationRuntimeConfig? Get() => Fallback;

        public TranslationRuntimeConfigResolution Resolve(string? selectedProviderId = null)
        {
            if (selectedProviderId is not null && _configs.TryGetValue(selectedProviderId, out var config))
            {
                return new TranslationRuntimeConfigResolution(
                    TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint,
                    !TranslationProviderConfig.IsLocalService(selectedProviderId, config.Provider), "");
            }

            return new TranslationRuntimeConfigResolution(
                TranslationRuntimeConfigStatus.Configured, Fallback, Fallback.Provider, Fallback.Endpoint,
                !TranslationProviderConfig.IsLocalService(null, Fallback.Provider), "");
        }
    }

    private static EngineResult Ok(string translation, double elapsed = 1.0)
        => new()
        {
            Ok = true,
            Elapsed = elapsed,
            Extra = new Dictionary<string, JsonElement>
            {
                ["translation"] = JsonSerializer.SerializeToElement(translation),
            },
        };

    [Fact]
    public async Task Translate_WithoutConfig_ReturnsActionableError_AndDoesNotCallEngine()
    {
        var bridge = new StubBridge(Ok("unused"));
        var service = new EngineSelectionTranslationService(bridge, new FixedConfigProvider(null));

        var result = await service.TranslateAsync(new SelectionTranslationRequest("Hello", "en", "zh", "generic"));

        Assert.False(result.Ok);
        Assert.Contains("尚未配置", result.Error);
        Assert.Null(bridge.LastRequest);
    }

    [Fact]
    public async Task Translate_WithConfig_SendsTextViaOptions_AndReturnsTranslation()
    {
        var bridge = new StubBridge(Ok("你好", 1.5));
        var config = new TranslationRuntimeConfig(
            "local-ollama", "http://127.0.0.1:11434/v1", "qwen", "sk-test", "en", "zh", "academic");
        var service = new EngineSelectionTranslationService(bridge, new FixedConfigProvider(config));

        var result = await service.TranslateAsync(new SelectionTranslationRequest("Hello", "en", "zh", "academic"));

        Assert.True(result.Ok);
        Assert.Equal("你好", result.Translation);
        Assert.Equal(1.5, result.ElapsedSeconds);
        Assert.NotNull(bridge.LastRequest);
        Assert.Equal("translate_text", bridge.LastRequest!.Operation);
        Assert.Equal("Hello", bridge.LastRequest.Options["text"]);
        Assert.Equal("http://127.0.0.1:11434/v1", bridge.LastRequest.Options["endpoint"]);
        // 协议随运行时配置贯通（v1.2 T6）：未显式指定即缺省 openai（T4 契约）。
        Assert.Equal("openai", bridge.LastRequest.Options["apiProtocol"]);
        // 密钥只经 options（→ stdin）传递，绝不进 argv / 环境变量。
        Assert.Equal("sk-test", bridge.LastRequest.Options["apiKey"]);
    }

    [Fact]
    public async Task Translate_WithAnthropicRuntimeConfig_SendsApiProtocol()
    {
        var bridge = new StubBridge(Ok("你好"));
        var config = new TranslationRuntimeConfig(
            "glm-coding-plan", "https://open.bigmodel.cn/api/anthropic", "glm-5.3", "sk-test",
            "en", "zh", "generic", ApiProtocol: "anthropic");
        var service = new EngineSelectionTranslationService(bridge, new FixedConfigProvider(config));

        var result = await service.TranslateAsync(new SelectionTranslationRequest("Hello", "en", "zh", "generic"));

        Assert.True(result.Ok);
        Assert.Equal("anthropic", bridge.LastRequest!.Options["apiProtocol"]);
        // provider 名 / 显示名绝不进入划词 options。
        Assert.DoesNotContain("provider", bridge.LastRequest.Options.Keys);
        Assert.DoesNotContain("presetId", bridge.LastRequest.Options.Keys);
    }

    [Fact]
    public async Task Translate_EngineFailure_SurfacesError()
    {
        var bridge = new StubBridge(new EngineResult { Ok = false, Error = "模型拒绝翻译这段内容。" });
        var config = new TranslationRuntimeConfig(
            "cloud", "https://api.example.com/v1", "m", "k", "en", "zh", "generic");
        var service = new EngineSelectionTranslationService(bridge, new FixedConfigProvider(config));

        var result = await service.TranslateAsync(new SelectionTranslationRequest("Hello", "en", "zh", "generic"));

        Assert.False(result.Ok);
        Assert.Equal("模型拒绝翻译这段内容。", result.Error);
    }

    [Fact]
    public async Task Translate_MissingTranslationInResult_ReportsFailure()
    {
        var bridge = new StubBridge(new EngineResult { Ok = true });
        var config = new TranslationRuntimeConfig(
            "local", "http://127.0.0.1:11434/v1", "m", "k", "en", "zh", "generic");
        var service = new EngineSelectionTranslationService(bridge, new FixedConfigProvider(config));

        var result = await service.TranslateAsync(new SelectionTranslationRequest("Hello", "en", "zh", "generic"));

        Assert.False(result.Ok);
        Assert.Contains("未返回译文", result.Error);
    }

    [Fact]
    public void RuntimeConfig_ToString_RedactsApiKey()
    {
        var config = new TranslationRuntimeConfig(
            "cloud", "https://api.example.com/v1", "m", "sk-super-secret-value",
            "en", "zh", "generic");

        var text = config.ToString();

        Assert.DoesNotContain("sk-super-secret-value", text);
        Assert.Contains("ApiKey", text);
        Assert.Contains("<redacted>", text);
    }

    [Fact]
    public void NullSidecarStore_ReportsNotWired_WithoutThrowing()
    {
        var store = new NullSelectionSidecarStore { ActiveDocument = "a.pdf" };

        var result = store.Add(0, new PdfRect(1, 2, 3, 4), "你好", "en→zh");

        Assert.False(result.Ok);
        Assert.Contains("尚未接入", result.Message);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task Translate_WithSelectedCloudProvider_SendsSelectedProviderEndpoint()
    {
        var bridge = new StubBridge(Ok("你好"));
        var local = new TranslationRuntimeConfig(
            "local", "http://127.0.0.1:11434/v1", "qwen", "", "en", "zh", "generic");
        var deepseek = new TranslationRuntimeConfig(
            "deepseek", "https://api.deepseek.com/chat/completions", "deepseek-chat", "sk-test",
            "en", "zh", "generic");
        var provider = new SelectedIdConfigProvider(
            new Dictionary<string, TranslationRuntimeConfig> { ["deepseek"] = deepseek }, local);
        var service = new EngineSelectionTranslationService(bridge, provider);

        var result = await service.TranslateAsync(
            new SelectionTranslationRequest("Hello", "en", "zh", "generic", SelectedProviderId: "deepseek"));

        Assert.True(result.Ok);
        Assert.NotNull(bridge.LastRequest);
        var endpoint = Assert.IsType<string>(bridge.LastRequest!.Options["endpoint"]);
        Assert.Contains("api.deepseek.com", endpoint);
        Assert.Equal("sk-test", bridge.LastRequest.Options["apiKey"]);
    }

    [Fact]
    public async Task Translate_WithoutSelectedProviderId_FallsBackToLocalEndpoint()
    {
        var bridge = new StubBridge(Ok("你好"));
        var local = new TranslationRuntimeConfig(
            "local", "http://127.0.0.1:11434/v1", "qwen", "", "en", "zh", "generic");
        var deepseek = new TranslationRuntimeConfig(
            "deepseek", "https://api.deepseek.com/chat/completions", "deepseek-chat", "sk-test",
            "en", "zh", "generic");
        var provider = new SelectedIdConfigProvider(
            new Dictionary<string, TranslationRuntimeConfig> { ["deepseek"] = deepseek }, local);
        var service = new EngineSelectionTranslationService(bridge, provider);

        var result = await service.TranslateAsync(
            new SelectionTranslationRequest("Hello", "en", "zh", "generic", SelectedProviderId: null));

        Assert.True(result.Ok);
        Assert.NotNull(bridge.LastRequest);
        var endpoint = Assert.IsType<string>(bridge.LastRequest!.Options["endpoint"]);
        Assert.Contains("127.0.0.1", endpoint);
        Assert.DoesNotContain("api.deepseek.com", endpoint);
    }
}
