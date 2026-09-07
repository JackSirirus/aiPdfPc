// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>批量处理页签：队列构建、顺序执行、取消批次（对话框事件在 code-behind，逻辑在 VM）。</summary>
public partial class BatchView : UserControl
{
    public BatchView()
    {
        InitializeComponent();
    }

    private BatchToolViewModel ViewModel => (BatchToolViewModel)DataContext;

    private void OnAddBatchFiles(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.SelectedTool is null)
        {
            if (viewModel is not null)
            {
                viewModel.StatusText = "请先选择要批量执行的工具。";
            }

            return;
        }

        var filter = string.Join("|", viewModel.SelectedTool.Extensions
            .Select(extension => $"*.{extension}")
            .Concat(["所有支持的格式|" + string.Join(";", viewModel.SelectedTool.Extensions.Select(extension => "*." + extension))]));
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"添加文件（{viewModel.SelectedTool.Name}）",
            Filter = filter,
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.AddFiles(dialog.FileNames);
        }
    }

    private void OnBrowseBatchOutput(object sender, RoutedEventArgs e)
    {
        if (WorkflowPage.PickFolder() is { } folder && ViewModel is not null)
        {
            ViewModel.OutputDir = folder;
        }
    }

    private async void OnRunBatch(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RunAsync();
        }
    }

    private void OnCancelBatch(object sender, RoutedEventArgs e)
        => ViewModel?.Cancel();
}
