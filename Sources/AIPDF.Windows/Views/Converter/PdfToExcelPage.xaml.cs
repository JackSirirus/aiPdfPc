// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>PDF 转 Excel 工具页。</summary>
public partial class PdfToExcelPage : UserControl
{
    public PdfToExcelPage(PdfToExcelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
