// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T6 命令面板注册表：把「33 工具 / 阅读操作 / 设置」装配成命令条目。
//
// 纯装配逻辑（不依赖 UI）：工具项的 Invoke 由宿主注入，保证与工具箱走同一条通道；
// 这样「命令面板选工具 → 与工具箱行为一致」可以被单测锁定。
using System;
using System.Collections.Generic;
using AIPDF.Windows.Models;

namespace AIPDF.Windows.Services;

/// <summary>阅读操作 / 设置类命令的注册描述（工具项不走此类型）。</summary>
public sealed record CommandPaletteOperation(
    string Id,
    string Title,
    IReadOnlyList<string> Aliases,
    Action Invoke);

/// <summary>命令面板条目装配。</summary>
public static class CommandPaletteRegistry
{
    /// <summary>条目 Id 前缀：工具。</summary>
    public const string ToolIdPrefix = "tool.";

    /// <summary>
    /// 33 个工具 → 条目。<paramref name="invoke"/> 由宿主注入（须复用工具箱通道，
    /// 否则会丢「当前文档置顶 / 页范围预填」）。
    /// </summary>
    public static IReadOnlyList<CommandPaletteItem> BuildToolItems(
        IEnumerable<Tool> tools, Action<Tool> invoke)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(invoke);

        var items = new List<CommandPaletteItem>();
        foreach (var tool in tools)
        {
            var captured = tool;
            items.Add(new CommandPaletteItem
            {
                Id = ToolIdPrefix + captured.Id,
                Title = captured.Name,
                Group = CommandPaletteGroup.Tool,
                Subtitle = captured.Category,
                Aliases = BuildToolAliases(captured),
                Invoke = () => invoke(captured),
            });
        }

        return items;
    }

    /// <summary>阅读操作 / 设置 → 条目。</summary>
    public static IReadOnlyList<CommandPaletteItem> BuildItems(
        IEnumerable<CommandPaletteOperation> operations, CommandPaletteGroup group)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var items = new List<CommandPaletteItem>();
        foreach (var operation in operations)
        {
            var captured = operation;
            items.Add(new CommandPaletteItem
            {
                Id = captured.Id,
                Title = captured.Title,
                Group = group,
                Aliases = captured.Aliases,
                Invoke = captured.Invoke,
            });
        }

        return items;
    }

    /// <summary>
    /// 工具别名：英文 id + 扩展名。工具的中文名之外，用户更常直接输入 <c>ocr</c> / <c>merge</c> /
    /// <c>pdf_to_word</c>，这比拼音首字母更实用；中文输入仍走标题与拼音首字母。
    /// </summary>
    private static IReadOnlyList<string> BuildToolAliases(Tool tool)
    {
        var aliases = new List<string>(1 + tool.Extensions.Count) { tool.Id };
        aliases.AddRange(tool.Extensions);
        return aliases;
    }
}
