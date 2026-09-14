// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词结果卡片 code-behind：只把四个操作按钮转发为 ActionRequested 事件，
// 具体动作（复制 / 替换标注 / 加入术语 / 重译）由宿主 MainWindow 执行——卡片本身不碰剪贴板、
// 不落盘、不发请求，保持无副作用可直测。
using System.Windows;
using System.Windows.Controls;

namespace AIPDF.Windows.Views.Translation;

/// <summary>卡片操作。</summary>
public enum SelectionTranslationCardAction
{
    /// <summary>复制译文到剪贴板。</summary>
    Copy,

    /// <summary>把译文写入独立 sidecar 替换标注（不写回源 PDF）。</summary>
    ReplaceAnnotation,

    /// <summary>把选中词条加入术语表。</summary>
    AddGlossary,

    /// <summary>用同一原文重新翻译。</summary>
    Retranslate,
}

/// <summary>划词结果卡片（DataContext = <c>SelectionTranslationCardViewModel</c>）。</summary>
public partial class SelectionTranslationCard : UserControl
{
    public SelectionTranslationCard()
    {
        InitializeComponent();
    }

    /// <summary>四个操作按钮的转发事件（payload 为被点击的操作）。</summary>
    public event EventHandler<SelectionTranslationCardAction>? ActionRequested;

    private void OnActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && Enum.TryParse<SelectionTranslationCardAction>(tag, out var action))
        {
            ActionRequested?.Invoke(this, action);
        }
    }
}
