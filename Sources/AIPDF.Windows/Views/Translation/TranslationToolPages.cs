// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 翻译工具页解析器：经 ToolPageRegistry 在 MainWindow 注册，选中「批量翻译」时创建工具页。
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 翻译工具组的页面解析器（T21）：目前覆盖 <c>translate_batch</c>（批量翻译工具页）。
/// 阅读器右栏的划词 / 整篇由 MainWindow 直接接线，不经本解析器。
/// </summary>
public static class TranslationToolPages
{
    /// <summary>本解析器覆盖的工具 id（与 backend/catalog.py 目录一致）。</summary>
    public static readonly IReadOnlyList<string> SupportedToolIds = ["translate_batch"];

    public static FrameworkElement? Resolve(Tool tool, EngineBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Id switch
        {
            "translate_batch" => new TranslationBatchPage(bridge, tool),
            _ => null,
        };
    }

    /// <summary>注册进全局工具页注册表（MainWindow 启动时调用一次）。</summary>
    public static void Register() => ToolPageRegistry.AddResolver(Resolve);
}
