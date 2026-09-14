// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词面板纯逻辑单测：卡片堆叠上限 20 / 新卡置顶 / 无文本层态 / 卡片状态机 / 标签映射。
using AIPDF.Windows.ViewModels.Translation;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public class SelectionTranslationViewModelTests
{
    private static SelectionTranslationPanelViewModel NewPanel() => new();

    [Fact]
    public void NewPanel_ShowsEmptyHint_AndNoNoTextLayer()
    {
        var panel = NewPanel();

        Assert.Empty(panel.Cards);
        Assert.False(panel.HasCards);
        Assert.False(panel.IsNoTextLayer);
        Assert.True(panel.ShowEmptyHint);
    }

    [Fact]
    public void AddCard_InsertsNewestFirst_AndHidesEmptyHint()
    {
        var panel = NewPanel();
        panel.AddCard("first", "en", "zh", "generic", "本地");
        panel.AddCard("second", "en", "zh", "academic", "本地");

        Assert.Equal(2, panel.Cards.Count);
        Assert.Equal("second", panel.Cards[0].SourceText);
        Assert.Equal("first", panel.Cards[1].SourceText);
        Assert.True(panel.HasCards);
        Assert.False(panel.ShowEmptyHint);
    }

    [Fact]
    public void AddCard_TrimsToTwenty_DroppingOldest()
    {
        var panel = NewPanel();
        for (var i = 0; i < SelectionTranslationPanelViewModel.MaxCards + 5; i++)
        {
            panel.AddCard($"text-{i}", "en", "zh", "generic", "本地");
        }

        Assert.Equal(SelectionTranslationPanelViewModel.MaxCards, panel.Cards.Count);
        // 最新的 20 条保留：索引 0 为最后加入，最旧的是 text-5（text-0..4 被丢弃）。
        Assert.Equal("text-24", panel.Cards[0].SourceText);
        Assert.Equal("text-5", panel.Cards[^1].SourceText);
        Assert.DoesNotContain(panel.Cards, c => c.SourceText == "text-0");
    }

    [Fact]
    public void EnterNoTextLayer_HidesEmptyHint_AddCardExitsState()
    {
        var panel = NewPanel();
        panel.EnterNoTextLayer();

        Assert.True(panel.IsNoTextLayer);
        Assert.False(panel.ShowEmptyHint);

        panel.AddCard("hello", "en", "zh", "generic", "本地");
        Assert.False(panel.IsNoTextLayer);
        Assert.False(panel.ShowEmptyHint);
    }

    [Fact]
    public void Clear_RemovesCardsAndRestoresEmptyHint()
    {
        var panel = NewPanel();
        panel.AddCard("hello", "en", "zh", "generic", "本地");
        panel.Clear();

        Assert.Empty(panel.Cards);
        Assert.True(panel.ShowEmptyHint);
    }

    [Fact]
    public void Card_StartsLoading_ThenCompletes()
    {
        var card = new SelectionTranslationCardViewModel("Hello world", "en", "zh", "academic", "云端");
        Assert.Equal(SelectionTranslationState.Loading, card.State);
        Assert.True(card.IsLoading);
        Assert.False(card.IsRetranslatable);

        card.Complete("你好，世界", 1.234);

        Assert.Equal(SelectionTranslationState.Done, card.State);
        Assert.True(card.IsDone);
        Assert.True(card.IsRetranslatable);
        Assert.Equal("你好，世界", card.Translation);
        Assert.Equal("1.2s", card.ElapsedLabel);
        Assert.Equal("", card.ErrorMessage);
    }

    [Fact]
    public void Card_FailExposesReadableError_AndReTranslateResets()
    {
        var card = new SelectionTranslationCardViewModel("Hello", "en", "zh", "generic", "未配置");
        card.Fail("翻译服务尚未配置。");

        Assert.Equal(SelectionTranslationState.Error, card.State);
        Assert.True(card.IsError);
        Assert.Equal("翻译服务尚未配置。", card.ErrorMessage);
        Assert.Equal("", card.Translation);

        card.BeginLoading();
        Assert.Equal(SelectionTranslationState.Loading, card.State);
        Assert.Equal("", card.ErrorMessage);
    }

    [Theory]
    [InlineData("academic", "学术")]
    [InlineData("legal", "法律")]
    [InlineData("medical", "医学")]
    [InlineData("technical", "技术")]
    [InlineData("generic", "通用")]
    [InlineData("", "通用")]
    public void TemplateLabels_MapPresetCodes(string code, string expected)
        => Assert.Equal(expected, TranslationLabels.Template(code));

    [Theory]
    [InlineData("zh", "中文")]
    [InlineData("en", "英语")]
    [InlineData("auto", "自动检测")]
    public void LanguageLabels_MapCodes(string code, string expected)
        => Assert.Equal(expected, TranslationLabels.Language(code));

    [Fact]
    public void CountWords_UsesWordsForSpacedText_AndCharsForCjk()
    {
        Assert.Equal(3, TranslationLabels.CountWords("one two three"));
        Assert.Equal(4, TranslationLabels.CountWords("你好世界"));
        Assert.Equal(0, TranslationLabels.CountWords("   "));
    }

    [Fact]
    public void Card_MetaAndRoute_AreHumanReadable()
    {
        var card = new SelectionTranslationCardViewModel("Hello world", "en", "zh", "academic", "云端");

        Assert.Equal("英语 · 2 字", card.SourceMeta);
        Assert.Equal("学术 · 英语→中文 · 云端", card.RouteLabel);
    }
}
