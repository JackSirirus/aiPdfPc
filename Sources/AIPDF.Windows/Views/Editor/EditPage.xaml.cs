// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Editor;

/// <summary>编辑 PDF 工具页（文字/图片/矩形/高亮/手绘，catalog 之外不新增样式）。</summary>
public partial class EditPage : UserControl
{
    public EditPage()
    {
        InitializeComponent();
        Preview.RegionSelected += OnPreviewRegionSelected;
    }

    private void OnPreviewRegionSelected(object? sender, Controls.PdfRegionSelectedEventArgs e)
    {
        if (DataContext is EditViewModel viewModel)
        {
            viewModel.ApplySelection(e);
        }
    }

    private void OnLoadPreviewClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EditViewModel viewModel)
        {
            return;
        }

        try
        {
            Preview.OpenPdf(viewModel.InputFile);
            viewModel.ErrorText = "";
        }
        catch (Exception error)
        {
            viewModel.ErrorText = $"无法打开预览：{error.Message}";
        }
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EditViewModel viewModel)
        {
            return;
        }

        viewModel.ClearSelection();
        Preview.ClearSelection();
    }

    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EditViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择图片文件",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.ImagePath = dialog.FileName;
        }
    }
}
