// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>流程工具组页面：流程步骤+预设 / 批量处理 / 处理记录 三个页签。</summary>
public partial class WorkflowPage : UserControl
{
    public WorkflowPage()
    {
        InitializeComponent();
    }

    /// <summary>应用入口构造：由导航注册表（WorkflowPageRegistration）调用。</summary>
    public WorkflowPage(EngineBridge? bridge, IReadOnlyList<Tool> catalogTools, Tool tool) : this()
    {
        Tool = tool;
        StepsVm = new WorkflowToolViewModel(bridge, catalogTools);
        BatchVm = new BatchToolViewModel(bridge, catalogTools);
        HistoryVm = new HistoryToolViewModel();
        DataContext = this;
    }

    /// <summary>目录中的流程工具条目（页头标题与说明）。</summary>
    public Tool Tool { get; } = new() { Name = "工作流程", Subtitle = "", Note = "" };

    public WorkflowToolViewModel StepsVm { get; } = new(null, []);

    public BatchToolViewModel BatchVm { get; } = new(null, []);

    public HistoryToolViewModel HistoryVm { get; } = new();

    private void OnAddWorkflowFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "添加要处理的 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
        };
        if (dialog.ShowDialog() == true)
        {
            StepsVm.AddFiles(dialog.FileNames);
        }
    }

    private void OnBrowseWorkflowOutput(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is { } folder)
        {
            StepsVm.OutputDir = folder;
        }
    }

    private void OnWorkflowPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            StepsVm.Password = box.Password;
        }
    }

    private async void OnExecuteWorkflow(object sender, RoutedEventArgs e)
        => await StepsVm.ExecuteAsync();

    private void OnCancelWorkflow(object sender, RoutedEventArgs e)
        => StepsVm.Cancel();

    /// <summary>输出文件夹选择（.NET 8 WPF 自带 OpenFolderDialog）。</summary>
    internal static string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择输出文件夹",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
