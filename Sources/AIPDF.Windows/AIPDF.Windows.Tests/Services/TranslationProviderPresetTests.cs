// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · 内置供应商预设表测试（T1；决策 #2/#11/#13/#14）：
//   - 14 家云端预设 id 唯一、非空、全小写；每家至少一个 base URL；
//   - DefaultProtocol 显式钉死：anthropic = Anthropic Claude / glm-coding-plan / kimi-code，其余 openai；
//   - opencode-go 按模型协议覆盖（仅 openai | anthropic 两值）；
//   - 编程套餐与订阅网关合规警示非空，其余为空；
//   - 本地服务稳定 id "local"，不进入云端预设表。
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationProviderPresetTests
{
    /// <summary>14 家云端内置预设的权威 id 列表（顺序即界面展示顺序）。</summary>
    private static readonly string[] ExpectedCloudIds =
    [
        "openai", "anthropic", "gemini", "deepseek", "qwen", "zhipu", "kimi", "groq",
        "minimax", "doubao", "opencode", "opencode-go", "glm-coding-plan", "kimi-code",
    ];

    /// <summary>DefaultProtocol = anthropic 的预设（其余一律 openai）。</summary>
    private static readonly string[] AnthropicProtocolIds = ["anthropic", "glm-coding-plan", "kimi-code"];

    /// <summary>须带非空合规 / 订阅警示的预设（编程套餐 + 订阅网关）。</summary>
    private static readonly string[] WarningIds = ["glm-coding-plan", "kimi-code", "opencode-go"];

    // ---------------------------------------------------------------- (a) 表完整性

    [Fact]
    public void All_ContainsExactlyFourteenCloudPresetsInStableOrder()
    {
        Assert.Equal(14, TranslationProviderPreset.All.Count);
        Assert.Equal(ExpectedCloudIds, TranslationProviderPreset.All.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void All_PresetIdsAreUniqueLowerCaseAndNonEmpty()
    {
        var ids = TranslationProviderPreset.All.Select(p => p.Id).ToArray();

        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id)); // 稳定 id 全小写
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------- (b) base URL 非空

    [Fact]
    public void All_CloudPresets_HaveAtLeastOneBaseUrl()
        => Assert.All(TranslationProviderPreset.All, preset => Assert.True(
            !string.IsNullOrWhiteSpace(preset.OpenAiBaseUrl) || !string.IsNullOrWhiteSpace(preset.AnthropicBaseUrl),
            $"预设 {preset.Id} 缺少 base URL"));

    [Fact]
    public void All_Presets_HaveDisplayNameAndModelList()
    {
        Assert.All(TranslationProviderPreset.All, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.DisplayName));
            Assert.NotNull(preset.DefaultModels);
        });
    }

    // ---------------------------------------------------------------- 协议钉死（T9 / T14 断言依据）

    [Theory]
    [InlineData("anthropic")]
    [InlineData("glm-coding-plan")]
    [InlineData("kimi-code")]
    public void DefaultProtocol_AnthropicPinnedPresets(string id)
        => Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, TranslationProviderPreset.Find(id)!.DefaultProtocol);

    [Fact]
    public void DefaultProtocol_AllOtherPresetsAreOpenAi()
        => Assert.All(
            TranslationProviderPreset.All.Where(p => !AnthropicProtocolIds.Contains(p.Id)),
            preset => Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, preset.DefaultProtocol));

    // ---------------------------------------------------------------- (d) opencode-go 按模型覆盖

    [Fact]
    public void OpenCodeGo_DefaultProtocolIsOpenAi_WithPerModelOverrides()
    {
        var preset = TranslationProviderPreset.Find("opencode-go")!;

        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, preset.DefaultProtocol);
        Assert.NotNull(preset.DefaultModelProtocols);
        Assert.True(preset.DefaultModelProtocols!.Count >= 3);
    }

    [Theory]
    [InlineData("minimax-m3", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("minimax-m2.7", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("minimax-m2.5", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("qwen3.8-max", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("qwen3.8-flash", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("qwen3.7-max", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("qwen3.7-plus", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("qwen3.6-plus", TranslationProviderConfig.ProtocolAnthropic)]
    [InlineData("kimi-k3", TranslationProviderConfig.ProtocolOpenAi)]
    [InlineData("deepseek-v4-pro", TranslationProviderConfig.ProtocolOpenAi)]
    [InlineData("grok-4.6", TranslationProviderConfig.ProtocolOpenAi)]
    public void OpenCodeGo_PerModelProtocols_ArePinned(string model, string expectedProtocol)
        => Assert.Equal(expectedProtocol, TranslationProviderPreset.Find("opencode-go")!.DefaultModelProtocols![model]);

    [Fact]
    public void DefaultModelProtocols_ValuesAreOnlyOpenAiOrAnthropic()
    {
        string[] allowed = [TranslationProviderConfig.ProtocolOpenAi, TranslationProviderConfig.ProtocolAnthropic];

        foreach (var preset in TranslationProviderPreset.All.Where(p => p.DefaultModelProtocols is not null))
        {
            Assert.All(preset.DefaultModelProtocols!.Values, value => Assert.Contains(value, allowed));
        }
    }

    [Fact]
    public void DefaultModelProtocols_OnlyOpenCodeGoIsPopulated()
        => Assert.All(
            TranslationProviderPreset.All.Where(p => p.Id != "opencode-go"),
            preset => Assert.Null(preset.DefaultModelProtocols));

    // ---------------------------------------------------------------- (c) 合规警示

    [Theory]
    [InlineData("glm-coding-plan")]
    [InlineData("kimi-code")]
    [InlineData("opencode-go")]
    public void CodingPlanAndSubscriptionPresets_HaveComplianceWarning(string id)
        => Assert.False(string.IsNullOrWhiteSpace(TranslationProviderPreset.Find(id)!.ComplianceWarning));

    [Fact]
    public void StandardCloudPresets_HaveNoComplianceWarning()
        => Assert.All(
            TranslationProviderPreset.All.Where(p => !WarningIds.Contains(p.Id)),
            preset => Assert.Null(preset.ComplianceWarning));

    // ---------------------------------------------------------------- 本地服务

    [Fact]
    public void Local_HasStableIdAndDisplayName_AndIsNotPartOfCloudTable()
    {
        var local = TranslationProviderPreset.Local;

        Assert.Equal(TranslationProviderConfig.LocalProviderName, local.Id); // "local"
        Assert.Equal("本地服务", local.DisplayName);
        Assert.Equal(TranslationProviderPresetKind.CloudStandard, local.Kind);
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, local.DefaultProtocol);
        Assert.Null(local.OpenAiBaseUrl);  // 端由用户填写（loopback 校验）
        Assert.Null(local.AnthropicBaseUrl);
        Assert.DoesNotContain(TranslationProviderPreset.All, p => p.Id == local.Id);
    }

    // ---------------------------------------------------------------- Find

    [Fact]
    public void Find_ResolvesCloudLocalCaseInsensitivelyAndUnknown()
    {
        Assert.Same(TranslationProviderPreset.All[0], TranslationProviderPreset.Find("openai"));
        Assert.Same(TranslationProviderPreset.Local, TranslationProviderPreset.Find("local"));
        Assert.Same(TranslationProviderPreset.Local, TranslationProviderPreset.Find("LOCAL")); // 查找便利：不区分大小写
        Assert.Null(TranslationProviderPreset.Find("no-such-provider")); // 自定义 / 未知 id
        Assert.Null(TranslationProviderPreset.Find(null));
        Assert.Null(TranslationProviderPreset.Find("   "));
    }
}
