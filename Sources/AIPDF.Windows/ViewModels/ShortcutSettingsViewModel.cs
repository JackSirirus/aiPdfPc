// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T12：快捷键设置视图模型（设置页直接绑定它）。
//
// 与 `ShortcutRegistry`（纯逻辑）的分工：注册表负责「有哪些动作 / 手势是否合法 / 如何解析」，
// 本 VM 负责「编辑中的工作副本 + 冲突拒绝 + 恢复默认 + 落盘」。
//
// 落盘沿用 `SettingsStore` 范式（`%APPDATA%\AIPDF\settings.json`）；只写**与默认不同**的覆盖项，
// 保持文件最小且默认值可随版本演进。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AIPDF.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>设置页的一行：一个动作及其当前手势。</summary>
public sealed partial class ShortcutSettingItem : ObservableObject
{
    public ShortcutSettingItem(ShortcutAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Id = action.Id;
        Label = action.Label;
        DefaultGesture = action.DefaultGesture;
        _gesture = action.DefaultGesture;
    }

    /// <summary>动作 Id（与注册表一致）。</summary>
    public string Id { get; }

    /// <summary>动作显示名。</summary>
    public string Label { get; }

    /// <summary>默认手势（「恢复默认」与「是否已改」的判据）。</summary>
    public string DefaultGesture { get; }

    /// <summary>当前手势。</summary>
    [ObservableProperty]
    private string _gesture;

    /// <summary>当前是否与其它动作冲突（UI 标红 + 提示）。</summary>
    [ObservableProperty]
    private bool _isConflicting;

    /// <summary>是否已被用户改过（≠ 默认）。</summary>
    public bool IsCustomized => !string.Equals(Gesture, DefaultGesture, StringComparison.OrdinalIgnoreCase);

    partial void OnGestureChanged(string value) => OnPropertyChanged(nameof(IsCustomized));
}

/// <summary>快捷键设置（动作清单 + 冲突检测 + 恢复默认 + 保存）。</summary>
public sealed partial class ShortcutSettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly Dictionary<string, string> _working = new(StringComparer.Ordinal);

    public ShortcutSettingsViewModel(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        foreach (var action in ShortcutRegistry.Actions)
        {
            Items.Add(new ShortcutSettingItem(action));
        }

        Reload();
    }

    /// <summary>动作行（顺序与注册表一致）。</summary>
    public ObservableCollection<ShortcutSettingItem> Items { get; } = [];

    /// <summary>当前配置下的冲突（同手势被多个动作占用）。</summary>
    public ObservableCollection<ShortcutConflict> Conflicts { get; } = [];

    /// <summary>是否有冲突。</summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>冲突摘要（无冲突为空串；设置页顶部提示直接展示）。</summary>
    public string ConflictSummary => Conflicts.Count == 0
        ? string.Empty
        : string.Join("；", Conflicts.Select(conflict => conflict.Describe(ShortcutRegistry.Labels)));

    /// <summary>是否有未保存改动。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>从设置文件重新加载（丢弃未保存改动）。</summary>
    public void Reload()
    {
        var effective = ShortcutRegistry.Resolve(_store.Load().Shortcuts);
        _working.Clear();
        foreach (var (actionId, gesture) in effective)
        {
            // 工作副本只保留「与默认不同」的项——落盘时同样只写这些
            if (!string.Equals(gesture, ShortcutRegistry.DefaultFor(actionId), StringComparison.OrdinalIgnoreCase))
            {
                _working[actionId] = gesture;
            }
        }

        foreach (var item in Items)
        {
            item.Gesture = effective.TryGetValue(item.Id, out var gesture) ? gesture : item.DefaultGesture;
        }

        IsDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        RebuildConflicts();
    }

    /// <summary>
    /// 设置某动作的手势。手势非法 → 返回 false 且**不改变原值**；
    /// 与其它动作冲突 → 默认拒绝（<paramref name="force"/> = true 时允许，仅用于「我就要这样」）。
    /// </summary>
    public bool TrySetGesture(string actionId, string? gesture, out string? error, bool force = false)
    {
        error = null;
        var item = Items.FirstOrDefault(candidate => string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        if (item is null)
        {
            error = "未知的快捷键动作。";
            return false;
        }

        if (ShortcutRegistry.NormalizeGesture(gesture) is not { } normalized)
        {
            error = "手势无效：需要「修饰键 + 按键」（如 Ctrl+Shift+P），功能键可单独使用；"
                    + "鼠标手势与 Esc 不在可自定义范围内。";
            return false;
        }

        if (!force && FindOwner(normalized, actionId) is { } owner)
        {
            error = $"{normalized} 已被「{owner}」使用，请先改那一个。";
            return false;
        }

        item.Gesture = normalized;
        if (string.Equals(normalized, item.DefaultGesture, StringComparison.OrdinalIgnoreCase))
        {
            _working.Remove(actionId);   // 改回默认 = 不需要覆盖项
        }
        else
        {
            _working[actionId] = normalized;
        }

        MarkDirty();
        RebuildConflicts();
        return true;
    }

    /// <summary>恢复单个动作的默认手势。</summary>
    public void ResetGesture(string actionId)
    {
        var item = Items.FirstOrDefault(candidate => string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        if (item is null || string.Equals(item.Gesture, item.DefaultGesture, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        item.Gesture = item.DefaultGesture;
        _working.Remove(actionId);
        MarkDirty();
        RebuildConflicts();
    }

    /// <summary>恢复全部默认手势。</summary>
    public void ResetAll()
    {
        foreach (var item in Items)
        {
            item.Gesture = item.DefaultGesture;
        }

        _working.Clear();
        MarkDirty();
        RebuildConflicts();
    }

    /// <summary>
    /// 落盘：只写与默认不同的覆盖项（保持文件最小、默认值可随版本演进）。
    /// 冲突存在时仍允许保存（与 `ApplyShortcuts` 的「保留用户设置 + 提示」一致），但返回 false
    /// 让调用方决定是否提示。
    /// </summary>
    public bool Save()
    {
        var settings = _store.Load();
        settings.Shortcuts = new Dictionary<string, string>(_working, StringComparer.Ordinal);
        _store.Save(settings);

        IsDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        return !HasConflicts;
    }

    private string? FindOwner(string gesture, string exceptActionId)
    {
        foreach (var item in Items)
        {
            if (string.Equals(item.Id, exceptActionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(item.Gesture, gesture, StringComparison.OrdinalIgnoreCase))
            {
                return item.Label;
            }
        }

        return null;
    }

    private void MarkDirty()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void RebuildConflicts()
    {
        var effective = Items.ToDictionary(item => item.Id, item => item.Gesture, StringComparer.Ordinal);
        var conflicts = ShortcutRegistry.FindConflicts(effective);

        Conflicts.Clear();
        foreach (var conflict in conflicts)
        {
            Conflicts.Add(conflict);
        }

        var conflicting = conflicts.SelectMany(conflict => conflict.ActionIds).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            item.IsConflicting = conflicting.Contains(item.Id);
        }

        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictSummary));
    }
}
