// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>HTML 转 PDF 工具页（本地文件或网址，网址优先）。</summary>
public partial class HtmlToPdfPage : UserControl
{
    public HtmlToPdfPage(HtmlToPdfViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
