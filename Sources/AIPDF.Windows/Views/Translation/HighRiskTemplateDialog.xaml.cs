// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 高风险模板确认对话框 code-behind：只负责收集「确认」意图，不承载翻译逻辑。
// 确认框默认未勾选，「确认并开始」在勾选前禁用；取消 / Esc → false，宿主据此不开始翻译。
using System.Windows;
using System.Windows.Controls;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 法律 / 医学高风险模板整篇翻译前的确认框（F2b / 需求总结 §9.3）。
/// 仅当用户显式勾选并确认时返回 true；默认未勾选，避免把未审核机翻直接用于高风险文档。
/// </summary>
public partial class HighRiskTemplateDialog : Window
{
    private HighRiskTemplateDialog(string templateLabel)
    {
        InitializeComponent();

        var label = string.IsNullOrWhiteSpace(templateLabel) ? "法律 / 医学" : templateLabel.Trim();
        TemplateLead.Text =
            $"你选择了「{label}」模板。机器翻译不能保证条款、金额、日期、编号等关键信息的准确性，"
            + "不构成专业意见，必须由具备资质的人复核后方可使用。";
    }

    /// <summary>
    /// 模态展示确认框。<paramref name="templateLabel"/> 为所选模板显示名（法律 / 医学）。
    /// 返回 true 表示用户勾选并确认已知悉风险；取消 / Esc / 关闭 → false。
    /// </summary>
    public static bool Show(Window? owner, string templateLabel)
    {
        var dialog = new HighRiskTemplateDialog(templateLabel);
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
