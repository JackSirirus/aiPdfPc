// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 书签面板视图：列表点击跳转；添加/删除命令在 BookmarkPanelViewModel（纯 VM 可测）。
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Reader.Panels;

/// <summary>书签面板（DataContext = <see cref="BookmarkPanelViewModel"/>）。</summary>
public partial class BookmarkPanel : UserControl
{
    public BookmarkPanel()
    {
        InitializeComponent();
    }

    private BookmarkPanelViewModel? ViewModel => DataContext as BookmarkPanelViewModel;

    private void OnBookmarkSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || BookmarkList.SelectedItem is not BookmarkItemViewModel item)
        {
            return;
        }

        BookmarkList.SelectedItem = null; // 允许重复点击同一书签
        ViewModel.NavigateToCommand.Execute(item);
    }
}
