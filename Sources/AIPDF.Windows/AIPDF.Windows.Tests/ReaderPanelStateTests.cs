// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 面板接管状态机测试：默认态、Ctrl+F 记住原面板、Esc/✕ 恢复、重复进入幂等。
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>ReaderPanelState 纯状态机测试（无 UI）。</summary>
public sealed class ReaderPanelStateTests
{
    [Fact]
    public void Default_IsThumbs()
    {
        var state = new ReaderPanelState();
        Assert.Equal(ReaderPanelState.Thumbs, state.ActiveKey);
        Assert.False(state.IsSearchActive);
    }

    [Fact]
    public void OpenSearch_FromOutline_RemembersAndRestores()
    {
        var state = new ReaderPanelState();
        state.Activate(ReaderPanelState.Outline);

        state.OpenSearch();
        Assert.True(state.IsSearchActive);
        Assert.Equal(ReaderPanelState.Outline, state.RestoreKey);

        Assert.True(state.ExitSearch());
        Assert.Equal(ReaderPanelState.Outline, state.ActiveKey);
        Assert.False(state.IsSearchActive);
    }

    [Fact]
    public void OpenSearch_Twice_KeepsOriginalRestoreTarget()
    {
        var state = new ReaderPanelState();
        state.Activate(ReaderPanelState.Bookmarks);

        state.OpenSearch();
        state.OpenSearch(); // 重复 Ctrl+F 幂等：不覆盖恢复键

        Assert.Equal(ReaderPanelState.Bookmarks, state.RestoreKey);
        state.ExitSearch();
        Assert.Equal(ReaderPanelState.Bookmarks, state.ActiveKey);
    }

    [Fact]
    public void ExitSearch_WhenNotSearching_ReturnsFalseAndDoesNotChange()
    {
        var state = new ReaderPanelState();
        state.Activate(ReaderPanelState.Outline);

        Assert.False(state.ExitSearch());
        Assert.Equal(ReaderPanelState.Outline, state.ActiveKey);
    }

    [Fact]
    public void ManualSwitchAwayFromSearch_ThenOpenSearch_RemembersNewPanel()
    {
        var state = new ReaderPanelState();
        state.Activate(ReaderPanelState.Outline);
        state.OpenSearch();

        state.Activate(ReaderPanelState.Thumbs); // 用户手动切走（未走 Esc）
        state.OpenSearch();

        Assert.Equal(ReaderPanelState.Thumbs, state.RestoreKey);
        state.ExitSearch();
        Assert.Equal(ReaderPanelState.Thumbs, state.ActiveKey);
    }

    [Fact]
    public void ExitSearch_RestoresOnlyOnce()
    {
        var state = new ReaderPanelState();
        state.Activate(ReaderPanelState.Outline);
        state.OpenSearch();

        Assert.True(state.ExitSearch());
        Assert.False(state.ExitSearch()); // 第二次无搜索可退出
        Assert.Equal(ReaderPanelState.Outline, state.ActiveKey);
    }
}
