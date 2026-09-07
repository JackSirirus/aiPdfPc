// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Editor;

/// <summary>添加水印工具页（文字或图片水印；参数提交给引擎渲染，不做所见即所得预览）。</summary>
public partial class WatermarkPage : UserControl
{
    public WatermarkPage()
    {
        InitializeComponent();
    }

    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WatermarkViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择图片水印",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.ImagePath = dialog.FileName;
        }
    }
}
