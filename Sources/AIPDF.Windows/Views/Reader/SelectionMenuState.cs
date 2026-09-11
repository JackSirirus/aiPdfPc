// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 浮菜单状态机（FR-11/FR-13）：主菜单 / 工具子菜单 / 笔记输入 / 色板 / 悬停操作条五态切换。
// 纯状态、无 WPF 依赖，xunit 覆盖「弹出 / 关闭 / 子菜单进入返回」。
namespace AIPDF.Windows.Views.Reader;

/// <summary>浮菜单模式。<see cref="Closed"/> 表示无菜单；编辑类模式仅在已有打开菜单时可进入。</summary>
public enum MenuMode
{
    /// <summary>无菜单。</summary>
    Closed,

    /// <summary>选区主菜单：复制 | 高亮 | 笔记 | 用工具处理 ▾。</summary>
    Selection,

    /// <summary>工具子菜单（主菜单「用工具处理 ▾」展开）。</summary>
    ToolSubmenu,

    /// <summary>笔记输入弹层（主菜单「笔记」展开）。</summary>
    NoteEditor,

    /// <summary>改色预设色板（悬停操作条「改色」展开）。</summary>
    ColorPalette,

    /// <summary>已有批注的悬停操作条：编辑 / 删除 / 改色（FR-13/H-9）。</summary>
    HoverActions,
}

/// <summary>
/// 浮菜单状态机。规则：
///  - 只有 <see cref="Open"/> 能建立菜单；<see cref="Closed"/> 时任何子模式进入被忽略。
///  - Esc 在子模式时先返回主菜单（<see cref="Back"/>），在主菜单时关闭。
///  - 点击外部 / 滚动 / 拖选新区域一律关闭。
/// </summary>
public sealed class SelectionMenuState
{
    /// <summary>当前模式。</summary>
    public MenuMode Mode { get; private set; } = MenuMode.Closed;

    /// <summary>是否有菜单打开（任一非 Closed 模式）。</summary>
    public bool IsOpen => Mode != MenuMode.Closed;

    /// <summary>打开选区主菜单（新的拖选释放或新的悬停命中调用）。</summary>
    public void Open() => Mode = MenuMode.Selection;

    /// <summary>打开悬停操作条。</summary>
    public void OpenHover() => Mode = MenuMode.HoverActions;

    /// <summary>关闭菜单（点击外部 / 滚动 / Esc 在主菜单）。</summary>
    public void Close() => Mode = MenuMode.Closed;

    /// <summary>进入工具子菜单（仅在菜单已打开时）。</summary>
    public void OpenToolSubmenu()
    {
        if (IsOpen) Mode = MenuMode.ToolSubmenu;
    }

    /// <summary>进入笔记输入弹层（仅在菜单已打开时）。</summary>
    public void OpenNoteEditor()
    {
        if (IsOpen) Mode = MenuMode.NoteEditor;
    }

    /// <summary>进入改色色板（仅在菜单已打开时）。</summary>
    public void OpenColorPalette()
    {
        if (IsOpen) Mode = MenuMode.ColorPalette;
    }

    /// <summary>Esc：子模式返回主菜单，主菜单/悬停条则关闭。返回是否消费了 Esc。</summary>
    public bool Back()
    {
        switch (Mode)
        {
            case MenuMode.ToolSubmenu:
            case MenuMode.NoteEditor:
            case MenuMode.ColorPalette:
                Mode = MenuMode.Selection;
                return true;

            case MenuMode.Selection:
            case MenuMode.HoverActions:
                Mode = MenuMode.Closed;
                return true;

            default:
                return false;
        }
    }
}
