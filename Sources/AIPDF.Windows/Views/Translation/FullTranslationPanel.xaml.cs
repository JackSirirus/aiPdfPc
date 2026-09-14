// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15「整篇」Tab 面板 code-behind：只做文件夹选择；翻译逻辑全部在 FullTranslationViewModel。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.ViewModels.Translation;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 阅读器右栏「整篇」Tab 面板。DataContext 为 <see cref="FullTranslationViewModel"/>（宿主注入）；
/// 内部配置区复用 <see cref="TranslationConfigPanel"/>（DataContext = VM.Config）。
/// </summary>
public partial class FullTranslationPanel : UserControl
{
    /// <summary>XAML 加载用默认构造。</summary>
    public FullTranslationPanel()
    {
        InitializeComponent();
    }

    /// <summary>选择输出文件夹（引擎会在其下新建任务目录，不覆盖源文件）。</summary>
    private void OnBrowseOutputDirClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FullTranslationViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        var current = viewModel.OutputDir?.Trim() ?? "";
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog() == true)
        {
            viewModel.OutputDir = dialog.FolderName;
        }
    }
}
