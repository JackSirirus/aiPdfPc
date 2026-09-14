// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 批量翻译工具页 code-behind：只做拖拽转发；其余交互（添加 / 移除 / 浏览 / 执行 / 重试 / 按需打开）
// 全在 TranslationBatchViewModel 上，页面本身无副作用逻辑。
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Translation;

/// <summary>批量翻译工具页（DataContext = <see cref="TranslationBatchViewModel"/>）。</summary>
public partial class TranslationBatchPage : UserControl
{
    public TranslationBatchViewModel ViewModel { get; }

    public TranslationBatchPage(EngineBridge? bridge, Tool tool)
    {
        InitializeComponent();
        ViewModel = new TranslationBatchViewModel(bridge, tool);
        DataContext = ViewModel;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ViewModel.AddFiles(files);
        }

        e.Handled = true;
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
