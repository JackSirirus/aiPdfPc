// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 「用工具处理 ▾」子菜单固定目录（FR-12 / 01-工具通道映射表）。
// 四项固定工具 + 「更多工具…」；页范围预填仅对确有页范围字段的工具（split/rotate）适用（H-6）。
using System.Collections.Generic;
using System.Linq;

namespace AIPDF.Windows.Views.Reader;

/// <summary>子菜单条目。</summary>
/// <param name="ToolId">与 backend/catalog.py 一致的稳定工具 id。</param>
/// <param name="Label">菜单显示名。</param>
/// <param name="SupportsPageRange">该工具是否有页范围字段可预填（H-6：compress/pdf_to_word 为 false）。</param>
public readonly record struct ToolSubmenuItem(string ToolId, string Label, bool SupportsPageRange);

/// <summary>
/// 浮菜单工具子菜单目录（固定，不改变 <c>ToolChannels</c> 判据与 32 工具映射语义）。
/// 「更多工具…」为独立动作（打开工具箱），ToolId 为 null。
/// </summary>
public static class ToolSubmenuCatalog
{
    /// <summary>固定四项（顺序即菜单顺序）。</summary>
    public static readonly IReadOnlyList<ToolSubmenuItem> Items =
    [
        new("compress", "压缩 PDF", false),
        new("split", "拆分 PDF", true),
        new("rotate", "旋转 PDF", true),
        new("pdf_to_word", "转 Word", false),
    ];

    /// <summary>「更多工具…」标签（打开工具箱面板）。</summary>
    public const string MoreToolsLabel = "更多工具…";

    /// <summary>按 id 查固定条目（不存在返回 null）。</summary>
    public static ToolSubmenuItem? Find(string? toolId)
    {
        if (string.IsNullOrEmpty(toolId)) return null;
        foreach (var item in Items)
        {
            if (string.Equals(item.ToolId, toolId, System.StringComparison.Ordinal)) return item;
        }
        return null;
    }

    /// <summary>固定四项的工具 id 集合（用于测试与路由校验）。</summary>
    public static IReadOnlyList<string> ToolIds => Items.Select(i => i.ToolId).ToArray();
}

/// <summary>工具可用性（宿主判定，如无 LibreOffice 的转 Word）。</summary>
/// <param name="Available">是否可用；false 时菜单项置灰。</param>
/// <param name="DisabledReason">置灰原因（tooltip）。</param>
public readonly record struct ToolAvailability(bool Available, string? DisabledReason)
{
    /// <summary>可用。</summary>
    public static ToolAvailability Enabled => new(true, null);
}

/// <summary>
/// 浮菜单「用工具处理」发起请求。<see cref="ToolId"/> 为 null 表示「更多工具…」（宿主打开工具箱）。
/// 宿主负责经既有 <c>ToolChannels</c>/<c>ShowToolPage</c> 打开（右抽屉 / 临时标签），
/// 并按 <see cref="PrefillPageRange"/> 对支持页范围的工具预填「当前页-当前页」。
/// </summary>
public sealed class ToolLaunchRequest : EventArgs
{
    /// <summary>目标工具 id；null = 更多工具。</summary>
    public string? ToolId { get; init; }

    /// <summary>是否要求把当前文档置顶（固定四项一律 true）。</summary>
    public bool PinCurrentDocument { get; init; } = true;

    /// <summary>是否预填页范围（仅 split/rotate）。</summary>
    public bool PrefillPageRange { get; init; }

    /// <summary>预填起始页（1 基，含）。</summary>
    public int PageStart { get; init; }

    /// <summary>预填结束页（1 基，含）。</summary>
    public int PageEnd { get; init; }

    /// <summary>「更多工具…」请求。</summary>
    public static ToolLaunchRequest MoreTools() => new() { ToolId = null, PinCurrentDocument = false };
}
