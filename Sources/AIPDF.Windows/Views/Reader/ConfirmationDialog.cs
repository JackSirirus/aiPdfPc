// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T10 通用模态确认框（代码构建、无 XAML）：确认/取消按钮文案可自定义。
// 用于 FR-22 redact 删除全部批注警告与 FR-21 flush 失败「强制继续（批注可能丢失）/取消」。
using System.Windows;
using System.Windows.Controls;

namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 代码构建的轻量模态确认框（避免 XAML 的 wpftmp 编译与资源字典耦合）。
/// 确认按钮返回 true，取消 / 关闭返回 false。
/// </summary>
public sealed class ConfirmationDialog : Window
{
    private ConfirmationDialog(string title, string message, string? detail, string confirmText, string cancelText)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 420;
        MaxWidth = 600;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        if (!string.IsNullOrEmpty(detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Opacity = 0.85,
            });
        }

        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 120,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 10, 0),
            IsDefault = true,
        };
        confirm.Click += (_, _) => DialogResult = true;

        var cancel = new Button
        {
            Content = cancelText,
            MinWidth = 88,
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true,
        };
        cancel.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    /// <summary>弹出模态确认框；确认返回 true，取消 / 关闭返回 false。</summary>
    public static bool Show(
        Window? owner, string title, string message, string? detail, string confirmText, string cancelText)
    {
        var dialog = new ConfirmationDialog(title, message, detail, confirmText, cancelText);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }
}
