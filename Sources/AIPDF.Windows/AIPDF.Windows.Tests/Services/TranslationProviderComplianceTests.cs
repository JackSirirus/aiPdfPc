// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T14 编程套餐合规 + 协议选择测试（纯逻辑，无 WPF 实例化）：
//   - 三家订阅 / 套餐项常驻警示非空、其余为空；文案取自 T1 预设表；
//   - 「改用标准 API」切到同族标准预设（GLM → zhipu / Kimi → kimi），端点与协议正确，
//     且不触碰凭据 / 同意（本层无从触碰）；
//   - 协议展示与 opencode-go 按模型徽章（混合协议）；
//   - 已下线 Kimi 模型 id 拦截；失败态分类覆盖 1113 / 400,404 / 401 / 下线。
using System.Collections.Generic;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationProviderComplianceTests
{
    // ---------------------------------------------------------------- 协议展示

    [Theory]
    [InlineData("anthropic", TranslationProviderCompliance.AnthropicLabel)]
    [InlineData("Anthropic", TranslationProviderCompliance.AnthropicLabel)]
    [InlineData("openai", TranslationProviderCompliance.OpenAiLabel)]
    [InlineData("", TranslationProviderCompliance.OpenAiLabel)]
    [InlineData(null, TranslationProviderCompliance.OpenAiLabel)]
    [InlineData("unknown", TranslationProviderCompliance.OpenAiLabel)]
    public void ProtocolLabel_NormalizesAndLabels(string? protocol, string expected)
    {
        Assert.Equal(expected, TranslationProviderCompliance.ProtocolLabel(protocol));
    }

    [Theory]
    [InlineData("anthropic", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("ANTHROPIC", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("openai", TranslationProviderConfig.ProtocolOpenAi)]
    [InlineData("", TranslationProviderConfig.ProtocolOpenAi)]
    [InlineData(null, TranslationProviderConfig.ProtocolOpenAi)]
    public void NormalizeProtocol_FallsBackToOpenAi(string? value, string expected)
    {
        Assert.Equal(expected, TranslationProviderCompliance.NormalizeProtocol(value));
    }

    [Fact]
    public void ProtocolDisplay_MixedProviderShowsMixedLabel()
    {
        Assert.Equal(TranslationProviderCompliance.MixedLabel, TranslationProviderCompliance.ProtocolDisplay("opencode-go"));
        Assert.Equal(TranslationProviderCompliance.AnthropicLabel, TranslationProviderCompliance.ProtocolDisplay("kimi-code"));
        Assert.Equal(TranslationProviderCompliance.OpenAiLabel, TranslationProviderCompliance.ProtocolDisplay("openai"));
        Assert.True(TranslationProviderCompliance.IsMixedProtocol("opencode-go"));
        Assert.False(TranslationProviderCompliance.IsMixedProtocol("glm-coding-plan"));
    }

    // ---------------------------------------------------------------- 合规警示

    [Theory]
    [InlineData("glm-coding-plan")]
    [InlineData("kimi-code")]
    [InlineData("opencode-go")]
    public void RequiresComplianceNotice_ForPackageAndSubscription(string presetId)
    {
        Assert.True(TranslationProviderCompliance.RequiresComplianceNotice(presetId));
        Assert.False(string.IsNullOrWhiteSpace(TranslationProviderCompliance.ComplianceNotice(presetId)));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("zhipu")]
    [InlineData("kimi")]
    [InlineData("local")]
    [InlineData("custom-gateway")]
    public void RequiresComplianceNotice_ForStandardAndCustom(string presetId)
    {
        Assert.False(TranslationProviderCompliance.RequiresComplianceNotice(presetId));
        Assert.Null(TranslationProviderCompliance.ComplianceNotice(presetId));
    }

    [Fact]
    public void CodingPlanPresets_AreSeparateFromStandardDefault()
    {
        // 标准 API 为默认：编程套餐与标准 API 是独立预设，套餐端点须显式选择（P37）。
        var codingPlanIds = TranslationProviderPreset.All
            .Where(p => p.Kind == TranslationProviderPresetKind.CodingPlan)
            .Select(p => p.Id)
            .ToArray();

        Assert.Equal(["glm-coding-plan", "kimi-code"], codingPlanIds);

        foreach (var id in codingPlanIds)
        {
            var standard = TranslationProviderCompliance.StandardPresetFor(id);
            Assert.NotNull(standard);
            Assert.Equal(TranslationProviderPresetKind.CloudStandard, standard!.Kind);
            Assert.NotEqual(id, standard.Id);
        }

        // 左栏首位（本地服务）不是编程套餐：列表默认不会落在套餐端点上。
        Assert.False(TranslationProviderCompliance.IsCodingPlan(TranslationProviderPreset.Local.Id));
    }

    [Fact]
    public void ComplianceNotice_GlmMentionsQuotaAndStandardApi()
    {
        var notice = TranslationProviderCompliance.ComplianceNotice("glm-coding-plan")!;
        Assert.Contains("编程套餐", notice);
        Assert.Contains("1113", notice);
        Assert.Contains("标准 API", notice);
    }

    [Fact]
    public void IsCodingPlan_OnlyPackagePresets()
    {
        Assert.True(TranslationProviderCompliance.IsCodingPlan("glm-coding-plan"));
        Assert.True(TranslationProviderCompliance.IsCodingPlan("kimi-code"));
        Assert.False(TranslationProviderCompliance.IsCodingPlan("opencode-go"));
        Assert.False(TranslationProviderCompliance.IsCodingPlan("zhipu"));
    }

    // ---------------------------------------------------------------- 改用标准 API

    [Fact]
    public void StandardPresetFor_MapsCodingPlanToStandardFamily()
    {
        var glm = TranslationProviderCompliance.StandardPresetFor("glm-coding-plan");
        Assert.NotNull(glm);
        Assert.Equal("zhipu", glm!.Id);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", glm.OpenAiBaseUrl);

        var kimi = TranslationProviderCompliance.StandardPresetFor("kimi-code");
        Assert.NotNull(kimi);
        Assert.Equal("kimi", kimi!.Id);
        Assert.Equal("https://api.moonshot.cn/v1", kimi.OpenAiBaseUrl);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("opencode-go")]
    [InlineData("zhipu")]
    [InlineData("local")]
    [InlineData("custom")]
    [InlineData(null)]
    public void StandardPresetFor_NonCodingPlanReturnsNull(string? presetId)
    {
        Assert.Null(TranslationProviderCompliance.StandardPresetFor(presetId));
        Assert.Null(TranslationProviderCompliance.StandardEndpointFor(presetId));
    }

    [Fact]
    public void TrySwitchToStandardApi_GlmCodingPlan_SwitchesEndpointAndProtocol()
    {
        var config = new TranslationProviderConfig
        {
            Provider = "GLM Coding Plan",
            Endpoint = "https://open.bigmodel.cn/api/anthropic",
            Model = "glm-5.3",
            PresetId = "glm-coding-plan",
            ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
        };

        var switched = TranslationProviderCompliance.TrySwitchToStandardApi(config);

        Assert.True(switched);
        Assert.Equal("zhipu", config.PresetId);
        Assert.Equal("智谱 GLM", config.Provider);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", config.Endpoint);
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, config.ApiProtocol);
        Assert.Null(config.ModelProtocols);
        Assert.Equal("glm-5.3", config.Model); // 模型名保留，用户可再改选
    }

    [Fact]
    public void TrySwitchToStandardApi_KimiCode_SwitchesToMoonshotStandard()
    {
        var config = new TranslationProviderConfig
        {
            Provider = "Kimi Code",
            Endpoint = "https://api.kimi.com/coding/v1",
            Model = "k3",
            PresetId = "kimi-code",
            ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
        };

        Assert.True(TranslationProviderCompliance.TrySwitchToStandardApi(config));
        Assert.Equal("kimi", config.PresetId);
        Assert.Equal("https://api.moonshot.cn/v1", config.Endpoint);
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, config.ApiProtocol);
    }

    [Fact]
    public void TrySwitchToStandardApi_NonCodingPlan_ReturnsFalseAndLeavesConfigUntouched()
    {
        var config = new TranslationProviderConfig
        {
            Provider = "OpenAI",
            Endpoint = "https://api.openai.com/v1",
            Model = "gpt-4o",
            PresetId = "openai",
            ApiProtocol = TranslationProviderConfig.ProtocolOpenAi,
        };

        Assert.False(TranslationProviderCompliance.TrySwitchToStandardApi(config));
        Assert.Equal("openai", config.PresetId);
        Assert.Equal("https://api.openai.com/v1", config.Endpoint);
        Assert.Equal("gpt-4o", config.Model);
    }

    [Fact]
    public void Switch_OnlyChangesConnectionMetadata_NotCredentials()
    {
        // 切换只改连接元数据（presetId / provider 名 / endpoint / 协议），不触碰凭据与同意。
        // 凭据目标名 = 稳定 id + origin 哈希：GLM 套餐与标准 API 同 host（origin 相同）但 provider id
        // 不同（glm-coding-plan → zhipu），因此目标名不同，旧凭据不会被误用。
        var config = new TranslationProviderConfig
        {
            Provider = "GLM Coding Plan",
            Endpoint = "https://open.bigmodel.cn/api/coding/paas/v4",
            PresetId = "glm-coding-plan",
        };

        var beforeIdentity = TranslationProviderIdentity.For(config);
        var beforeEndpoint = config.Endpoint;

        Assert.True(TranslationProviderCompliance.TrySwitchToStandardApi(config));

        var afterIdentity = TranslationProviderIdentity.For(config);
        Assert.NotEqual(beforeIdentity, afterIdentity);      // 稳定 id 改变 → 凭据目标名不同
        Assert.NotEqual(beforeEndpoint, config.Endpoint);    // 端点确实切换到标准 API
        Assert.False(string.IsNullOrWhiteSpace(TranslationProviderCompliance.StandardApiCredentialNotice));
    }

    // ---------------------------------------------------------------- 按模型协议徽章

    [Theory]
    [InlineData("minimax-m3", "Anthropic")]
    [InlineData("qwen3.8-max", "Anthropic")]
    [InlineData("kimi-k3", "OpenAI")]
    [InlineData("deepseek-v4-pro", "OpenAI")]
    [InlineData("grok-4.6", "OpenAI")]
    [InlineData("glm-5.3", "OpenAI")] // 未在覆盖表中 → 回退 provider 默认 openai
    public void ModelProtocolBadge_OpencodeGo_ByModel(string model, string expected)
    {
        Assert.Equal(expected, TranslationProviderCompliance.ModelProtocolBadge("opencode-go", model));
    }

    [Fact]
    public void ModelProtocolBadge_NonMixedProvider_HasNoBadge()
    {
        Assert.Null(TranslationProviderCompliance.ModelProtocolBadge("openai", "gpt-4o"));
        Assert.Null(TranslationProviderCompliance.ModelProtocolBadge("glm-coding-plan", "glm-5.3"));
        Assert.Null(TranslationProviderCompliance.ModelProtocolBadge(null, "custom-model"));
        Assert.Null(TranslationProviderCompliance.ModelProtocolBadge("opencode-go", ""));
    }

    [Fact]
    public void ModelProtocolBadge_ExplicitOverrideWins()
    {
        var overrides = new Dictionary<string, string>
        {
            ["kimi-k3"] = TranslationProviderConfig.ProtocolAnthropic,
        };

        Assert.Equal("Anthropic", TranslationProviderCompliance.ModelProtocolBadge("opencode-go", "kimi-k3", overrides));
        Assert.Equal(
            TranslationProviderConfig.ProtocolAnthropic,
            TranslationProviderCompliance.ModelProtocol("opencode-go", "kimi-k3", overrides));
    }

    [Fact]
    public void ModelProtocol_CustomPresetReturnsNull()
    {
        Assert.Null(TranslationProviderCompliance.ModelProtocol("custom", "my-model"));
        Assert.Null(TranslationProviderCompliance.ModelProtocol("openai", ""));
    }

    // ---------------------------------------------------------------- 已下线模型

    [Theory]
    [InlineData("kimi-k2-0905-preview")]
    [InlineData("kimi-k2-turbo-preview")]
    [InlineData("kimi-latest")]
    [InlineData("kimi-k2.5")]
    [InlineData("KIMI-LATEST")]
    [InlineData("moonshot-v1-8k")]
    [InlineData("moonshot-v1-128k")]
    public void IsRetiredModel_MatchesRetiredIds(string model)
    {
        Assert.True(TranslationProviderCompliance.IsRetiredModel(model));
    }

    [Theory]
    [InlineData("k3")]
    [InlineData("kimi-for-coding")]
    [InlineData("kimi-k3")]
    [InlineData("kimi-k2.7-code")]
    [InlineData("gpt-4o")]
    [InlineData("")]
    [InlineData(null)]
    public void IsRetiredModel_CurrentIdsAreAccepted(string? model)
    {
        Assert.False(TranslationProviderCompliance.IsRetiredModel(model));
    }

    [Fact]
    public void RetiredModel_HasNoProtocolBadge()
    {
        Assert.Null(TranslationProviderCompliance.ModelProtocolBadge("kimi", "moonshot-v1-8k"));
    }

    // ---------------------------------------------------------------- 失败态分类

    [Fact]
    public void ClassifyFailure_QuotaExceeded_On1113()
    {
        var kind = TranslationProviderCompliance.ClassifyFailure(200, "1113 Insufficient Balance", "glm-5.3");
        Assert.Equal(TranslationProviderFailureKind.QuotaExceeded, kind);
        Assert.Equal(TranslationProviderCompliance.QuotaExceededBanner, TranslationProviderCompliance.FailureBanner(kind));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(405)]
    public void ClassifyFailure_ProtocolMismatch(int status)
    {
        Assert.Equal(
            TranslationProviderFailureKind.ProtocolMismatch,
            TranslationProviderCompliance.ClassifyFailure(status, "not found", "claude-sonnet-4-20250514"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ClassifyFailure_CredentialMismatch(int status)
    {
        Assert.Equal(
            TranslationProviderFailureKind.CredentialMismatch,
            TranslationProviderCompliance.ClassifyFailure(status, "unauthorized", "k3"));

        // 401 / 403 共用同一横幅（Task 13 / P3-B）：文案须同时覆盖两个状态码，
        // 403 路径不得显示「401：」纯 401 误导前缀，也不得把拒绝归咎于密钥本身（无「检查 API Key」）。
        var banner = TranslationProviderCompliance.FailureBanner(
            TranslationProviderFailureKind.CredentialMismatch)!;
        Assert.Contains("401 / 403", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("401：", banner, StringComparison.Ordinal);
        Assert.DoesNotContain("检查 API Key", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyFailure_RetiredModel_TakesPrecedenceOver404()
    {
        Assert.Equal(
            TranslationProviderFailureKind.RetiredModel,
            TranslationProviderCompliance.ClassifyFailure(404, "model not found", "kimi-latest"));
    }

    [Fact]
    public void ClassifyFailure_UnknownIsNone()
    {
        Assert.Equal(
            TranslationProviderFailureKind.None,
            TranslationProviderCompliance.ClassifyFailure(500, "boom", "gpt-4o"));
        Assert.Null(TranslationProviderCompliance.FailureBanner(TranslationProviderFailureKind.None));
    }

    [Fact]
    public void FailureBanners_AreNonEmptyAndActionable()
    {
        foreach (var kind in new[]
                 {
                     TranslationProviderFailureKind.QuotaExceeded,
                     TranslationProviderFailureKind.ProtocolMismatch,
                     TranslationProviderFailureKind.CredentialMismatch,
                     TranslationProviderFailureKind.RetiredModel,
                 })
        {
            var banner = TranslationProviderCompliance.FailureBanner(kind);
            Assert.False(string.IsNullOrWhiteSpace(banner));
        }

        Assert.Contains("标准 API", TranslationProviderCompliance.FailureBanner(
            TranslationProviderFailureKind.QuotaExceeded)!);
        Assert.Contains("/v1/messages", TranslationProviderCompliance.FailureBanner(
            TranslationProviderFailureKind.ProtocolMismatch)!);
        Assert.Contains("kimi-k3", TranslationProviderCompliance.FailureBanner(
            TranslationProviderFailureKind.RetiredModel)!);
    }
}
