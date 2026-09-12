// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T5 设置模态：承载 T1 的 7 项设置（FR-38 + 裁决 Q6）——默认缩放 / 默认左栏面板 / 最近文件上限 /
// 主题 / 打印 DPI / 默认输出目录 / 默认渲染 DPI。保存即时写盘并归一化（AC-39.1/39.2）；
// 「仅对新打开文档生效」的注入边界由宿主 MainWindow 保证，本对话框只负责读写设置。
// 代码构建（沿用 Views/Reader/ConfirmationDialog 范式，避免 XAML wpftmp/资源前向引用坑），
// 视觉沿用 App.xaml 的 design token（Brush.* / FieldLabel / PrimaryButton 等），与既有模态一致。
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIPDF.Windows.Services;

namespace AIPDF.Windows.Views.Settings;

/// <summary>
/// 设置模态（代码构建、无 XAML）。保存后 <see cref="SavedSettings"/> 为已归一化的落盘设置；
/// 取消 / 关闭返回 null。密码设置不存在（PRD 4.3.3 红线）。
/// </summary>
public sealed class SettingsDialog : Window
{
    private readonly SettingsStore _store;
    private readonly ThemeManager? _themeManager;

    private readonly ComboBox _zoomBox = NewCombo();
    private readonly ComboBox _panelBox = NewCombo();
    private readonly Slider _recentSlider = new();
    private readonly TextBlock _recentValue = new();
    private readonly ComboBox _themeBox = NewCombo();
    private readonly TextBox _printDpiBox = new();
    private readonly TextBox _outputDirBox = new();
    private readonly TextBox _renderDpiBox = new();

    /// <summary>保存成功后的已归一化设置；取消 / 关闭为 null。</summary>
    public AppSettings? SavedSettings { get; private set; }

    public SettingsDialog(SettingsStore store, ThemeManager? themeManager = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _themeManager = themeManager;

        Title = "设置";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 560;
        WindowStyle = WindowStyle.ToolWindow;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Background = FindBrush("Brush.Window");
        Foreground = FindBrush("Brush.TextPrimary");

        Content = BuildContent(_store.Load());
    }

    // ------------------------------------------------------------------ 布局

