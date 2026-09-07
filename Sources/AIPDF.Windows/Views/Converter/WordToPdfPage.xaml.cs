// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>Word 转 PDF 工具页。</summary>
public partial class WordToPdfPage : UserControl
{
    public WordToPdfPage(WordToPdfViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
