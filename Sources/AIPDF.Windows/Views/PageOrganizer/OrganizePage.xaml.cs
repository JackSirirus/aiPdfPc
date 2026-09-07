// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.PageOrganizer;

public partial class OrganizePage : UserControl
{
    public OrganizePage()
    {
        InitializeComponent();
    }

    public OrganizePage(Tool tool, EngineBridge? bridge) : this()
    {
        DataContext = new OrganizeViewModel(bridge, tool);
    }
}
