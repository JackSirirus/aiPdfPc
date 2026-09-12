// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T10 / C2 夜间模式单测：
//   1. ThemeManager 主题解析（light/dark/system/未知回退）；
//   2. token 集完整性（App.xaml 每项亮色 token 均须有 Brush.Dark.* 对应，且与 ThemeTokens.All 一致）；
//   3. token 审计（白名单外无硬编码主题色 / 静态主题引用；批注·内容色与代码构建对话框的颜色调用显式白名单）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public class ThemeManagerTests
{
    // ------------------------------------------------------------------ 主题解析

    [Theory]
    [InlineData("light", false, ThemeMode.Light)]
    [InlineData("light", true, ThemeMode.Light)]
    [InlineData("dark", false, ThemeMode.Dark)]
    [InlineData("dark", true, ThemeMode.Dark)]
    [InlineData("system", false, ThemeMode.Light)]
    [InlineData("system", true, ThemeMode.Dark)]
    [InlineData("DARK", false, ThemeMode.Dark)]
    [InlineData("System", true, ThemeMode.Dark)]
    public void ResolveMode_MapsSettingAndSystemPreference(string setting, bool systemDark, ThemeMode expected)
        => Assert.Equal(expected, ThemeManager.ResolveMode(setting, systemDark));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo")]
    [InlineData("light2")]
    public void ResolveMode_UnknownFallsBackToFollowSystem(string? setting)
    {
        Assert.Equal(ThemeMode.Light, ThemeManager.ResolveMode(setting, systemPrefersDark: false));
        Assert.Equal(ThemeMode.Dark, ThemeManager.ResolveMode(setting, systemPrefersDark: true));
    }

    [Fact]
    public void Apply_WithoutApplication_ResolvesModeWithoutThrowing()
    {
        var store = new SettingsStore(TempSettingsPath());
        using var manager = new ThemeManager(store);

        manager.Apply("dark");
        Assert.Equal(ThemeMode.Dark, manager.CurrentMode);

        manager.Apply("light");
        Assert.Equal(ThemeMode.Light, manager.CurrentMode);

        // 未知值 → 跟随系统（无 Application 时系统偏好回退亮色）
        manager.Apply("bogus");
        Assert.Equal(ThemeManager.ResolveMode("bogus", ThemeManager.IsSystemDark()), manager.CurrentMode);
    }

    // ------------------------------------------------------------------ token 集完整性

    private static readonly Regex BrushKeyRegex = new("x:Key=\"(Brush\\.[A-Za-z.]+)\"", RegexOptions.Compiled);

    [Fact]
    public void AppXaml_EveryLightTokenHasDarkCounterpart()
    {
        var appXaml = File.ReadAllText(Path.Combine(WindowsProjectDir(), "App.xaml"));
        var keys = BrushKeyRegex.Matches(appXaml)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var lightNames = keys
            .Where(k => !k.StartsWith(ThemeTokens.DarkPrefix, StringComparison.Ordinal))
            .Select(k => k.Substring(ThemeTokens.LightPrefix.Length))
            .ToHashSet(StringComparer.Ordinal);
        var darkNames = keys
            .Where(k => k.StartsWith(ThemeTokens.DarkPrefix, StringComparison.Ordinal))
            .Select(k => k.Substring(ThemeTokens.DarkPrefix.Length))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(lightNames);

        // ThemeTokens.All 与 App.xaml 亮色集合严格一致（防止代码/资源漂移）
        Assert.Equal(
            ThemeTokens.All.OrderBy(x => x, StringComparer.Ordinal),
            lightNames.OrderBy(x => x, StringComparer.Ordinal));

        // 每个亮色 token 必须有暗色对应
        var missingDark = lightNames.Where(name => !darkNames.Contains(name)).OrderBy(x => x).ToList();
        Assert.True(missingDark.Count == 0, "缺少暗色对应的亮色 token：" + string.Join(", ", missingDark));
    }

    // ------------------------------------------------------------------ token 审计（M6）

    /// <summary>
    /// 白名单：文档内容色 / 交互叠层色——不是主题色，不随主题改写。
    /// 新增白名单必须在此显式登记（审计绑定文件相对路径 + 颜色值）。
    /// </summary>
    private static readonly HashSet<(string File, string Color)> ContentColorWhitelist = new()
    {
        // 批注高亮色板（文档内容色，FR-13）
        ("Views/Reader/ReaderView.xaml", "#FFFF00"),
        ("Views/Reader/ReaderView.xaml", "#A6E22E"),
        ("Views/Reader/ReaderView.xaml", "#5AC8FA"),
        ("Views/Reader/ReaderView.xaml", "#FF9EC4"),
        // PdfPreviewControl 框选叠层（语义色，非主题）
        ("Controls/PdfPreviewControl.xaml", "#FF9500"),
        ("Controls/PdfPreviewControl.xaml", "#24FF9500"),
    };

    private static readonly Regex HexRegex = new("#[0-9A-Fa-f]{6,8}", RegexOptions.Compiled);
    private static readonly Regex XmlCommentRegex = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NamedColorRegex = new(
        "\"(White|Black|Transparent|Gray|LightGray|DarkGray|Silver|Red|Green|Blue|Orange|Yellow)\"",
        RegexOptions.Compiled);

    [Fact]
    public void XamlAudit_NoHardcodedThemeColorsOutsideWhitelist()
    {
        var root = WindowsProjectDir();
        var appXaml = Path.GetFullPath(Path.Combine(root, "App.xaml"));
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file) || string.Equals(file, appXaml, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 跳过构建产物与 token 唯一来源 App.xaml
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = XmlCommentRegex.Replace(File.ReadAllText(file), " ");

            foreach (Match match in HexRegex.Matches(text))
            {
                var color = match.Value.ToUpperInvariant();
                if (color == "#000000")
                {
                    continue; // 阴影色：主题无关
                }

                if (ContentColorWhitelist.Contains((relative, color)))
                {
                    continue;
                }

                offenders.Add($"{relative}: 硬编码色 {color}");
            }

            foreach (Match match in NamedColorRegex.Matches(text))
            {
                var named = match.Groups[1].Value;
                if (named is "White" or "Black" or "Transparent")
                {
                    continue; // 语义常量（如主色按钮文字 / 透明底）
                }

                offenders.Add($"{relative}: 硬编码命名色 {named}");
            }

            if (text.Contains("{StaticResource Brush.", StringComparison.Ordinal))
            {
                offenders.Add($"{relative}: 主题引用为 StaticResource（运行时切换不生效，须 DynamicResource）");
            }
        }

        Assert.True(offenders.Count == 0, "主题审计白名单外命中：\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// 代码构建对话框的显式白名单：键 =（文件相对路径, 归一化颜色调用）。
    /// 仅登记主题无关的语义色；归一化 = 去掉调用表达式内的全部空白字符。
    /// 新增白名单必须在此显式登记（审计绑定文件相对路径 + 表达式，不做泛化放宽）。
    /// </summary>
    private static readonly HashSet<(string File, string Call)> DialogCodeColorWhitelist = new()
    {
        // 向导全窗遮罩：主题无关的半透明叠层（亮/暗通用），不是主题色。
        ("Views/Wizard/WizardDialog.cs", "Color.FromArgb(0x66,0x10,0x14,0x1C)"),
    };

    private static readonly Regex ColorCallRegex = new(
        "Color\\.From(?:Argb|Rgb)\\([^)]*\\)", RegexOptions.Compiled);
    private static readonly Regex WhiteSpaceRegex = new("\\s+", RegexOptions.Compiled);

    [Fact]
    public void DialogCodeAudit_NoHardcodedThemeColors()
    {
        var root = WindowsProjectDir();
        string[] files =
        [
            "Views/Settings/SettingsDialog.cs",
            "Views/Settings/AboutDialog.cs",
            "Views/Wizard/WizardDialog.cs",
        ];

        // 语义常量色：主色按钮文字 / 透明底——不随主题改写，全局放行。
        var allowedBrushes = new HashSet<string>(StringComparer.Ordinal) { "White", "Black", "Transparent" };
        var brushRegex = new Regex("Brushes\\.([A-Za-z]+)", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var relative in files)
        {
            var text = XmlCommentRegex.Replace(File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))), " ");
            if (HexRegex.IsMatch(text))
            {
                offenders.Add($"{relative}: 含 #RRGGBB 硬编码色");
            }

            // Color.FromArgb / Color.FromRgb 一并审计：此前只查 FromRgb，遮罩类 FromArgb 调用逃过审计（review C1）。
            foreach (Match match in ColorCallRegex.Matches(text))
            {
                var call = WhiteSpaceRegex.Replace(match.Value, "");
                if (!DialogCodeColorWhitelist.Contains((relative, call)))
                {
                    offenders.Add($"{relative}: 硬编码颜色调用 {call}（须走 Brush.* token；主题无关语义色须显式登记白名单）");
                }
            }

            foreach (Match match in brushRegex.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (!allowedBrushes.Contains(name))
                {
                    offenders.Add($"{relative}: 硬编码 Brushes.{name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "对话框代码主题审计命中：\n" + string.Join("\n", offenders));
    }

    // ------------------------------------------------------------------ 辅助

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string WindowsProjectDir()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Sources", "AIPDF.Windows");
            if (File.Exists(Path.Combine(candidate, "App.xaml")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 AIPDF.Windows 工程目录（Sources/AIPDF.Windows/App.xaml）");
    }

    private static string TempSettingsPath()
        => Path.Combine(Path.GetTempPath(), $"aipdf-theme-{Guid.NewGuid():N}.json");
}
