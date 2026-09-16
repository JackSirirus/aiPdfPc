// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T10 划词只读跟随单测：
//   - 共享选择变化 → 划词面板标签与已有 / 新增卡片同步（只读，无独立选择入口）；
//   - 反射断言划词 VM 不暴露任何供应商选择 / 切换属性或方法。
using System.ComponentModel;
using System.Reflection;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.ViewModels.Translation;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public class TranslationProviderFollowTests
{
    /// <summary>最小共享选择替身：模拟共享 VM（T9 TranslationConfigViewModel）的选中供应商变化。</summary>
    private sealed class SharedSelectionStub : INotifyPropertyChanged
    {
        public string ProviderId { get; private set; } = "openai";

        public string DisplayName { get; private set; } = "OpenAI";

        public string Model { get; private set; } = "gpt-5";

        public string Protocol { get; private set; } = "openai";

        public void Select(string providerId, string displayName, string model, string protocol)
        {
            ProviderId = providerId;
            DisplayName = displayName;
            Model = model;
            Protocol = protocol;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProviderId)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static (SharedSelectionStub Shared, TranslationProviderReadout Readout, SelectionTranslationPanelViewModel Panel)
        NewFollowSetup()
    {
        var shared = new SharedSelectionStub();
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot(shared.ProviderId, shared.DisplayName, shared.Model, shared.Protocol),
            shared);
        var panel = new SelectionTranslationPanelViewModel();
        panel.FollowProvider(readout);
        readout.Refresh();
        return (shared, readout, panel);
    }

    [Fact]
    public void Panel_FollowsSharedProvider_AndNewCardUsesIt()
    {
        var (_, _, panel) = NewFollowSetup();

        Assert.True(panel.IsFollowingProvider);
        Assert.Equal("OpenAI", panel.ProviderLabel);
        Assert.Equal("OpenAI 兼容", panel.ProviderProtocolBadge);
        Assert.True(panel.HasProviderLabel);

        var card = panel.AddCardForCurrentProvider("Hello", "en", "zh", "generic");

        Assert.Equal("OpenAI", card.EngineLabel);
        Assert.Equal("OpenAI 兼容", card.ProtocolBadge);
        Assert.True(card.ShowProtocolBadge);
        Assert.Contains("OpenAI", card.RouteLabel);
    }

    [Fact]
    public void SwitchingSharedProvider_SyncsPanelAndExistingCards()
    {
        var (shared, _, panel) = NewFollowSetup();
        var card = panel.AddCardForCurrentProvider("Hello", "en", "zh", "generic");
        Assert.Equal("OpenAI", card.EngineLabel);

        // QA 场景：共享 VM.SelectedProvider = "kimi-code" → 划词标签同步为「Kimi Code」。
        shared.Select("kimi-code", "Kimi Code", "kimi-k3", "anthropic");

        Assert.Equal("Kimi Code", panel.ProviderLabel);
        Assert.Equal("Anthropic 兼容", panel.ProviderProtocolBadge);
        Assert.Equal("Kimi Code", card.EngineLabel);
        Assert.Equal("Anthropic 兼容", card.ProtocolBadge);
        Assert.Contains("Kimi Code", card.RouteLabel);
    }

    [Fact]
    public void NewCard_AfterSwitch_UsesLatestProvider()
    {
        var (shared, _, panel) = NewFollowSetup();
        _ = panel.AddCardForCurrentProvider("first", "en", "zh", "generic");

        shared.Select("local", "本地服务", "qwen2.5:7b", "openai");
        var second = panel.AddCardForCurrentProvider("second", "en", "zh", "generic");

        Assert.Equal("本地服务", second.EngineLabel);
        Assert.Equal("本地服务", panel.Cards[0].EngineLabel);
        Assert.Equal("本地服务", panel.Cards[1].EngineLabel);
    }

    [Fact]
    public void Unconfigured_ReadoutShowsUnconfiguredLabel_AndNoBadge()
    {
        var readout = new TranslationProviderReadout(() => new TranslationProviderSnapshot("", "", "", ""));
        readout.Refresh();

        Assert.False(readout.HasProvider);
        Assert.Equal("未配置", readout.DisplayLabel);
        Assert.Equal("未配置", readout.SummaryLabel);
        Assert.False(readout.HasProtocolBadge);
        Assert.Equal("", readout.ProtocolBadge);
    }

    [Fact]
    public void Readout_ResolvesPresetDisplayName_FromStableId()
    {
        // 共享 VM 只暴露稳定 id 时，显示名回退预设表（T1）。
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot("kimi-code", "", "kimi-k3", "anthropic"));
        readout.Refresh();

        Assert.Equal("Kimi Code", readout.DisplayLabel);
        Assert.Equal("Kimi Code · kimi-k3", readout.SummaryLabel);
        Assert.True(readout.IsAnthropic);
        Assert.False(readout.IsLocal);
    }

    [Fact]
    public void Readout_LocalService_UsesStableId_AndKeepsModelInSummary()
    {
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot("local", "本地服务", "qwen2.5:7b", "openai"));
        readout.Refresh();

        Assert.True(readout.IsLocal);
        Assert.Equal("本地服务 · qwen2.5:7b", readout.SummaryLabel);
        Assert.Equal(TranslationProviderReadout.OpenAiBadge, readout.ProtocolBadge);
        Assert.True(readout.HasProtocolBadge);
    }

    [Fact]
    public void Readout_UnknownProtocol_HasNoBadge()
    {
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot("custom-gw", "公司网关", "m1", ""));
        readout.Refresh();

        Assert.Equal("公司网关", readout.DisplayLabel);
        Assert.Equal("", readout.ProtocolBadge);
        Assert.False(readout.HasProtocolBadge);
    }

    [Fact]
    public void Panel_FollowsRealSharedConfigViewModel_WhenProviderSwitches()
    {
        // 真实共享 VM（T9）：划词读卡只读跟随——切换 SelectedProvider → 标签 / 徽章同步。
        var config = new TranslationConfigViewModel();
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot(
                config.SelectedProvider, config.ProviderDisplayName, config.SelectedModel, config.Protocol),
            config);
        var panel = new SelectionTranslationPanelViewModel();
        panel.FollowProvider(readout);
        readout.Refresh();

        Assert.Equal("本地服务", panel.ProviderLabel);
        var card = panel.AddCardForCurrentProvider("Hello", "en", "zh", "generic");
        Assert.Equal("本地服务", card.EngineLabel);

        config.SelectedProvider = "kimi-code";

        Assert.Equal("Kimi Code", panel.ProviderLabel);
        Assert.Equal(TranslationProviderReadout.AnthropicBadge, panel.ProviderProtocolBadge);
        Assert.Equal("Kimi Code", card.EngineLabel);
        Assert.Equal(TranslationProviderReadout.AnthropicBadge, card.ProtocolBadge);

        // 摘要 = 显示名 · 当前模型（模型取预设默认，避免与 T1 数据耦合出脆弱断言）。
        var preset = TranslationProviderPreset.Find("kimi-code")!;
        Assert.Equal($"Kimi Code · {preset.DefaultModels[0]}", readout.SummaryLabel);
    }

    [Fact]
    public void SelectionVms_ExposeNoIndependentProviderSelectionSurface()
    {
        // 划词只读：既没有可写的选中供应商，也没有供应商列表 / 选择方法。
        var forbidden = new[]
        {
            "SelectedProvider", "SelectedProviderId", "AvailableProviders", "ProviderOptions",
            "SelectProvider", "ChangeProvider", "SwitchProvider", "SetProvider",
        };

        foreach (var type in new[] { typeof(SelectionTranslationPanelViewModel), typeof(SelectionTranslationCardViewModel) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(property.Name, forbidden);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain(method.Name, forbidden);
            }
        }

        // 供应商展示面必须存在且只读（无公开 setter）。
        var readOnlySurface = new (Type Type, string Name)[]
        {
            (typeof(SelectionTranslationPanelViewModel), nameof(SelectionTranslationPanelViewModel.ProviderLabel)),
            (typeof(SelectionTranslationPanelViewModel), nameof(SelectionTranslationPanelViewModel.ProviderProtocolBadge)),
            (typeof(SelectionTranslationCardViewModel), nameof(SelectionTranslationCardViewModel.EngineLabel)),
            (typeof(SelectionTranslationCardViewModel), nameof(SelectionTranslationCardViewModel.ProtocolBadge)),
        };

        foreach (var (type, name) in readOnlySurface)
        {
            var property = type.GetProperty(name);
            Assert.NotNull(property);
            Assert.True(
                property!.SetMethod is null || !property.SetMethod.IsPublic,
                $"{type.Name}.{name} 不得有公开 setter");
        }
    }
}
