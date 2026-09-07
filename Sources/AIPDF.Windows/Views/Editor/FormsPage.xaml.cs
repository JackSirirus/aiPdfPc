// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Editor;

/// <summary>PDF 表单工具页：填写既有字段或创建新字段（catalog 范围内，不自研字段树）。</summary>
public partial class FormsPage : UserControl
{
    public FormsPage()
    {
        InitializeComponent();
        Preview.RegionSelected += OnPreviewRegionSelected;
    }

    private void OnPreviewRegionSelected(object? sender, Controls.PdfRegionSelectedEventArgs e)
    {
        if (DataContext is FormsViewModel viewModel)
        {
            viewModel.ApplySelection(e);
        }
    }

    private void OnLoadPreviewClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FormsViewModel viewModel)
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
        if (DataContext is not FormsViewModel viewModel)
        {
            return;
        }

        viewModel.ClearSelection();
        Preview.ClearSelection();
    }
}
