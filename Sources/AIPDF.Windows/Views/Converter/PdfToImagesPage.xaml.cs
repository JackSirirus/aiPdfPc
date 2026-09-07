// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>PDF 转图片工具页。</summary>
public partial class PdfToImagesPage : UserControl
{
    public PdfToImagesPage(PdfToImagesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
