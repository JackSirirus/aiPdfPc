// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.PageOrganizer;

public partial class RemovePage : UserControl
{
    public RemovePage()
    {
        InitializeComponent();
    }

    public RemovePage(Tool tool, EngineBridge? bridge) : this()
    {
        DataContext = new RemoveViewModel(bridge, tool);
    }
}
