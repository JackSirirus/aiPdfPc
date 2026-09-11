// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 搜索槽位占位视图：无逻辑；T8 接管为真实搜索面板（PRD 3.3 FR-09/FR-10）。
using System.Windows.Controls;

namespace AIPDF.Windows.Views.Reader.Panels;

/// <summary>搜索面板槽位（T8 填充）。</summary>
public partial class SearchSlotPanel : UserControl
{
    public SearchSlotPanel()
    {
        InitializeComponent();
    }
}
