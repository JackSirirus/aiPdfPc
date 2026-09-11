// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 浮菜单工具子菜单可用性探测：不可用工具（如无 LibreOffice 的转 Word）置灰 + tooltip。
// 复用 ConverterViewModels.ConverterDependencyChecks 的探测链（与转换组工具页同源），
// 不新增依赖判定逻辑；测试可注入 OfficeDependencyProbe 接缝。
using System.Collections.Generic;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Reader;

/// <summary>子菜单工具可用性探测器（纯静态，可测）。</summary>
public static class ToolAvailabilityProbe
{
    /// <summary>依赖 LibreOffice 的子菜单工具 id（AC-12.2：无 LibreOffice 时转 Word 置灰）。</summary>
    private static readonly HashSet<string> OfficeDependentIds = new(System.StringComparer.Ordinal)
    {
        "pdf_to_word",
    };

    /// <summary>测试接缝：返回 null 表示依赖可用，否则返回缺失提示。</summary>
    public static Func<string, string?> OfficeDependencyProbe { get; set; } =
        _ => ConverterDependencyChecks.CheckOffice();

    /// <summary>判定工具是否可用（不可用时带禁用原因）。</summary>
    public static ToolAvailability Check(string toolId)
    {
        if (string.IsNullOrEmpty(toolId)) return ToolAvailability.Enabled;

        if (OfficeDependentIds.Contains(toolId))
        {
            var reason = OfficeDependencyProbe(toolId);
            return reason is null ? ToolAvailability.Enabled : new ToolAvailability(false, reason);
        }

        return ToolAvailability.Enabled;
    }
}
