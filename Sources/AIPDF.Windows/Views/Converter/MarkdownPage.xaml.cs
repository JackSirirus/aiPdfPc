// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>PDF 转 Markdown 工具页。</summary>
public partial class MarkdownPage : UserControl
{
    public MarkdownPage(MarkdownViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
