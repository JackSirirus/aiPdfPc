// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 缩略图面板视图：可见区驱动渲染（ScrollChanged → VM.EnsureRange），点击跳页事件上行。
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Reader.Panels;

/// <summary>缩略图面板（DataContext = <see cref="ThumbPanelViewModel"/>）。</summary>
public partial class ThumbPanel : UserControl
{
    public ThumbPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // 虚拟化容器的滚动事件（含内部 ScrollViewer，handledEventsToo 兜底）
        // 显式构造委托：C# 10 方法组自然类型会推成 EventHandler<T>，与 RoutedEvent 的
        // ScrollChangedEventHandler 不符 → AddHandler 运行时 ArgumentException
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged), true);
        Loaded += (_, _) => RefreshVisibleRange();
    }

    private ThumbPanelViewModel? ViewModel => DataContext as ThumbPanelViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 新文档挂接：重置旧队列（防结果回填错册）并按当前可见区渲染
        if (e.OldValue is ThumbPanelViewModel old)
        {
            old.ResetQueue();
        }
        RefreshVisibleRange();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        RefreshVisibleRange();
    }

    /// <summary>把当前可见页区间交给 VM（可见区优先生成 + 锚点更新）。</summary>
    private void RefreshVisibleRange()
    {
        if (ViewModel is null || ThumbsList.Items.Count == 0)
        {
            return;
        }

        // 虚拟化容器：由 realized items 估算可见区间（overscan 区域略宽 = 预热）
        var firstVisible = -1;
        var lastVisible = -1;
        for (var i = 0; i < ThumbsList.Items.Count; i++)
        {
            if (ThumbsList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement)
            {
                if (firstVisible < 0)
                {
                    firstVisible = i;
                }
                lastVisible = i;
            }
            else if (firstVisible >= 0)
            {
                break; // realized 区段结束
            }
        }

        if (firstVisible < 0)
        {
            // 容器尚未布局：至少确保顶部若干页入队
            firstVisible = 0;
            lastVisible = Math.Min(7, ViewModel.TotalCount - 1);
        }
        if (lastVisible < firstVisible)
        {
            lastVisible = firstVisible;
        }

        ViewModel.EnsureRange(firstVisible, lastVisible + 2); // +2 页预热（下滚预备）
        ViewModel.FillAll();
    }

    private void OnThumbSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || ThumbsList.SelectedItem is not ThumbItemViewModel item)
        {
            return;
        }

        ThumbsList.SelectedIndex = -1; // 允许重复点击同一缩略图
        ViewModel.RequestNavigate(item.PageIndex);
    }

    /// <summary>
    /// 阅读位置联动：把缩略图列表滚动到当前阅读页。
    /// 不改变选中项（避免触发 <see cref="OnThumbSelected"/> 造成「缩略图↔阅读」回跳循环）；
    /// 已可见时 WPF ScrollIntoView 不会移动，无抖动。未挂接可视树时静默返回。
    /// </summary>
    public void ScrollToPage(int pageIndex)
    {
        if (!IsLoaded || ViewModel is null || pageIndex < 0 || pageIndex >= ThumbsList.Items.Count)
        {
            return;
        }

        ThumbsList.ScrollIntoView(ThumbsList.Items[pageIndex]);
    }
}
