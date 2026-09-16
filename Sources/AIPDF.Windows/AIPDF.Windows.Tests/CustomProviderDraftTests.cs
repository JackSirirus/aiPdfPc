// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T13 自定义供应商：草稿 → 持久化配置的收敛规则（PresetId=null、端点路径逐字保留、协议归一）
// 与 P23 未知模型提示。纯逻辑单测，不实例化 WPF（WPF 像素级自动化不在范围内）。
using System.IO;
using AIPDF.Windows.Services;
using AIPDF.Windows.Views.Settings;
using Xunit;

namespace AIPDF.Windows.Tests;

public sealed class CustomProviderDraftTests
{
    [Fact]
    public void ToConfig_PreservesEndpointPath_AndLeavesPresetIdNull()
    {
        var draft = new CustomProviderDraft
        {
            Name = "我的网关",
            Protocol = TranslationProviderConfig.ProtocolOpenAi,
            Endpoint = "https://gw.corp/llm/v1",
            Model = "my-model",
        };

        var config = draft.ToConfig();

        Assert.Null(config.PresetId);                                // 自定义：PresetId 为 null
        Assert.Equal("我的网关", config.Provider);
        Assert.Equal("https://gw.corp/llm/v1", config.Endpoint);     // 路径逐字保留
        Assert.Equal("my-model", config.Model);
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, config.ApiProtocol);
        Assert.Null(config.ModelProtocols);
    }

    [Fact]
    public void ToConfig_DoesNotCollapseCustomPathToOrigin()
    {
        // 带自建路径的网关：绝不塌陷为 origin（会破坏 /api/paas/v4 这类版本段）。
        var draft = new CustomProviderDraft { Name = "gw", Endpoint = "https://gw.corp/api/paas/v4" };
        Assert.Equal("https://gw.corp/api/paas/v4", draft.ToConfig().Endpoint);
    }

    [Fact]
    public void ToConfig_Unnamed_UsesPlaceholder()
    {
        var draft = new CustomProviderDraft { Endpoint = "https://gw.corp/llm/v1" };
        Assert.Equal(CustomProviderDraft.UnnamedDisplayName, draft.DisplayName);
        Assert.Equal(CustomProviderDraft.UnnamedDisplayName, draft.ToConfig().Provider);
    }

    [Theory]
    [InlineData("anthropic", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("Anthropic", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("openai", TranslationProviderConfig.ProtocolOpenAi)]
    [InlineData("unknown", TranslationProviderConfig.ProtocolOpenAi)] // 未知 → openai（引擎缺省）
    [InlineData("", TranslationProviderConfig.ProtocolOpenAi)]
    public void ToConfig_NormalizesProtocol(string input, string expected)
    {
        var draft = new CustomProviderDraft { Name = "gw", Endpoint = "https://gw.corp/v1", Protocol = input };
        Assert.Equal(expected, draft.ToConfig().ApiProtocol);
    }

    [Fact]
    public void ProviderId_IsDerivedFromName_Lowercased()
    {
        var draft = new CustomProviderDraft { Name = "My-Gateway" };
        Assert.Equal("my-gateway", draft.ProviderId);
    }

    [Fact]
    public void FromConfig_RoundTripsSavedCustomConfig()
    {
        var saved = new TranslationProviderConfig
        {
            Provider = "My-Gateway",
            Endpoint = "https://gw.corp/llm/v1",
            Model = "my-model",
            PresetId = null,
            ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
        };

        var roundTrip = CustomProviderDraft.FromConfig(saved).ToConfig();

        Assert.Null(roundTrip.PresetId);
        Assert.Equal("My-Gateway", roundTrip.Provider);
        Assert.Equal("https://gw.corp/llm/v1", roundTrip.Endpoint);
        Assert.Equal("my-model", roundTrip.Model);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, roundTrip.ApiProtocol);
    }

    [Theory]
    [InlineData("team-chat-v3", true)]   // 手填且不在列表 → 提示
    [InlineData("team-chat-v2", false)]  // 命中列表 → 不提示
    [InlineData("", false)]              // 空模型 → 不提示
    public void ModelListHint_OnlyWhenManualModelMissingFromServerList(string model, bool expectHint)
    {
        string[] serverModels = ["team-chat-v2", "team-embed-1"];
        var hint = CustomProviderDraft.ModelListHint(model, serverModels);
        Assert.Equal(expectHint, hint is not null);
    }

    [Fact]
    public void ModelListHint_NullWhenNoServerList()
    {
        Assert.Null(CustomProviderDraft.ModelListHint("whatever", []));
        Assert.Null(CustomProviderDraft.ModelListHint("whatever", null));
    }

    [Fact]
    public void CustomEndpointWithPath_SurvivesStoreRoundTrip_AndRequestBaseKeepsPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "aipdf-v12t13-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            var draft = new CustomProviderDraft
            {
                Name = "我的网关",
                Protocol = TranslationProviderConfig.ProtocolOpenAi,
                Endpoint = "https://gw.corp/llm/v1",
                Model = "my-model",
            };

            store.Save(new AppSettings { TranslationProviders = [draft.ToConfig()] });
            var saved = Assert.Single(store.Load().TranslationProviders);

            Assert.Null(saved.PresetId);                             // 自定义：不被预设回填
            Assert.Equal("https://gw.corp/llm/v1", saved.Endpoint);  // 端点逐字保留

            // 请求基址保留路径（不塌陷为 origin）；安全 origin 仍只取 scheme://host（双归一化用途不同）。
            Assert.Equal("https://gw.corp/llm/v1", ProviderUrl.NormalizeRequestBase(saved.Endpoint, saved.ApiProtocol));
            Assert.Equal("https://gw.corp", ProviderUrl.SecurityOrigin(saved.Endpoint));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
