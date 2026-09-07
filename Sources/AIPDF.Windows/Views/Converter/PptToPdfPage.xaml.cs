// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>Word 杞?PDF 宸ュ叿椤点€?/summary>
public partial class PptToPdfPage : UserControl
{
    public PptToPdfPage(PptToPdfViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
