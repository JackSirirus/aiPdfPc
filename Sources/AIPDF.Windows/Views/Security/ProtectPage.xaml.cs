// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Security;

public partial class ProtectPage : UserControl
{
    public ProtectPage()
    {
        InitializeComponent();
    }

    private ProtectViewModel ViewModel => (ProtectViewModel)DataContext;

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // PasswordBox 的值不进 VM 属性：委托按需取值，构建请求时才读取一次。
        ViewModel.NewPasswordProvider = () => SecretBox.Password;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开视觉树后不再持有 PasswordBox 引用。
        ViewModel.NewPasswordProvider = null;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
        => ViewModel.AddDroppedFiles(ToolFileDialogs.PickFiles(this, ViewModel.Tool, multiple: false));

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
