// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Tools;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>通用参数表单：绑定 OptionFieldViewModel 集合，按 Kind 呈现对应编辑器。</summary>
public partial class OptionsEditor : UserControl
{
    public OptionsEditor()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IEnumerable), typeof(OptionsEditor), new PropertyMetadata(null));

    public IEnumerable? Options
    {
        get => (IEnumerable?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>PasswordBox 不支持直接双向绑定：输入事件回写字段值（密码仅驻内存，经引擎 stdin 传递）。</summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is OptionFieldViewModel field && field.IsSecret)
        {
            field.Value = box.Password;
        }
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OptionFieldViewModel field })
        {
            return;
        }

        var dialog = new OpenFileDialog { CheckFileExists = true, Title = field.Label };
        if (dialog.ShowDialog() == true)
        {
            field.Value = dialog.FileName;
        }
    }
}
