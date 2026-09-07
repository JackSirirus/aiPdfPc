// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Security;

public partial class ComparePage : UserControl
{
    public ComparePage()
    {
        InitializeComponent();
    }

    private CompareViewModel ViewModel => (CompareViewModel)DataContext;

    private void Zone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FirstZone_Drop(object sender, DragEventArgs e)
    {
        var paths = ToolFileDialogs.GetDroppedPaths(e);
        if (paths.Count > 0)
        {
            ViewModel.SetFirstFile(paths[0]);
            if (paths.Count > 1)
            {
                ViewModel.SetSecondFile(paths[1]);
            }
        }
    }

    private void SecondZone_Drop(object sender, DragEventArgs e)
    {
        var paths = ToolFileDialogs.GetDroppedPaths(e);
        if (paths.Count > 0)
        {
            ViewModel.SetSecondFile(paths[0]);
        }
    }

    private void PickFirst_Click(object sender, RoutedEventArgs e)
    {
        var picked = ToolFileDialogs.PickFiles(this, ViewModel.Tool, multiple: false);
        if (picked.Count > 0)
        {
            ViewModel.SetFirstFile(picked[0]);
        }
    }

    private void PickSecond_Click(object sender, RoutedEventArgs e)
    {
        var picked = ToolFileDialogs.PickFiles(this, ViewModel.Tool, multiple: false);
        if (picked.Count > 0)
        {
            ViewModel.SetSecondFile(picked[0]);
        }
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ToolFileDialogs.PickFolder(this) is { } folder)
        {
            ViewModel.OutputDir = folder;
        }
    }
}
