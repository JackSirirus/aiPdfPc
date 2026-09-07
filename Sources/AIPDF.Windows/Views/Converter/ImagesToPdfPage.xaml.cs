// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>图片转 PDF 工具页。</summary>
public partial class ImagesToPdfPage : UserControl
{
    public ImagesToPdfPage(ImagesToPdfViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
