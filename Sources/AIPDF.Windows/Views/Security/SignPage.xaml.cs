// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Security;

public partial class SignPage : UserControl
{
    public SignPage()
    {
        InitializeComponent();
    }

    private SignViewModel ViewModel => (SignViewModel)DataContext;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
        => ViewModel.AddDroppedFiles(ToolFileDialogs.PickFiles(this, ViewModel.Tool, multiple: false));

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
        => ViewModel.AddDroppedFiles(ToolFileDialogs.GetDroppedPaths(e));

    private void PickImage_Click(object sender, RoutedEventArgs e)
    {
        if (ToolFileDialogs.PickImage(this) is { } image)
        {
            ViewModel.SignatureImage = image;
        }
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
        => ViewModel.SignatureImage = "";

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ToolFileDialogs.PickFolder(this) is { } folder)
        {
            ViewModel.OutputDir = folder;
        }
    }
}
