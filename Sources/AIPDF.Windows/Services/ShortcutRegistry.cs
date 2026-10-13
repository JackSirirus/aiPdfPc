// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T12：可自定义快捷键注册表。
//
// 口径（勿写错）：可自定义的是 **5 个 KeyBinding（Ctrl+F/W/P/B/K）+ 1 个代码分支（Ctrl+G）**。
//   - `Ctrl+滚轮` 是鼠标手势、`Esc` 是弹层收起逻辑，**都不是** KeyBinding；要自定义必须先重构成命令，
//     本任务不做（计划已明确此边界）。
//   - 计划成文时只有 3 个 KeyBinding（Ctrl+F/W/P）；Ctrl+B（T1 书签）与 Ctrl+K（T5 命令面板）
//     是本轮 V4 新增，故实际为 5+1。
//
// 纯逻辑、无 WPF 控件依赖（只借 KeyGesture 做语法校验），因此可独立单测。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace AIPDF.Windows.Services;

/// <summary>一个可自定义的快捷键动作。</summary>
public sealed record ShortcutAction(string Id, string Label, string DefaultGesture);

/// <summary>两（或多）个动作占用了同一手势。</summary>
public sealed record ShortcutConflict(string Gesture, IReadOnlyList<string> ActionIds)
{
    /// <summary>冲突文案（含动作名，供设置页与提示直接展示）。</summary>
    public string Describe(IReadOnlyDictionary<string, string> labels)
        => $"{Gesture} 被 {string.Join("、", ActionIds.Select(id => labels.TryGetValue(id, out var l) ? l : id))} 同时使用";
}

/// <summary>快捷键动作清单 + 手势校验/解析 + 冲突检测。</summary>
public static class ShortcutRegistry
{
    public const string FullTextSearch = "fullTextSearch";
    public const string CloseTab = "closeTab";
    public const string Print = "print";
    public const string AddBookmark = "addBookmark";
    public const string CommandPalette = "commandPalette";
    public const string GoToPage = "goToPage";

    /// <summary>全部可自定义动作（顺序即设置页展示顺序）。</summary>
    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new(FullTextSearch, "全文搜索", "Ctrl+F"),
        new(CloseTab, "关闭标签", "Ctrl+W"),
        new(Print, "打印", "Ctrl+P"),
        new(AddBookmark, "收藏当前页", "Ctrl+B"),
        new(CommandPalette, "命令面板", "Ctrl+K"),
        new(GoToPage, "跳转到指定页", "Ctrl+G"),
    ];

    /// <summary>动作 Id → 默认手势。</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        Actions.ToDictionary(action => action.Id, action => action.DefaultGesture, StringComparer.Ordinal);

    /// <summary>动作 Id → 显示名（冲突文案与设置页共用）。</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } =
        Actions.ToDictionary(action => action.Id, action => action.Label, StringComparer.Ordinal);

    private static readonly KeyGestureConverter Converter = new();

    /// <summary>
    /// 校验并规范化手势字符串（如 <c>ctrl+f</c> → <c>Ctrl+F</c>）。无效返回 null。
    ///
    /// 拒绝两类：**语法无效**（无法解析）、**无修饰键的普通键**（裸 <c>F</c> 会在输入时抢键）；
    /// 功能键（F1–F24）允许无修饰键，符合桌面应用惯例。
    /// </summary>
    public static string? NormalizeGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        try
        {
            if (Converter.ConvertFromInvariantString(gesture.Trim()) is not KeyGesture parsed)
            {
                return null;
            }

            if (parsed.Modifiers == ModifierKeys.None && !IsFunctionKey(parsed.Key))
            {
                return null;   // 裸字母/数字键不得作为全局快捷键
            }

            return Converter.ConvertToInvariantString(new KeyGesture(parsed.Key, parsed.Modifiers));
        }
        catch (Exception)
        {
            return null;   // 无法解析：视为无效（调用方回退默认）
        }
    }

    /// <summary>解析手势为 <see cref="KeyGesture"/>（供代码分支匹配键事件）。</summary>
    public static bool TryParseGesture(string? gesture, out KeyGesture parsed)
    {
        parsed = null!;
        if (NormalizeGesture(gesture) is not { } normalized)
        {
            return false;
        }

        try
        {
            if (Converter.ConvertFromInvariantString(normalized) is KeyGesture value)
            {
                parsed = value;
                return true;
            }
        }
        catch (Exception)
        {
            // 落到 false
        }

        return false;
    }

    /// <summary>剔除未知动作 Id 与非法手势（手改 settings.json 不得让应用崩或丢默认）。</summary>
    public static Dictionary<string, string> NormalizeOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overrides is null)
        {
            return result;
        }

        foreach (var (id, gesture) in overrides)
        {
            if (!Defaults.ContainsKey(id))
            {
                continue;   // 未知动作（旧版本遗留）：丢弃
            }

            if (NormalizeGesture(gesture) is { } normalized)
            {
                result[id] = normalized;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析每个动作的**有效手势**：覆盖优先，其余用默认。无效覆盖自动回退默认（不会丢快捷键）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string>? overrides)
    {
        var clean = NormalizeOverrides(overrides);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in Actions)
        {
            result[action.Id] = clean.TryGetValue(action.Id, out var gesture) ? gesture : action.DefaultGesture;
        }

        return result;
    }

    /// <summary>冲突检测：同一手势被多个动作占用（按手势分组，只返回冲突项）。</summary>
    public static IReadOnlyList<ShortcutConflict> FindConflicts(IReadOnlyDictionary<string, string>? effective)
    {
        if (effective is null)
        {
            return [];
        }

        return effective
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new ShortcutConflict(
                group.Key,
                [.. group.Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal)]))
            .OrderBy(conflict => conflict.Gesture, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>单个动作的默认手势（未知动作返回 null）。</summary>
    public static string? DefaultFor(string actionId)
        => Defaults.TryGetValue(actionId, out var gesture) ? gesture : null;

    private static bool IsFunctionKey(Key key)
        => key is >= Key.F1 and <= Key.F24;
}
