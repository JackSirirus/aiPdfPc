// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>工作流程组（"工作流程"）工具页注册。MainWindow 每组一行：WorkflowPages.Register()。</summary>
public static class WorkflowPages
{
    /// <summary>本组覆盖的工具 id，与 backend/catalog.py 的"工作流程"组一致（单工具：步骤串联+预设+批量+记录）。</summary>
    public static IReadOnlyList<string> ToolIds { get; } = ["workflow"];

    public static void Register() => ToolPageRegistry.AddResolver(Create);

    public static FrameworkElement? Create(Tool tool, EngineBridge? bridge)
        => tool.Id == "workflow" ? new WorkflowPage(bridge, ToolCatalog.Load(), tool) : null;
}
