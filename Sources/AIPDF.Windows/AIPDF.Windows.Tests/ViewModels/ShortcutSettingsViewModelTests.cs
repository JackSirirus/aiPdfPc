// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T12 快捷键设置 VM 测试：默认加载、改手势、非法拒绝且不改原值、冲突拒绝与强制、
// 恢复单个/全部默认、落盘只写覆盖项、落盘往返可被新实例读回。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

/// <summary>快捷键设置（V4 T12）。</summary>
public sealed class ShortcutSettingsViewModelTests : IDisposable
{
    private readonly string _path;

    public ShortcutSettingsViewModelTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "aipdf-t12-" + Guid.NewGuid().ToString("N"), "settings.json");
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言
        }
    }

    private SettingsStore Store() => new(_path);

    [Fact]
    public void Load_ShowsDefaults_WithoutOverrides()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());

        Assert.Equal(ShortcutRegistry.Actions.Count, viewModel.Items.Count);
        Assert.All(viewModel.Items, item => Assert.Equal(item.DefaultGesture, item.Gesture));
        Assert.All(viewModel.Items, item => Assert.False(item.IsCustomized));
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.HasConflicts);
        Assert.Empty(viewModel.ConflictSummary);
    }

    [Fact]
    public void TrySetGesture_AppliesAndMarksCustomized()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());

        Assert.True(viewModel.TrySetGesture(ShortcutRegistry.Print, "ctrl+shift+p", out var error));
        Assert.Null(error);

        var item = viewModel.Items.Single(row => row.Id == ShortcutRegistry.Print);
        Assert.Equal("Ctrl+Shift+P", item.Gesture);
        Assert.True(item.IsCustomized);
        Assert.True(viewModel.IsDirty);
        Assert.False(item.IsConflicting);
    }

    [Fact]
    public void TrySetGesture_RejectsInvalid_AndKeepsPreviousValue()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());

        Assert.False(viewModel.TrySetGesture(ShortcutRegistry.Print, "F", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        // 拒绝时不得改变原值，也不得把 VM 标脏
        var item = viewModel.Items.Single(row => row.Id == ShortcutRegistry.Print);
        Assert.Equal(item.DefaultGesture, item.Gesture);
        Assert.False(item.IsCustomized);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void TrySetGesture_RejectsConflict_UnlessForced()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());

        // Ctrl+P 已被「打印」占用
        Assert.False(viewModel.TrySetGesture(ShortcutRegistry.CloseTab, "Ctrl+P", out var error));
        Assert.Contains("打印", error!, StringComparison.Ordinal);
        Assert.Equal("Ctrl+W", viewModel.Items.Single(row => row.Id == ShortcutRegistry.CloseTab).Gesture);

        // 强制允许：接受但必须标记冲突（不静默）
        Assert.True(viewModel.TrySetGesture(ShortcutRegistry.CloseTab, "Ctrl+P", out _, force: true));
        Assert.True(viewModel.HasConflicts);
        Assert.Contains("Ctrl+P", viewModel.ConflictSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.Items.Single(row => row.Id == ShortcutRegistry.CloseTab).IsConflicting);
        Assert.True(viewModel.Items.Single(row => row.Id == ShortcutRegistry.Print).IsConflicting);

        // 冲突下保存仍成功，但返回 false 让调用方提示
        Assert.False(viewModel.Save());
    }

    [Fact]
    public void ResetGesture_And_ResetAll_RestoreDefaults()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());
        viewModel.TrySetGesture(ShortcutRegistry.Print, "Ctrl+Shift+P", out _);
        viewModel.TrySetGesture(ShortcutRegistry.CloseTab, "Ctrl+Shift+W", out _);

        viewModel.ResetGesture(ShortcutRegistry.Print);
        Assert.Equal("Ctrl+P", viewModel.Items.Single(row => row.Id == ShortcutRegistry.Print).Gesture);
        Assert.Equal("Ctrl+Shift+W", viewModel.Items.Single(row => row.Id == ShortcutRegistry.CloseTab).Gesture);

        viewModel.ResetAll();
        Assert.All(viewModel.Items, item => Assert.False(item.IsCustomized));
    }

    [Fact]
    public void Save_WritesOnlyOverrides_AndRoundTrips()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());
        viewModel.TrySetGesture(ShortcutRegistry.Print, "Ctrl+Shift+P", out _);
        viewModel.TrySetGesture(ShortcutRegistry.CloseTab, "Ctrl+Shift+W", out _);
        viewModel.ResetGesture(ShortcutRegistry.CloseTab);   // 改回默认 → 不应落盘
        Assert.True(viewModel.Save());
        Assert.False(viewModel.IsDirty);

        // 文件里只有一项覆盖
        var stored = Store().Load().Shortcuts;
        Assert.Equal("Ctrl+Shift+P", Assert.Single(stored).Value);
        Assert.True(stored.ContainsKey(ShortcutRegistry.Print));

        // 新实例（模拟重启）读回同一结果
        var reopened = new ShortcutSettingsViewModel(Store());
        Assert.Equal("Ctrl+Shift+P", reopened.Items.Single(row => row.Id == ShortcutRegistry.Print).Gesture);
        Assert.Equal("Ctrl+W", reopened.Items.Single(row => row.Id == ShortcutRegistry.CloseTab).Gesture);
        Assert.True(reopened.Items.Single(row => row.Id == ShortcutRegistry.Print).IsCustomized);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public void Reload_DiscardsUnsavedChanges()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());
        viewModel.TrySetGesture(ShortcutRegistry.Print, "Ctrl+Shift+P", out _);
        Assert.True(viewModel.IsDirty);

        viewModel.Reload();

        Assert.False(viewModel.IsDirty);
        Assert.All(viewModel.Items, item => Assert.False(item.IsCustomized));
    }

    [Fact]
    public void UnknownActionId_IsRejectedWithError()
    {
        var viewModel = new ShortcutSettingsViewModel(Store());

        Assert.False(viewModel.TrySetGesture("no-such-action", "Ctrl+Q", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(viewModel.IsDirty);
    }
}