    private UIElement BuildContent(AppSettings current)
    {
        var rows = new Grid { Margin = new Thickness(24, 20, 24, 8) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 页标题
        var title = new TextBlock
        {
            Text = "设置",
            Style = FindStyle("PageTitle"),
        };
        rows.Children.Add(title);
        Grid.SetRow(title, row);
        Grid.SetColumnSpan(title, 2);

        row++;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var subtitle = new TextBlock
        {
            Text = "修改保存后，仅对随后新打开的文档生效；已打开文档不受影响。",
            Style = FindStyle("PageSubtitle"),
            Margin = new Thickness(0, 4, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        rows.Children.Add(subtitle);
        Grid.SetRow(subtitle, row);
        Grid.SetColumnSpan(subtitle, 2);

        // 1. 默认缩放（FR-38 项 1）
        AddComboItem(_zoomBox, "适配宽度", AppSettings.ZoomFitWidth);
        AddComboItem(_zoomBox, "适配页面", AppSettings.ZoomFitPage);
        foreach (var percent in AppSettings.ZoomLadderPercent)
        {
            AddComboItem(_zoomBox, $"{percent}%", percent.ToString(CultureInfo.InvariantCulture));
        }
        SelectComboValue(_zoomBox, current.DefaultZoom);
        AddRow(rows, ref row, "默认缩放",
            "新打开文档的初始缩放；文件已有阅读记录时以阅读记录为准。", _zoomBox);

        // 2. 默认左栏面板（FR-38 项 2）
        AddComboItem(_panelBox, "缩略图", AppSettings.PanelThumbs);
        AddComboItem(_panelBox, "目录", AppSettings.PanelOutline);
        AddComboItem(_panelBox, "书签", AppSettings.PanelBookmarks);
        SelectComboValue(_panelBox, current.DefaultPanelKey);
        AddRow(rows, ref row, "默认左栏面板",
            "新打开文档的初始面板；文件已有阅读记录时以阅读记录为准（搜索面板由 Ctrl+F 激活，不在此列）。", _panelBox);

        // 3. 最近文件上限（FR-38 项 3）
        _recentSlider.Minimum = AppSettings.RecentFilesLimitMin;
        _recentSlider.Maximum = AppSettings.RecentFilesLimitMax;
        _recentSlider.TickFrequency = 1;
        _recentSlider.IsSnapToTickEnabled = true;
        _recentSlider.Width = 220;
        _recentSlider.VerticalAlignment = VerticalAlignment.Center;
        _recentSlider.Value = current.RecentFilesLimit;
        _recentValue.Text = current.RecentFilesLimit.ToString(CultureInfo.InvariantCulture);
        _recentValue.Style = FindStyle("FieldLabel");
        _recentValue.Margin = new Thickness(12, 0, 0, 0);
        _recentValue.MinWidth = 28;
        _recentSlider.ValueChanged += (_, e) =>
            _recentValue.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        var recentRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        recentRow.Children.Add(_recentSlider);
        recentRow.Children.Add(_recentValue);
        AddRow(rows, ref row, "最近文件上限",
            $"起始页「最近打开」保留条数（{AppSettings.RecentFilesLimitMin}–{AppSettings.RecentFilesLimitMax}）。", recentRow);

        // 4. 主题（Q6/C2）
        AddComboItem(_themeBox, "跟随系统", AppSettings.ThemeSystem);
        AddComboItem(_themeBox, "亮色", AppSettings.ThemeLight);
        AddComboItem(_themeBox, "暗色", AppSettings.ThemeDark);
        SelectComboValue(_themeBox, current.Theme);
        AddRow(rows, ref row, "主题", "外观主题：亮色 / 暗色 / 跟随系统；保存后立即生效（无需重启）。", _themeBox);

        // 5. 打印 DPI（Q6/Q11）
        _printDpiBox.Text = current.PrintDpi.ToString(CultureInfo.InvariantCulture);
        ApplyFieldStyle(_printDpiBox, 120);
        AddRow(rows, ref row, "打印 DPI",
            $"打印渲染 DPI（{AppSettings.PrintDpiMin}–{AppSettings.PrintDpiMax}，默认 {AppSettings.PrintDpiDefault}）。", _printDpiBox);

        // 6. 默认输出目录（Q6）
        _outputDirBox.Text = current.DefaultOutputDir;
        ApplyFieldStyle(_outputDirBox, double.NaN);
        var browse = new Button
        {
            Content = "浏览…",
            Style = FindStyle("SecondaryButton"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        browse.Click += OnBrowseOutputDirClick;
        var outputRow = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(browse, Dock.Right);
        outputRow.Children.Add(browse);
        outputRow.Children.Add(_outputDirBox);
        AddRow(rows, ref row, "默认输出目录",
            "工具页输出目录的初始值；留空 = 沿用页面「选择输出文件夹」（引擎仍每次生成新任务目录、不覆盖源文件）。", outputRow);

        // 7. 默认渲染 DPI（Q6）
        _renderDpiBox.Text = current.DefaultRenderDpi.ToString("0.#", CultureInfo.InvariantCulture);
        ApplyFieldStyle(_renderDpiBox, 120);
        AddRow(rows, ref row, "默认渲染 DPI",
            $"阅读渲染基准 DPI（{AppSettings.RenderDpiMin:0}–{AppSettings.RenderDpiMax:0}，默认 {AppSettings.RenderDpiDefault:0}）；不改变高倍率渲染阈值，也不参与打印。",
            _renderDpiBox);

        // 底部按钮
        var save = new Button { Content = "保存", MinWidth = 96, IsDefault = true, Style = FindStyle("PrimaryButton") };
        save.Click += OnSaveClick;
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 88,
            IsCancel = true,
            Margin = new Thickness(0, 0, 10, 0),
            Style = FindStyle("SecondaryButton"),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 8, 24, 20),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(rows);
        return root;
    }

    /// <summary>向设置网格追加一行：左列标签（含提示），右列控件。</summary>
    private static void AddRow(Grid grid, ref int row, string label, string hint, FrameworkElement control)
    {
        row++;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelPanel = new StackPanel
        {
            Margin = new Thickness(0, 10, 20, 10),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 200,
        };
        labelPanel.Children.Add(new TextBlock { Text = label, Style = FindStyle("FieldLabel") });
        labelPanel.Children.Add(new TextBlock
        {
            Text = hint,
            Style = FindStyle("FieldHint"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetRow(labelPanel, row);
        Grid.SetColumn(labelPanel, 0);
        grid.Children.Add(labelPanel);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    // ------------------------------------------------------------------ 交互

    private void OnBrowseOutputDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认输出目录" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _outputDirBox.Text = dialog.FolderName;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            DefaultZoom = SelectedValue(_zoomBox),
            DefaultPanelKey = SelectedValue(_panelBox),
            RecentFilesLimit = (int)Math.Round(_recentSlider.Value),
            Theme = SelectedValue(_themeBox),
            PrintDpi = ParseInt(_printDpiBox.Text, AppSettings.PrintDpiDefault),
            DefaultOutputDir = _outputDirBox.Text,
            DefaultRenderDpi = ParseDouble(_renderDpiBox.Text, AppSettings.RenderDpiDefault),
        };

        try
        {
            _store.Save(settings); // Save 内部归一化；随后 Load 保证 SavedSettings 为落盘后的规范值
            SavedSettings = _store.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"设置保存失败：{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // V3 T10 / C2：主题保存后即时应用（SettingsStore.Theme 为持久化真相源；不改已打开文档内容）。
        _themeManager?.ApplyFromSettings();

        DialogResult = true;
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ParseDouble(string? text, double fallback)
        => double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    // ------------------------------------------------------------------ 控件辅助

    private static ComboBox NewCombo() => new()
    {
        Style = FindStyle("FieldComboBox"),
        Width = 220,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static void ApplyFieldStyle(TextBox box, double width)
    {
        box.Style = FindStyle("FieldTextBox");
        if (!double.IsNaN(width))
        {
            box.Width = width;
            box.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private static void AddComboItem(ComboBox box, string text, string value)
        => box.Items.Add(new ComboBoxItem { Content = text, Tag = value });

    private static void SelectComboValue(ComboBox box, string value)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, value, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }

        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string SelectedValue(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>从 App 级资源取样式；缺失返回 null（控件回退默认外观，不崩）。</summary>
    private static Style? FindStyle(string key) => Application.Current?.TryFindResource(key) as Style;

    /// <summary>从 App 级资源取画刷；缺失返回默认（Transparent），避免对话框构造期拿不到资源时崩溃。</summary>
    private static Brush FindBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
