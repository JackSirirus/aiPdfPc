// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;

namespace AIPDF.Windows.Views.Converter;

/// <summary>转换组共享执行栏；DataContext 由宿主工具页注入（ConverterToolViewModelBase）。</summary>
public partial class ToolRunBar : UserControl
{
    public ToolRunBar()
    {
        InitializeComponent();
    }
}
