// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.StartPage;

/// <summary>
/// 起始页视图（PRD 3.10）：只做视图职责——文件对话框、拖拽事件校验、Toast 动效与点击转发；
/// 列表逻辑与状态在 <see cref="StartPageViewModel"/>（纯 VM，可测）。
/// </summary>
public partial class StartPageView : UserControl
{
    /// <summary>Toast 自动消隐时长（轻量内联提示，不做常驻状态栏）。</summary>
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(2.8);

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    public StartPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
    }

    public StartPageViewModel? ViewModel => DataContext as StartPageViewModel;

    private void HookViewModel()
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StartPageViewModel.ToastMessage))
            {
                ShowToast(ViewModel.ToastMessage);
            }
        };
    }

    // ---- 打开入口（FR-33） ----

    /// <summary>打开按钮：.pdf 过滤文件对话框（与顶栏 ＋ 同一过滤串）。</summary>
    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 PDF 文档",
            Filter = "PDF 文档 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        ViewModel?.OpenPickedFile(dialog.FileName);
    }

    // ---- 拖拽落区（FR-33 / ST-07） ----

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        // 非 PDF / 文件夹不给放置效果；提示文案在 Drop 时经 Toast 呈现【ST-07】
        e.Effects = ContainsPdfFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && ViewModel is not null)
        {
            ViewModel.HandleDroppedPaths(paths);
        }

        e.Handled = true;
    }

    private static bool ContainsPdfFile(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop)
           && data.GetData(DataFormats.FileDrop) is string[] paths
           && paths.Any(path => File.Exists(path)
               && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase));

    // ---- 最近文件卡片（FR-34 / ST-05） ----

    private void OnRecentCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentFileItem item })
        {
            ViewModel?.TryOpenRecent(item);
        }
    }

    private void OnRemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentFileItem item)
        {
            ViewModel?.RemoveRecent(item);
        }
    }

    /// <summary>清空需二次确认（PRD FR-34）：入口 → 内联确认条 → 清除/取消。</summary>
    private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel?.RequestClear();

    private void OnConfirmClearClick(object sender, RoutedEventArgs e) => ViewModel?.ConfirmClear();

    private void OnCancelClearClick(object sender, RoutedEventArgs e) => ViewModel?.CancelClear();

    // ---- 工具入口（FR-35；行为统一走 T5 临时工具标签流，T7 重路由单文件工具到右抽屉） ----

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Models.Tool tool)
        {
            ViewModel?.OpenTool(tool);
        }
    }

    private void OnToggleAllToolsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.IsAllToolsExpanded = !ViewModel.IsAllToolsExpanded;
        }
    }

    // ---- Toast（ST-07 / ST-05） ----

    private void ShowToast(string? message)
    {
        _toastTimer?.Stop();
        if (string.IsNullOrEmpty(message))
        {
            ToastHost.Visibility = Visibility.Collapsed;
            return;
        }

        ToastText.Text = message;
        ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.Zero));
        ToastHost.Visibility = Visibility.Visible;

        _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = ToastDuration };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastHost.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }
}
