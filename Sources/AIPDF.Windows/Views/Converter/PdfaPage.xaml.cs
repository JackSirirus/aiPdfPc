// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>PDF 转 PDF/A 工具页（依赖 LibreOffice）。</summary>
public partial class PdfaPage : UserControl
{
    public PdfaPage(PdfaViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
