// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>处理记录页签：历史列表与 CSV 导出（保存对话框在 code-behind，导出逻辑在 VM）。</summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private HistoryToolViewModel? ViewModel => DataContext as HistoryToolViewModel;

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出处理记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"AIPDF处理记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.ExportCsv(dialog.FileName);
        }
    }
}
