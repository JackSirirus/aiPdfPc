// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;

namespace AIPDF.Windows.Views.Optimizer;

/// <summary>
/// 优化文件组的页面解析器（T15）：经 ToolPageRegistry（Wave 5 约定）在 MainWindow 注册，
/// 选中「优化文件」组的工具时创建对应页面，其余工具返回 null 交给下一个解析器。
/// </summary>
public static class OptimizerToolPages
{
    /// <summary>本解析器覆盖的工具 id（与 backend/catalog.py 优化文件组一致）。</summary>
    public static readonly IReadOnlyList<string> SupportedToolIds = ["compress", "repair", "ocr"];

    public static FrameworkElement? Resolve(Tool tool, EngineBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Id switch
        {
            "compress" => new CompressPage(bridge, tool),
            "repair" => new RepairPage(bridge, tool),
            "ocr" => new OcrPage(bridge, tool),
            _ => null,
        };
    }

    /// <summary>注册进全局工具页注册表（MainWindow 启动时调用一次）。</summary>
    public static void Register() => ToolPageRegistry.AddResolver(Resolve);
}
