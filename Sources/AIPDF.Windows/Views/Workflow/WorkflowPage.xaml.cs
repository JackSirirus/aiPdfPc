// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views.Wizard;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>
/// 工作流程工具页：流程步骤+预设 / 批量处理 / 处理记录 三页标签。
/// V3 T8：页面即宿主的 DataContext（复合三 VM），故按向导契约转发到步骤 VM（<see cref="StepsVm"/>），
/// 使向导宿主能经 <c>DataContext as IWizardToolAdapter</c> 发现 workflow 适配器。
/// </summary>
public partial class WorkflowPage : UserControl, IWizardToolAdapter
{
    public WorkflowPage()
    {
        InitializeComponent();
    }

    /// <summary>应用入口构造：由导航注册表（WorkflowPageRegistration）调用。</summary>
    public WorkflowPage(EngineBridge? bridge, IReadOnlyList<Tool> catalogTools, Tool tool) : this()
    {
        Tool = tool;
        StepsVm = new WorkflowToolViewModel(bridge, catalogTools, catalogTool: tool);
        BatchVm = new BatchToolViewModel(bridge, catalogTools);
        HistoryVm = new HistoryToolViewModel();
        DataContext = this;
    }

    // ---- V3 T8 向导契约转发（页面 DataContext = this，宿主发现的是页面而非复合 VM） ----

    string? IWizardToolAdapter.ValidateParameters(IReadOnlyList<string> stagedFiles)
        => StepsVm.ValidateParameters(stagedFiles);

    IReadOnlyList<WizardSubmitItem> IWizardToolAdapter.BuildSubmitItems(IReadOnlyList<string> stagedFiles)
        => StepsVm.BuildSubmitItems(stagedFiles);

    Task<WizardItemResult> IWizardToolAdapter.ExecuteItemAsync(WizardSubmitItem item, CancellationToken token)
        => StepsVm.ExecuteItemAsync(item, token);

    IReadOnlyList<string> IWizardToolAdapter.AdditionalStepTitles() => StepsVm.AdditionalStepTitles();

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
