// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T12 快捷键注册表测试：动作清单与默认手势、手势规范化（拒绝裸键/非法语法）、
// 覆盖解析（非法回退默认、未知动作丢弃）、冲突检测、设置持久化的规范化。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

/// <summary>可自定义快捷键（V4 T12）。</summary>
public sealed class ShortcutRegistryTests
{
    [Fact]
    public void Actions_CoverAllSixCustomizableShortcuts_WithUniqueIdsAndGestures()
    {
        // 口径守卫：可自定义的是 5 个 KeyBinding（Ctrl+F/W/P/B/K）+ 1 个代码分支（Ctrl+G）。
        // 这个数量若因新增快捷键而变化，必须同步更新计划与设置页——故在此锁死。
        Assert.Equal(6, ShortcutRegistry.Actions.Count);
        Assert.Equal(6, ShortcutRegistry.Actions.Select(action => action.Id).Distinct().Count());
        Assert.Equal(6, ShortcutRegistry.Actions.Select(action => action.DefaultGesture).Distinct().Count());

        Assert.Equal("Ctrl+F", ShortcutRegistry.DefaultFor(ShortcutRegistry.FullTextSearch));
        Assert.Equal("Ctrl+W", ShortcutRegistry.DefaultFor(ShortcutRegistry.CloseTab));
        Assert.Equal("Ctrl+P", ShortcutRegistry.DefaultFor(ShortcutRegistry.Print));
        Assert.Equal("Ctrl+B", ShortcutRegistry.DefaultFor(ShortcutRegistry.AddBookmark));
        Assert.Equal("Ctrl+K", ShortcutRegistry.DefaultFor(ShortcutRegistry.CommandPalette));
        Assert.Equal("Ctrl+G", ShortcutRegistry.DefaultFor(ShortcutRegistry.GoToPage));

        // 每个动作都要有展示名（冲突文案与设置页依赖它）
        Assert.All(ShortcutRegistry.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Label)));

        // 默认手势之间**不得**互相冲突
        Assert.Empty(ShortcutRegistry.FindConflicts(ShortcutRegistry.Defaults));
    }

    [Theory]
    [InlineData("ctrl+f", "Ctrl+F")]
    [InlineData("CTRL+SHIFT+P", "Ctrl+Shift+P")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("F5", "F5")]            // 功能键允许无修饰键
    [InlineData("Ctrl+Alt+Delete", "Ctrl+Alt+Delete")]
    public void NormalizeGesture_AcceptsValidGestures(string input, string expected)
        => Assert.Equal(expected, ShortcutRegistry.NormalizeGesture(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("F")]              // 裸字母键会抢输入，必须拒绝
    [InlineData("Ctrl")]           // 只有修饰键
    [InlineData("Ctrl+滚轮")]       // 鼠标手势不是 KeyBinding
    [InlineData("Esc")]            // 裸 Esc 是弹层收起逻辑，不作为可绑定快捷键
    [InlineData("Ctrl+NotAKey")]
    public void NormalizeGesture_RejectsInvalidOrBareKeys(string input)
        => Assert.Null(ShortcutRegistry.NormalizeGesture(input));

    [Fact]
    public void Resolve_AppliesOverrides_AndFallsBackToDefault()
    {
        var effective = ShortcutRegistry.Resolve(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ShortcutRegistry.Print] = "ctrl+shift+p",
            [ShortcutRegistry.CloseTab] = "F",          // 非法 → 回退默认
            ["no-such-action"] = "Ctrl+Q",              // 未知动作 → 丢弃
        });

        Assert.Equal("Ctrl+Shift+P", effective[ShortcutRegistry.Print]);
        Assert.Equal("Ctrl+W", effective[ShortcutRegistry.CloseTab]);   // 回退默认，不丢快捷键
        Assert.Equal(ShortcutRegistry.Actions.Count, effective.Count);
        Assert.DoesNotContain("no-such-action", effective.Keys);
    }

    [Fact]
    public void Resolve_WithoutOverrides_ReturnsDefaults()
    {
        var effective = ShortcutRegistry.Resolve(null);

        Assert.Equal(ShortcutRegistry.Defaults.Count, effective.Count);
        Assert.All(ShortcutRegistry.Defaults, pair => Assert.Equal(pair.Value, effective[pair.Key]));
    }

    [Fact]
    public void FindConflicts_DetectsSharedGesture_WithActionLabels()
    {
        var effective = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ShortcutRegistry.FullTextSearch] = "Ctrl+F",
            [ShortcutRegistry.Print] = "ctrl+f",        // 大小写不同但同一手势
            [ShortcutRegistry.CloseTab] = "Ctrl+W",
        };

        var conflict = Assert.Single(ShortcutRegistry.FindConflicts(effective));
        Assert.Contains("Ctrl+F", conflict.Gesture, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, conflict.ActionIds.Count);

        // 冲突文案含动作显示名（设置页与状态栏提示直接复用）
        var description = conflict.Describe(ShortcutRegistry.Labels);
        Assert.Contains("全文搜索", description, StringComparison.Ordinal);
        Assert.Contains("打印", description, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeOverrides_DropsUnknownIdsAndInvalidGestures()
    {
        var clean = ShortcutRegistry.NormalizeOverrides(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ShortcutRegistry.AddBookmark] = "ctrl+alt+b",
            [ShortcutRegistry.Print] = "nonsense",
            ["legacy-action"] = "Ctrl+L",
        });

        Assert.Equal("Ctrl+Alt+B", Assert.Single(clean).Value);
        Assert.True(clean.ContainsKey(ShortcutRegistry.AddBookmark));
    }

    [Fact]
    public void TryParseGesture_MatchesKeyAndModifiers()
    {
        Assert.True(ShortcutRegistry.TryParseGesture("ctrl+shift+p", out var parsed));
        Assert.Equal(Key.P, parsed.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, parsed.Modifiers);

        Assert.False(ShortcutRegistry.TryParseGesture("F", out _));
        Assert.False(ShortcutRegistry.TryParseGesture(null, out _));
    }

    // ---------------------------------------------------------------- 设置持久化

    [Fact]
    public void SettingsNormalize_CleansShortcutOverrides()
    {
        var settings = new AppSettings
        {
            Shortcuts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ShortcutRegistry.Print] = "ctrl+shift+p",
                [ShortcutRegistry.CloseTab] = "F",      // 非法 → 剔除（Resolve 时会回退默认）
                ["bogus"] = "Ctrl+Q",                   // 未知 → 剔除
            },
        };

        var normalized = SettingsStore.Normalize(settings);

        var single = Assert.Single(normalized.Shortcuts);
        Assert.Equal("Ctrl+Shift+P", single.Value);
        Assert.True(normalized.Shortcuts.ContainsKey(ShortcutRegistry.Print));
    }
}
