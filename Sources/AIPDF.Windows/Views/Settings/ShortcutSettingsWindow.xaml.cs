// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T12 快捷键设置窗口：仅做宿主职责（承载面板 + 保存/取消），逻辑在 VM 与面板。
using System.Windows;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Settings;

/// <summary>快捷键设置窗口（DataContext = <see cref="ShortcutSettingsViewModel"/>）。</summary>
public partial class ShortcutSettingsWindow : Window
{
    public ShortcutSettingsWindow(ShortcutSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();

        DataContext = viewModel;
        Panel.DataContext = viewModel;
    }

    /// <summary>是否已保存（取消为 false）。</summary>
    public bool Saved { get; private set; }

    /// <summary>保存后是否仍存在冲突（宿主据此提示，不静默）。</summary>
    public bool HasConflictsAfterSave { get; private set; }

    /// <summary>冲突摘要（无冲突为空串）。</summary>
    public string ConflictSummary { get; private set; } = string.Empty;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShortcutSettingsViewModel viewModel)
        {
            HasConflictsAfterSave = !viewModel.Save();
            ConflictSummary = viewModel.ConflictSummary;
            Saved = true;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
