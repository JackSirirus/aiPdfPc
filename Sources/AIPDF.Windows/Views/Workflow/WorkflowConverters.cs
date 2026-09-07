// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace AIPDF.Windows.Views.Workflow;

/// <summary>布尔取反（按钮随 IsRunning 反向启用）。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false or null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false or null;
}

/// <summary>catalog toggle 字段与字符串 "true"/"false" 的双向换算。</summary>
public sealed class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as string is "true" or "True" or "1";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "true" : "false";
}

/// <summary>路径 → 文件名（列表标签用，完整路径放 ToolTip）。</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path && path.Length > 0 ? Path.GetFileName(path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed（参数 "Inverse" 时 null → Visible）。主窗口详情区三态切换用。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null
            ? (parameter is "Inverse" ? Visibility.Visible : Visibility.Collapsed)
            : (parameter is "Inverse" ? Visibility.Collapsed : Visibility.Visible);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
