// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 全文搜索面板视图：仅做视图职责（焦点、命中行点击、关闭按钮上行），业务在 SearchPanelViewModel。
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Reader.Panels;

/// <summary>搜索面板（DataContext = <see cref="SearchPanelViewModel"/>）。</summary>
public partial class SearchPanel : UserControl
{
    public SearchPanel()
    {
        InitializeComponent();
    }

    /// <summary>Ctrl+F / 打开面板后聚焦搜索框并全选（便于替换关键词）。</summary>
    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => (DataContext as SearchPanelViewModel)?.RequestClose();

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SearchPanelViewModel viewModel && ResultList.SelectedItem is SearchResultItem item)
        {
            ResultList.SelectedItem = null; // 允许重复点击同一命中
            viewModel.ActivateHitCommand.Execute(item);
        }
    }

    /// <summary>V4 T14：跨文档分组的命中点击（与扁平列表共用同一激活入口）。</summary>
    private void OnGroupedResultClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SearchPanelViewModel viewModel
            && sender is FrameworkElement { Tag: SearchResultItem item })
        {
            viewModel.ActivateHitCommand.Execute(item);
        }
    }

    /// <summary>V4 T14：搜索范围切换（当前文档 / 所有打开的文档）。</summary>
    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SearchPanelViewModel viewModel)
        {
            viewModel.Scope = ScopeBox.SelectedIndex == 1
                ? SearchScope.AllOpenDocuments
                : SearchScope.CurrentDocument;
        }
    }
}
