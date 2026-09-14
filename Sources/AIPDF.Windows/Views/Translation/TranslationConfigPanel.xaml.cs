// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 共享配置面板（T13）：阅读器「整篇」Tab（T15）与批量翻译工具页（T21）复用同一 UserControl。
using System.Windows.Controls;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 共享翻译配置面板。DataContext 为 <see cref="TranslationConfigViewModel"/>：
/// 宿主用 <c>DataContext="{Binding Config}"</c> 注入即可，控制内部不持有宿主 VM，保证整篇 Tab 与批量页同源复用。
/// </summary>
public partial class TranslationConfigPanel : UserControl
{
    /// <summary>XAML 加载用默认构造。</summary>
    public TranslationConfigPanel()
    {
        InitializeComponent();
    }

    /// <summary>便捷构造：直接注入配置视图模型（等价于手动设置 DataContext）。</summary>
    public TranslationConfigPanel(TranslationConfigViewModel config)
        : this()
        => DataContext = config;
}
