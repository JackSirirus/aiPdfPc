// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;

namespace AIPDF.Windows.Views.Converter;

/// <summary>转换组共享文件列表面板；DataContext 由宿主工具页注入（ConverterToolViewModelBase）。</summary>
public partial class FileListPanel : UserControl
{
    public FileListPanel()
    {
        InitializeComponent();
    }
}
