// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Editor;

/// <summary>裁剪工具页：嵌入 T13 预览控件，框选区域原样转为 x/y/width/height 参数。</summary>
public partial class CropPage : UserControl
{
    public CropPage()
    {
        InitializeComponent();
        Preview.RegionSelected += OnPreviewRegionSelected;
    }

    private void OnPreviewRegionSelected(object? sender, Controls.PdfRegionSelectedEventArgs e)
    {
        if (DataContext is CropViewModel viewModel)
        {
            viewModel.ApplySelection(e);
        }
    }

    private void OnLoadPreviewClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CropViewModel viewModel)
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
        if (DataContext is not CropViewModel viewModel)
        {
            return;
        }

        viewModel.ClearSelection();
        Preview.ClearSelection();
    }
}
