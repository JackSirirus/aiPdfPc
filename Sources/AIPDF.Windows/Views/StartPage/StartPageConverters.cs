// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Globalization;
using System.Windows.Data;

namespace AIPDF.Windows.Views.StartPage;

/// <summary>bool 取反 → 显隐（用于「清除记录」入口在确认条出现时隐藏）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>工具名首字母徽标：取名称首个字符（AIPDF 应用「A」徽标的同款视觉语言）。</summary>
public sealed class InitialLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string name && name.Length > 0 ? name[..1] : "?";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
