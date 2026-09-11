// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 目录面板视图：树形展示，点击跳页；数据由 OutlinePanelViewModel 后台加载。
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Reader.Panels;

/// <summary>目录面板（DataContext = <see cref="OutlinePanelViewModel"/>）；只读不编辑。</summary>
public partial class OutlinePanel : UserControl
{
    public OutlinePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private OutlinePanelViewModel? ViewModel => DataContext as OutlinePanelViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            _ = ViewModel.LoadAsync(); // 幂等；后台线程解析（大目录不阻塞 UI）
        }
    }

    private void OnOutlineSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // 注：TreeView.SelectedItem 只读，无法像 ListBox 一样清除选中以支持重复点击同一节点；
        // 重复跳转同一目录项由阅读视图滚动行为兜底（T7 接线）。
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.NavigateTo(e.NewValue as OutlineNodeViewModel);
    }
}
