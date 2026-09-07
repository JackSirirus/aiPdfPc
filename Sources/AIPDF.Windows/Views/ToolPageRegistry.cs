// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;

namespace AIPDF.Windows.Views;

/// <summary>
/// 工具页导航注册表（Wave 5 并行约定）：各工具组任务在 MainWindow 构造时各加一行注册，
/// 解析器逻辑留在各任务自己的 Views 子目录内；MainWindow 选中工具时按注册顺序尝试创建页面，
/// 无人认领则沿用主窗口占位视图。
/// </summary>
public static class ToolPageRegistry
{
    private static readonly List<Func<Tool, EngineBridge?, FrameworkElement?>> Resolvers = [];

    /// <summary>注册一个工具页解析器（每个工具组任务调用一次）。</summary>
    public static void AddResolver(Func<Tool, EngineBridge?, FrameworkElement?> resolver)
        => Resolvers.Add(resolver);

    /// <summary>按注册顺序尝试为工具创建页面；无人认领返回 null。</summary>
    public static FrameworkElement? TryCreate(Tool tool, EngineBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Resolvers.Select(resolver => resolver(tool, bridge)).FirstOrDefault(page => page is not null);
    }

    /// <summary>测试用：清空注册表。</summary>
    internal static void Reset() => Resolvers.Clear();
}
