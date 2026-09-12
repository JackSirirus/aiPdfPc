// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T5 关于·许可模态（AC-38.3 / Q12）：版本号 + AGPL-3.0-only + 许可要点 + 仓库 LICENSE/NOTICE/
// THIRD_PARTY_NOTICES.md 入口。代码构建（沿用 ConfirmationDialog 范式），视觉沿用 App.xaml design token。
// 不显示「检查更新」（AC-38.1：⚙ 菜单仅「设置 | 关于·许可」，本对话框亦然）。
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIPDF.Windows.Views.Settings;

/// <summary>
/// 关于·许可模态（代码构建、无 XAML）。展示版本与 AGPL-3.0-only 要点，并提供仓库许可文件入口。
/// </summary>
public sealed class AboutDialog : Window
{
    /// <summary>需要可打开的仓库许可文件（按 Q12：要点 + 入口）。</summary>
    private static readonly (string File, string Label)[] LicenseFiles =
    [
        ("LICENSE", "查看 LICENSE"),
        ("NOTICE", "查看 NOTICE"),
        ("THIRD_PARTY_NOTICES.md", "第三方声明"),
    ];

    private AboutDialog()
    {
        Title = "关于 AIPDF";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        WindowStyle = WindowStyle.ToolWindow;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Background = FindBrush("Brush.Window");
        Foreground = FindBrush("Brush.TextPrimary");

        Content = BuildContent();
    }

    /// <summary>弹出模态「关于·许可」。</summary>
    public static void Show(Window? owner)
    {
        var dialog = new AboutDialog();
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };

        // 品牌头：图标 + 名称 + 副标题
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = FindBrush("Brush.Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "A",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        var titlePanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titlePanel.Children.Add(new TextBlock
        {
            Text = "AIPDF · 本地 PDF 工作台",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = $"版本 {VersionText}",
            Style = FindStyle("PageSubtitle"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(titlePanel);
        root.Children.Add(header);

        // 许可
        root.Children.Add(SectionTitle("开源许可", top: 18));
        root.Children.Add(new TextBlock
        {
            Text = "GNU Affero General Public License v3.0 only (AGPL-3.0-only)",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("Brush.AccentText"),
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(Bullet("可自由使用、修改与分发（含商业用途）。"));
        root.Children.Add(Bullet("分发或通过网络提供服务时，须按许可提供对应源代码。"));
        root.Children.Add(Bullet("第三方组件保留各自许可与版权，不统一标为自有许可。"));
        root.Children.Add(Bullet("软件按原样提供，不提供适销性、特定用途等担保。"));

        root.Children.Add(SectionTitle("许可与第三方声明", top: 16));
        root.Children.Add(new TextBlock
        {
            Text = "完整条款见仓库根目录的 LICENSE、NOTICE 与 THIRD_PARTY_NOTICES.md。",
            Style = FindStyle("FieldHint"),
            Margin = new Thickness(0, 2, 0, 8),
        });

        var licenseButtons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (file, label) in LicenseFiles)
        {
            var button = new Button
            {
                Content = label,
                Style = FindStyle("SecondaryButton"),
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) => OpenRepositoryFile(file);
            licenseButtons.Children.Add(button);
        }
        root.Children.Add(licenseButtons);

        // 关闭
        var close = new Button
        {
            Content = "关闭",
            MinWidth = 96,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            Style = FindStyle("PrimaryButton"),
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        return root;
    }

    private static TextBlock SectionTitle(string text, double top) => new()
    {
        Text = text,
        Style = FindStyle("SectionTitle"),
        Margin = new Thickness(0, top, 0, 6),
    };

    private static TextBlock Bullet(string text) => new()
    {
        Text = "· " + text,
        FontSize = 12,
        Foreground = FindBrush("Brush.TextSecondary"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };

    // ------------------------------------------------------------------ 许可文件入口

    /// <summary>
    /// 打开仓库许可文件。开发构建下向上查找仓库根；用户安装版可能不含仓库文件，
    /// 此时回退打开所在目录并在找不到时给出提示（不崩、不静默）。
    /// </summary>
    private void OpenRepositoryFile(string fileName)
    {
        var path = FindRepositoryFile(fileName);
        if (path is null)
        {
            MessageBox.Show(this, $"未在本机找到 {fileName}。", "关于·许可", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开 {fileName}：{ex.Message}", "关于·许可", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>从应用目录向上逐级查找仓库文件；限定层数避免异常路径下长时间遍历。</summary>
    private static string? FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static string VersionText
    {
        get
        {
            var assembly = typeof(AboutDialog).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return informational;
            var version = assembly.GetName().Version;
            return version is null ? "0.1.0" : version.ToString(3);
        }
    }

    private static Style? FindStyle(string key) => Application.Current?.TryFindResource(key) as Style;

    private static Brush FindBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
