// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T6 命令面板覆盖层代码后置：打开/关闭、查询同步、键盘导航与执行。
// 执行体由宿主在装配条目时注入（见 MainWindow.BuildCommandPaletteItems），
// 本视图只负责交互，不直接调用任何业务入口。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.CommandPalette;

/// <summary>命令面板覆盖层（Ctrl+K）。</summary>
public partial class CommandPaletteView : UserControl
{
    /// <summary>请求关闭（宿主据此把焦点还给阅读区）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>当前视图模型（DataContext 由宿主注入）。</summary>
    public CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    /// <summary>是否处于打开态。</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    public CommandPaletteView()
    {
        InitializeComponent();
    }

    /// <summary>打开面板：清空查询、重建结果、聚焦输入框。</summary>
    public void Open()
    {
        QueryBox.Text = string.Empty;
        if (ViewModel is { } viewModel)
        {
            // 显式清空：TextBox 本来就是空串时赋值不触发 TextChanged，只靠事件会漏清 VM 查询。
            viewModel.Query = string.Empty;
            viewModel.Rebuild();
        }

        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => QueryBox.Focus()));
    }

    /// <summary>关闭面板（幂等）。</summary>
    public void Close()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换开关（Ctrl+K 语义）。</summary>
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private void OnQueryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Query = QueryBox.Text ?? string.Empty;
        }
    }

    private void OnQueryPreviewKeyDown(object sender, KeyEventArgs e)
        => e.Handled = HandleKey(e.Key);

    /// <summary>
    /// 键盘语义（PreviewKeyDown 与单测共用；返回是否已处理）。
    /// 抽成独立方法是为了让「↑↓/Enter/Esc」可被宿主级单测覆盖——UI 冒烟会因本机 FlaUI 抖动而 skip。
    /// </summary>
    internal bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Down:
                ViewModel?.MoveSelection(1);
                return true;

            case Key.Up:
                ViewModel?.MoveSelection(-1);
                return true;

            case Key.Enter:
                ExecuteSelected();
                return true;

            case Key.Escape:
                Close();
                return true;

            default:
                return false;
        }
    }

    private void OnResultActivated(object sender, MouseButtonEventArgs e) => ExecuteSelected();

    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e) => Close();

    /// <summary>执行当前高亮项（供 HandleKey 与单测调用）：先关闭面板再执行。</summary>
    internal void ExecuteSelected()
    {
        if (ViewModel is not { } viewModel)
        {
            Close();
            return;
        }

        var item = viewModel.Selected;
        if (item is null)
        {
            Close();
            return;
        }

        // 先关闭再执行：执行可能打开工具页/对话框/切换面板，面板不该继续盖在上面。
        Close();
        viewModel.Execute(item);
    }
}
