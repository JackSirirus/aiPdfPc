// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 受限文档确认对话框 code-behind：只负责收集「确认」意图，不承载翻译逻辑。
// 确认框默认未勾选，「确认并开始」在勾选前禁用；取消 / Esc → false，宿主据此不开始翻译。
using System.Windows;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 受限（权限加密）文档整篇翻译前的确认框（决策 #19）。仅当用户显式勾选并确认时返回 true。
/// </summary>
public partial class RestrictedDocumentDialog : Window
{
    private RestrictedDocumentDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 模态展示确认框。返回 true 表示用户勾选并确认「有权处理该文档」；取消 / Esc / 关闭 → false。
    /// </summary>
    public static bool Show(Window? owner)
    {
        var dialog = new RestrictedDocumentDialog();
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmCheck.IsChecked == true)
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
