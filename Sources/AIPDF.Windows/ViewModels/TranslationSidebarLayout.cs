// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
namespace AIPDF.Windows.ViewModels;

/// <summary>
/// 翻译右栏的响应式布局模式（v1.1 T12）。与 UI 无关的纯判定，便于单测。
/// </summary>
public enum TranslationSidebarMode
{
    /// <summary>非文档标签 / 零标签：翻译右栏不显示。</summary>
    Hidden,

    /// <summary>窗口宽度 ≥ 680px：右栏停靠为右侧独立列。</summary>
    Docked,

    /// <summary>480px ≤ 窗口宽度 &lt; 680px：右栏覆盖在阅读区右侧。</summary>
    Overlay,

    /// <summary>窗口宽度 &lt; 480px：右栏折叠，仅保留浮动开关。</summary>
    Collapsed,
}

/// <summary>
/// 翻译右栏响应式布局判定：按窗口宽度解析停靠 / 覆盖 / 折叠。
/// 阈值取自 <c>设计/v1.1-翻译/需求总结.md</c> §最小窗口与响应式（680 / 480）。
/// </summary>
public static class TranslationSidebarLayout
{
    /// <summary>停靠阈值：窗口宽度 ≥ 此值时右栏停靠为独立列，不覆盖阅读区。</summary>
    public const double OverlayThreshold = 680;

    /// <summary>折叠阈值：窗口宽度 &lt; 此值时右栏折叠，仅保留浮动开关。</summary>
    public const double CollapseThreshold = 480;

    /// <summary>按窗口宽度与是否文档标签激活解析布局模式；非文档标签恒为 <see cref="TranslationSidebarMode.Hidden"/>。</summary>
    public static TranslationSidebarMode Resolve(double windowWidth, bool documentTabActive)
    {
        if (!documentTabActive)
        {
            return TranslationSidebarMode.Hidden;
        }

        if (windowWidth < CollapseThreshold)
        {
            return TranslationSidebarMode.Collapsed;
        }

        if (windowWidth < OverlayThreshold)
        {
            return TranslationSidebarMode.Overlay;
        }

        return TranslationSidebarMode.Docked;
    }
}
