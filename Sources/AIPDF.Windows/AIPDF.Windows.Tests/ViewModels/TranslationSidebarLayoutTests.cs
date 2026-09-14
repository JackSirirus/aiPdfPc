// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T12 翻译右栏响应式判定测试：680 / 480 阈值 + 非文档标签隐藏。
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

/// <summary>TranslationSidebarLayout 纯逻辑测试（无 UI）。</summary>
public sealed class TranslationSidebarLayoutTests
{
    [Theory]
    [InlineData(1080, TranslationSidebarMode.Docked)]
    [InlineData(680, TranslationSidebarMode.Docked)] // 阈值含等号：≥680 停靠
    [InlineData(679, TranslationSidebarMode.Overlay)]
    [InlineData(480, TranslationSidebarMode.Overlay)] // 阈值含等号：480–680 覆盖
    [InlineData(479, TranslationSidebarMode.Collapsed)]
    [InlineData(320, TranslationSidebarMode.Collapsed)]
    public void Resolve_DocumentTab_MapsWidthToMode(double width, TranslationSidebarMode expected)
        => Assert.Equal(expected, TranslationSidebarLayout.Resolve(width, documentTabActive: true));

    [Theory]
    [InlineData(1080)]
    [InlineData(480)]
    [InlineData(320)]
    public void Resolve_NonDocumentTab_AlwaysHidden(double width)
        => Assert.Equal(TranslationSidebarMode.Hidden, TranslationSidebarLayout.Resolve(width, documentTabActive: false));

    [Fact]
    public void Thresholds_MatchDesignBaseline()
    {
        Assert.Equal(680d, TranslationSidebarLayout.OverlayThreshold);
        Assert.Equal(480d, TranslationSidebarLayout.CollapseThreshold);
    }
}
