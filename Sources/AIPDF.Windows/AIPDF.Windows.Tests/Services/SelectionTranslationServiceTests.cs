// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译服务层单测：未配置返回可读错误且不发请求 / 配置就绪经 translate_text 传参 /
// 引擎失败透传原因 / sidecar 占位不假装成功。
using System.Text.Json;
using AIPDF.Windows.Bridge;
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
        // 密钥只经 options（→ stdin）传递，绝不进 argv / 环境变量。
        Assert.Equal("sk-test", bridge.LastRequest.Options["apiKey"]);
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
    public async Task NullSidecarStore_ReportsNotWired_WithoutThrowing()
    {
        var store = new NullSelectionSidecarStore();

        var result = await store.SaveAsync(
            new SelectionSidecarEntry("a.pdf", 0, "Hello", "你好", "en", "zh"));

        Assert.False(result.Ok);
        Assert.Contains("尚未接入", result.Message);
    }
}
