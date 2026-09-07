// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.PageOrganizer;

/// <summary>
/// 工具页统一骨架：文件输入（拖拽 + 浏览）→ 参数（各工具页注入 OptionsContent）→ 输出目录 → 执行/取消 → 进度与结果。
/// DataContext 必须是 PageOrganizerViewModelBase（各工具页在构造时设置）。
/// </summary>
public partial class ToolPageShell : UserControl
{
    public static readonly DependencyProperty OptionsContentProperty = DependencyProperty.Register(
        nameof(OptionsContent), typeof(object), typeof(ToolPageShell), new PropertyMetadata(null));

    public static readonly DependencyProperty IsFileDragOverProperty = DependencyProperty.Register(
        nameof(IsFileDragOver), typeof(bool), typeof(ToolPageShell), new PropertyMetadata(false));

    public ToolPageShell()
    {
        InitializeComponent();
    }

    /// <summary>参数区内容（各工具页注入自己的表单；内容绑定继承页面 DataContext）。</summary>
    public object? OptionsContent
    {
        get => GetValue(OptionsContentProperty);
        set => SetValue(OptionsContentProperty, value);
    }

    /// <summary>拖拽悬停高亮（XAML Style 触发器消费）。</summary>
    public bool IsFileDragOver
    {
        get => (bool)GetValue(IsFileDragOverProperty);
        set => SetValue(IsFileDragOverProperty, value);
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        IsFileDragOver = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = IsFileDragOver ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropZoneDragLeave(object sender, DragEventArgs e)
    {
        IsFileDragOver = false;
        e.Handled = true;
    }

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        IsFileDragOver = false;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            && DataContext is PageOrganizerViewModelBase viewModel)
        {
            viewModel.AddFiles(paths);
        }

        e.Handled = true;
    }
}
