// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Security;

public partial class RedactPage : UserControl
{
    public RedactPage()
    {
        InitializeComponent();
    }

    private RedactViewModel ViewModel => (RedactViewModel)DataContext;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
        => ViewModel.AddDroppedFiles(ToolFileDialogs.PickFiles(this, ViewModel.Tool, multiple: true));

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
        => ViewModel.AddDroppedFiles(ToolFileDialogs.GetDroppedPaths(e));

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ToolFileDialogs.PickFolder(this) is { } folder)
        {
            ViewModel.OutputDir = folder;
        }
    }
}
