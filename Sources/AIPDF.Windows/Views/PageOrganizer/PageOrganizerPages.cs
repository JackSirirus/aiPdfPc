// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.PageOrganizer;

/// <summary>页面整理组（"整理页面"）工具页注册。MainWindow 每组一行：PageOrganizerPages.Register()。</summary>
public static class PageOrganizerPages
{
    /// <summary>本组覆盖的工具 id，顺序与 backend/catalog.py 的"整理页面"组一致。</summary>
    public static IReadOnlyList<string> ToolIds { get; } = ["merge", "split", "remove", "extract", "organize", "scan"];

    public static void Register() => ToolPageRegistry.AddResolver(Create);

    public static FrameworkElement? Create(Tool tool, EngineBridge? bridge) => tool.Id switch
    {
        "merge" => new MergePage(tool, bridge),
        "split" => new SplitPage(tool, bridge),
        "remove" => new RemovePage(tool, bridge),
        "extract" => new ExtractPage(tool, bridge),
        "organize" => new OrganizePage(tool, bridge),
        "scan" => new ScanPage(tool, bridge),
        _ => null,
    };
}
