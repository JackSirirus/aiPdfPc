// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T5 命令面板条目模型：把「阅读操作 / 33 工具 / 设置」收敛为统一的可搜索命令。
using System;
using System.Collections.Generic;

namespace AIPDF.Windows.Models;

/// <summary>命令分组（决定空查询时的展示顺序与结果分组）。</summary>
public enum CommandPaletteGroup
{
    /// <summary>最近使用（仅空查询时置顶显示，不参与注册）。</summary>
    Recent = 0,

    /// <summary>阅读操作（跳页/缩放/全屏/搜索/打印/书签…）。</summary>
    Reader = 1,

    /// <summary>工具（来自 catalog.json 的 33 个工具）。</summary>
    Tool = 2,

    /// <summary>设置与关于。</summary>
    Settings = 3,
}

/// <summary>
/// 命令面板条目。<see cref="Invoke"/> 由宿主注入（VM 不依赖任何 UI 类型，便于纯单测）。
/// </summary>
public sealed record CommandPaletteItem
{
    /// <summary>稳定标识（用于最近使用记录；工具项用 catalog 的 tool id）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（中文标题）。</summary>
    public required string Title { get; init; }

    /// <summary>分组。</summary>
    public required CommandPaletteGroup Group { get; init; }

    /// <summary>副标题（工具项填分类；其余可空）。</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// 检索别名（大小写不敏感的子串匹配）。工具项填英文 id 与扩展名，
    /// 阅读操作填拼音全拼与首字母等；用于补足中文标题之外的输入路径。
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>执行体（宿主注入）。</summary>
    public required Action Invoke { get; init; }
}
