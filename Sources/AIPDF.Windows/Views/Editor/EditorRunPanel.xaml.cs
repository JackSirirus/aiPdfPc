// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Editor;

/// <summary>编辑组共用执行条：文件/输出文件夹选择与开始处理按钮。</summary>
public partial class EditorRunPanel : UserControl
{
    public EditorRunPanel()
    {
        InitializeComponent();
    }

    private EditorToolViewModel? ViewModel => DataContext as EditorToolViewModel;

    private void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 PDF 文件",
            Filter = "PDF 文件|*.pdf|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.InputFile = dialog.FileName;
        }
    }

    private void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        if (Directory.Exists(viewModel.OutputDir))
        {
            dialog.InitialDirectory = viewModel.OutputDir;
        }

        if (dialog.ShowDialog() == true)
        {
            viewModel.OutputDir = dialog.FolderName;
        }
    }
}
