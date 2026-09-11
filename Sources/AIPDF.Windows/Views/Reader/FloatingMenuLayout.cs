// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 浮菜单定位纯逻辑（FR-11）：优先在选区下方弹出，下方空间不足翻转到上方，
// 水平方向居中并钳制在视口内（左右各留边距）。无 WPF 依赖，xunit 直接覆盖。
namespace AIPDF.Windows.Views.Reader;

/// <summary>菜单在视口中的落点（DIP，视口左上原点）与相对选区的位置。</summary>
public readonly record struct MenuPlacement(double Left, double Top, bool Below);

/// <summary>浮菜单/悬停操作条的定位计算（纯函数）。</summary>
public static class FloatingMenuLayout
{
    /// <summary>视口边缘最小留白（DIP）。</summary>
    public const double DefaultMargin = 8.0;

    /// <summary>菜单与选区之间的间隙（DIP）。</summary>
    public const double DefaultGap = 6.0;

    /// <summary>
    /// 计算菜单落点。selection 为选区在视口中的矩形（DIP）；viewport/menu 为对应尺寸。
    /// 规则：优先下方（selection.Bottom + gap）；若下方越界则翻转到上方（selection.Top - gap - menuH）；
    /// 上下都放不下时钳制到视口内；水平居中后钳制到 [margin, viewport.Width - menu.Width - margin]。
    /// </summary>
    public static MenuPlacement Compute(
        (double Left, double Top, double Width, double Height) selection,
        (double Width, double Height) viewport,
        (double Width, double Height) menu,
        double margin = DefaultMargin,
        double gap = DefaultGap)
    {
        if (menu.Width <= 0 || menu.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return new MenuPlacement(margin, margin, false);
        }

        var spaceBelow = viewport.Height - selection.Top - selection.Height - gap - margin;
        var below = spaceBelow >= menu.Height;
        var top = below
            ? selection.Top + selection.Height + gap
            : selection.Top - gap - menu.Height;

        // 上下都放不下（菜单高于可用高度）：压回视口内（顶部留边距优先）
        var maxTop = Math.Max(margin, viewport.Height - menu.Height - margin);
        if (top < margin || top > maxTop)
        {
            top = Math.Clamp(top, margin, maxTop);
        }

        var centered = selection.Left + selection.Width / 2.0 - menu.Width / 2.0;
        var maxLeft = Math.Max(margin, viewport.Width - menu.Width - margin);
        var left = Math.Clamp(centered, margin, maxLeft);

        return new MenuPlacement(left, top, below);
    }
}
