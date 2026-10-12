// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T5 命令面板视图模型：把「阅读操作 / 33 工具 / 设置」收敛为单一可搜索入口
// （对标 SumatraPDF 的 Ctrl+K 命令面板 / Foxit 的 Alt+Q「找命令」）。
//
// 纯 VM：不引用任何 UI 类型，条目自带 Invoke（宿主注入），故可完全单测。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using AIPDF.Windows.Models;

namespace AIPDF.Windows.ViewModels;

/// <summary>命令面板：查询、过滤、排序、最近使用与选择移动。</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>最近使用记录上限。</summary>
    public const int MaxRecent = 8;

    private readonly List<CommandPaletteItem> _items = [];
    private readonly List<string> _recent = [];

    /// <summary>查询串（空串显示全部：最近使用 → 阅读操作 → 工具 → 设置）。</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>当前高亮项（↑↓ 移动；Enter 执行）。</summary>
    [ObservableProperty]
    private CommandPaletteItem? _selected;

    /// <summary>过滤后的结果（按得分与分组排序）。</summary>
    public ObservableCollection<CommandPaletteItem> Results { get; } = [];

    /// <summary>是否无结果（供 UI 显示空态）。</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>最近使用过的命令 Id（MRU 顺序，仅测试与诊断用）。</summary>
    public IReadOnlyList<string> RecentIds => _recent;

    /// <summary>注册命令（重复调用会替换全部条目并重建结果）。</summary>
    public void SetItems(IEnumerable<CommandPaletteItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        _items.AddRange(items);
        Rebuild();
    }

    /// <summary>记录一次使用（影响空查询时的置顶顺序），并重建结果。</summary>
    public void RecordUsage(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _recent.RemoveAll(existing => string.Equals(existing, id, StringComparison.Ordinal));
        _recent.Insert(0, id);
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        }

        Rebuild();
    }

    /// <summary>
    /// 执行结果中的某一项：仅当它确实在<em>当前结果</em>里才执行（避免执行到已被过滤掉的命令），
    /// 成功后记入最近使用并重建。
    /// </summary>
    public bool Execute(CommandPaletteItem? item)
    {
        if (item is null || !Results.Contains(item))
        {
            return false;
        }

        RecordUsage(item.Id);
        item.Invoke();
        return true;
    }

    /// <summary>按 ↑/↓ 移动高亮（delta = ±1；首尾环绕）。无结果时不动。</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            Selected = null;
            return;
        }

        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        var next = index < 0
            ? (delta > 0 ? 0 : Results.Count - 1)
            : ((index + delta) % Results.Count + Results.Count) % Results.Count;
        Selected = Results[next];
    }

    partial void OnQueryChanged(string value) => Rebuild();

    /// <summary>按当前查询重建结果（条目变化、查询变化、最近使用变化后调用）。</summary>
    public void Rebuild()
    {
        var query = (Query ?? string.Empty).Trim();
        var scored = new List<(CommandPaletteItem Item, int Score, int Order)>(_items.Count);

        for (var order = 0; order < _items.Count; order++)
        {
            var item = _items[order];
            var score = query.Length == 0 ? ScoreEmpty(item) : Score(item, query);
            if (score > 0)
            {
                scored.Add((item, score, order));
            }
        }

        var ordered = scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Order)                     // 同分保持注册顺序（稳定）
            .Select(entry => entry.Item)
            .ToList();

        Results.Clear();
        foreach (var item in ordered)
        {
            Results.Add(item);
        }

        // 高亮收敛：原选中项若仍在结果中则保留，否则选第一条。
        Selected = Selected is not null && Results.Contains(Selected) ? Selected : Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>空查询排序：最近使用 → 阅读操作 → 工具 → 设置（同级保持注册顺序）。</summary>
    private int ScoreEmpty(CommandPaletteItem item)
    {
        var recentIndex = _recent.FindIndex(id => string.Equals(id, item.Id, StringComparison.Ordinal));
        if (recentIndex >= 0)
        {
            return 10_000 - recentIndex;   // 最近使用置顶（MRU 越新越高）
        }

        return item.Group switch
        {
            CommandPaletteGroup.Reader => 900,
            CommandPaletteGroup.Tool => 700,
            CommandPaletteGroup.Settings => 500,
            _ => 100,
        };
    }

    /// <summary>
    /// 非空查询打分（大小写不敏感；空格分词为 AND，各词分别匹配后求和）。
    /// 档位（从高到低）：标题全等 → 标题前缀 → 标题包含 → id 全等 → 拼音首字母前缀 →
    /// 拼音首字母包含 → 拼音首字母子序列 → 别名前缀 → 别名包含 → id 包含 → 副标题包含。
    /// 0 分 = 不匹配（被过滤掉）。
    /// </summary>
    internal static int Score(CommandPaletteItem item, string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var token in tokens)
        {
            var score = ScoreToken(item, token.ToLowerInvariant());
            if (score == 0)
            {
                return 0;   // 任一关键词不匹配即整体不匹配
            }

            total += score;
        }

        return total;
    }

    private static int ScoreToken(CommandPaletteItem item, string token)
    {
        var title = item.Title.ToLowerInvariant();
        var id = item.Id.ToLowerInvariant();

        if (title == token) return 1_000;
        if (title.StartsWith(token, StringComparison.Ordinal)) return 900;
        if (title.Contains(token, StringComparison.Ordinal)) return 800;

        if (id == token) return 850;
        if (id.StartsWith(token, StringComparison.Ordinal)) return 600;

        var initials = PinyinInitials.Of(item.Title).ToLowerInvariant();
        if (initials.Length > 0)
        {
            if (initials.StartsWith(token, StringComparison.Ordinal)) return 750;
            if (initials.Contains(token, StringComparison.Ordinal)) return 700;
            if (IsSubsequence(token, initials)) return 400;
        }

        foreach (var alias in item.Aliases)
        {
            var text = alias.ToLowerInvariant();
            if (text.StartsWith(token, StringComparison.Ordinal)) return 650;
            if (text.Contains(token, StringComparison.Ordinal)) return 550;
        }

        if (id.Contains(token, StringComparison.Ordinal)) return 450;

        if (item.Subtitle is { Length: > 0 } subtitle
            && subtitle.ToLowerInvariant().Contains(token, StringComparison.Ordinal))
        {
            return 300;
        }

        return 0;
    }

    /// <summary>token 是否为 text 的子序列（用于拼音首字母的模糊输入，如 qs → qwss）。</summary>
    private static bool IsSubsequence(string token, string text)
    {
        var index = 0;
        foreach (var ch in text)
        {
            if (index < token.Length && token[index] == ch)
            {
                index++;
            }
        }

        return index == token.Length;
    }
}
