// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T12 快捷键设置面板视图：只做视图职责（捕获按键组合、展示校验错误、回退非法输入），
// 业务（合法性、冲突、恢复默认、落盘）全在 ShortcutSettingsViewModel。
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Settings;

/// <summary>快捷键设置面板（DataContext = <see cref="ShortcutSettingsViewModel"/>）。</summary>
public partial class ShortcutSettingsPanel : UserControl
{
    public ShortcutSettingsPanel() => InitializeComponent();

    private ShortcutSettingsViewModel? ViewModel => DataContext as ShortcutSettingsViewModel;

    /// <summary>聚焦即全选，便于直接覆盖输入。</summary>
    private void OnGestureBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    /// <summary>
    /// 直接捕获按键组合（比手打字符串可靠）。只按修饰键时不提交；Esc 放弃本次编辑。
    /// </summary>
    private void OnGestureBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string actionId } box)
        {
            return;
        }

        if (e.Key is Key.Tab or Key.Enter)
        {
            return;   // 交给常规焦点/提交行为（LostFocus 会提交）
        }

        if (e.Key == Key.Escape)
        {
            RevertBox(box, actionId);
            ClearError();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // Alt 组合下 WPF 把主键放在 SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;   // 只按了修饰键：等主键
        }

        e.Handled = true;
        Commit(box, actionId, FormatGesture(key, Keyboard.Modifiers));
    }

    private void OnGestureBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string actionId } box)
        {
            Commit(box, actionId, box.Text);
        }
    }

    private void OnResetRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string actionId })
        {
            ViewModel?.ResetGesture(actionId);
            ClearError();
        }
    }

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ResetAll();
        ClearError();
    }

    /// <summary>提交一次手势编辑：非法或冲突则展示原因并把输入框回退到 VM 中的原值。</summary>
    private void Commit(TextBox box, string actionId, string? gesture)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.TrySetGesture(actionId, gesture, out var error))
        {
            ClearError();
            box.Text = viewModel.Items.FirstOrDefault(item => item.Id == actionId)?.Gesture ?? box.Text;
            return;
        }

        ShowError(error);
        RevertBox(box, actionId);
    }

    private void RevertBox(TextBox box, string actionId)
    {
        var current = ViewModel?.Items.FirstOrDefault(item => item.Id == actionId)?.Gesture;
        if (current is not null)
        {
            box.Text = current;
        }
    }

    private void ShowError(string? message)
    {
        ShortcutError.Text = message ?? string.Empty;
        ShortcutError.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearError() => ShowError(null);

    private static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        try
        {
            return new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key, modifiers)) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;   // 无法表达的组合：交给 VM 判非法并提示
        }
    }
}
