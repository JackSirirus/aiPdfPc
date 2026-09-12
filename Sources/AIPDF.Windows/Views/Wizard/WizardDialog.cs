// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T4 向导模态宿主（代码构建，无 XAML）：居中模态 + 全窗遮罩 + 文件暂存 / 步骤导航 / 内联校验 / 结果。
// 代码构建刻意规避 XAML 的 wpftmp 编译与 StaticResource 前向引用（风险 11）。
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Wizard;

/// <summary>
/// 居中模态向导窗口。承载 <see cref="WizardViewModel"/>：文件暂存（添加/拖入/排序/移除，当前文档置顶标记）、
/// 分步导航（默认 3 步；workflow 多步）、内联校验【ST-25】、批量提交执行结果。
/// Esc / 点遮罩 = 取消且草稿丢弃【ST-26】；提交完成后 Esc / 完成 = 关闭（结果由宿主处置）。
/// </summary>
public sealed class WizardDialog : Window
{
    // 全窗遮罩：主题无关的半透明叠层（亮/暗通用），显式保留。
    private static readonly Brush ScrimBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x10, 0x14, 0x1C));

    private readonly WizardViewModel _viewModel;
    private readonly StackPanel _stepStrip = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _stagingRows = new();
    private readonly StackPanel _resultRows = new();
    private readonly ContentControl _parameterHost = new();
    private readonly TextBlock _validationText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Grid _body = new();
    private readonly StackPanel _footerButtons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 16, 0, 0),
    };
    private readonly FrameworkElement _stagingPanel;
    private readonly FrameworkElement _parameterPanel;
    private readonly FrameworkElement _resultPanel;

    private readonly Button _addButton = new() { Content = "添加文件", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _cancelButton = new() { Content = "取消", MinWidth = 88, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _backButton = new() { Content = "上一步", MinWidth = 88, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _nextButton = new() { Content = "下一步", MinWidth = 96, Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
    private readonly Button _submitButton = new() { Content = "开始处理", MinWidth = 96, Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
    private readonly Button _finishButton = new() { Content = "完成", MinWidth = 96, Padding = new Thickness(12, 6, 12, 6), IsDefault = true };

    public WizardDialog(WizardViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Title = $"向导 · {viewModel.Tool.Name}";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Manual;
        Width = 780;
        Height = 620;
        MinWidth = 640;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        _stagingPanel = BuildStagingPanel();
        _parameterPanel = BuildParameterPanel();
        _resultPanel = BuildResultPanel();

        // 主题 token 经资源引用（亮/暗由 ThemeManager 注入，运行时可跟随）
        _validationText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        Content = BuildRoot();

        _viewModel.PropertyChanged += (_, _) => Refresh();
        // ReadOnlyObservableCollection 以显式接口实现集合变更事件，经接口订阅更稳妥。
        ((INotifyCollectionChanged)_viewModel.StagedFiles).CollectionChanged += OnStagingChanged;
        ((INotifyCollectionChanged)_viewModel.Results).CollectionChanged += OnResultsChanged;

        Refresh();
        Loaded += (_, _) => _nextButton.Focus();
    }

    /// <summary>向导结果（提交完成后由宿主处置：PDF 开新标签 / 非 PDF Toast+打开文件夹）。</summary>
    public IReadOnlyList<WizardItemResult> Results => [.. _viewModel.Results];

    // ---------------------------------------------------------------- UI 构建

    private FrameworkElement BuildRoot()
    {
        var scrim = new Border { Background = ScrimBrush };
        scrim.MouseLeftButtonDown += OnScrimMouseLeftButtonDown;

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 18, 22, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 960,
            MaxHeight = 760,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        var layout = new DockPanel();
        DockPanel.SetDock(_stepStrip, Dock.Top);
        layout.Children.Add(_stepStrip);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var main = new StackPanel();
        main.Children.Add(new TextBlock
        {
            Text = _viewModel.Tool.Name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        main.Children.Add(_body);
        main.Children.Add(_validationText);
        main.Children.Add(_statusText);
        layout.Children.Add(main);

        card.Child = layout;
        scrim.Child = card;
        return scrim;
    }

    private FrameworkElement BuildFooter()
    {
        // 修复：按钮一次性挂到固定 footer，刷新时只切换可见性/可用性（不再爬视觉树重挂）。
        _cancelButton.Click += (_, _) => CancelAndClose();
        _backButton.Click += (_, _) => _viewModel.GoBack();
        _addButton.Click += (_, _) => BrowseFiles();
        _nextButton.Click += (_, _) => _viewModel.GoNext();
        _submitButton.Click += async (_, _) => await _viewModel.SubmitAsync();
        _finishButton.Click += (_, _) => CloseWithResult();

        _footerButtons.Children.Add(_cancelButton);
        _footerButtons.Children.Add(_backButton);
        _footerButtons.Children.Add(_nextButton);
        _footerButtons.Children.Add(_submitButton);
        _footerButtons.Children.Add(_finishButton);
        return _footerButtons;
    }

    private FrameworkElement BuildStagingPanel()
    {
        var panel = new StackPanel();

        var dropZone = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 20, 14, 20),
            AllowDrop = true,
        };
        dropZone.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        dropZone.SetResourceReference(Border.BackgroundProperty, "Brush.DropZoneBackground");
        var dropHint = new TextBlock
        {
            Text = "将文件拖到这里，或点击下方「添加文件」。当前文档会自动入选并置顶。",
            TextWrapping = TextWrapping.Wrap,
        };
        dropHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        dropZone.Child = dropHint;
        dropZone.DragOver += OnDropDragOver;
        dropZone.Drop += OnDrop;
        panel.Children.Add(dropZone);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        toolbar.Children.Add(_addButton);
        panel.Children.Add(toolbar);

        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320,
            Content = _stagingRows,
        });

        // 初始行
        RebuildStagingRows();
        return panel;
    }

    private FrameworkElement BuildParameterPanel()
    {
        _parameterHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            Content = _parameterHost,
        };
    }

    private FrameworkElement BuildResultPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            Content = _resultRows,
        });
        return panel;
    }

    // ---------------------------------------------------------------- 状态刷新

    private void Refresh()
    {
        RefreshStepStrip();
        RefreshBody();
        RefreshValidation();
        RefreshFooter();

        _validationText.Text = _viewModel.ValidationMessage ?? "";
        _validationText.Visibility = string.IsNullOrEmpty(_viewModel.ValidationMessage) ? Visibility.Collapsed : Visibility.Visible;
        _statusText.Text = _viewModel.StatusMessage;
    }

    private void RefreshStepStrip()
    {
        _stepStrip.Children.Clear();
        for (var i = 0; i < _viewModel.Steps.Count; i++)
        {
            var step = _viewModel.Steps[i];
            var current = i == _viewModel.CurrentStepIndex;
            var label = new TextBlock
            {
                Text = $"{(current ? "●" : "○")} {step.Title}",
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.Accent" : "Brush.TextSecondary");
            _stepStrip.Children.Add(label);
        }
    }

    private void RefreshBody()
    {
        _body.Children.Clear();
        if (_viewModel.IsResultStep)
        {
            _body.Children.Add(_resultPanel);
            RebuildResultRows();
            return;
        }

        if (_viewModel.IsParameterStep)
        {
            _parameterHost.Content = _viewModel.HostedContent
                ?? new TextBlock
                {
                    Text = "该工具尚未接入向导（等待 T8 工具接入）。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FindBrush("Brush.TextSecondary"),
                };
            _body.Children.Add(_parameterPanel);
            return;
        }

        _body.Children.Add(_stagingPanel);
    }

    private void RefreshValidation()
    {
        // 未提交前不显示旧错误；提交/前进失败时由 VM 写入。
        if (!string.IsNullOrEmpty(_viewModel.ValidationMessage))
        {
            _validationText.Text = _viewModel.ValidationMessage!;
        }
    }

    private void RefreshFooter()
    {
        var staging = _viewModel.IsFileStagingStep;
        var parameters = _viewModel.IsParameterStep;
        var result = _viewModel.IsResultStep;

        _cancelButton.Visibility = result ? Visibility.Collapsed : Visibility.Visible;
        _backButton.Visibility = parameters ? Visibility.Visible : Visibility.Collapsed;
        _backButton.IsEnabled = _viewModel.CanGoBack;
        _nextButton.Visibility = staging ? Visibility.Visible : Visibility.Collapsed;
        _nextButton.IsEnabled = _viewModel.CanGoNext;
        _submitButton.Visibility = parameters ? Visibility.Visible : Visibility.Collapsed;
        _submitButton.IsEnabled = _viewModel.CanSubmit;
        _finishButton.Visibility = result ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 文件暂存

    private void OnStagingChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildStagingRows();
        RefreshFooter();
    }

    private void RebuildStagingRows()
    {
        _stagingRows.Children.Clear();
        if (_viewModel.StagedFiles.Count == 0)
        {
            _stagingRows.Children.Add(new TextBlock { Text = "尚未添加文件。", Foreground = FindBrush("Brush.TextSecondary"), Margin = new Thickness(2, 6, 0, 0) });
            return;
        }

        foreach (var file in _viewModel.StagedFiles)
        {
            var row = new Border
            {
                BorderBrush = FindBrush("Brush.Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 6, 0, 6),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = file.Name, VerticalAlignment = VerticalAlignment.Center });
            if (file.IsCurrentDocument)
            {
                label.Children.Add(new Border
                {
                    Background = FindBrush("Brush.Accent"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "当前文档", Foreground = Brushes.White, FontSize = 11 },
                });
            }

            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(RowButton("↑", () => _viewModel.MoveStagedFileUp(file)));
            actions.Children.Add(RowButton("↓", () => _viewModel.MoveStagedFileDown(file)));
            actions.Children.Add(RowButton("移除", () => _viewModel.RemoveStagedFile(file)));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            row.Child = grid;
            _stagingRows.Children.Add(row);
        }
    }

    private static Button RowButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 34,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void BrowseFiles()
    {
        var extensions = _viewModel.Tool.Extensions;
        var pattern = string.Join(";", extensions.Select(e => "*." + e.ToLowerInvariant()));
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"为{_viewModel.Tool.Name}选择文件",
            Filter = $"{_viewModel.Tool.Name}|{pattern}|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.StageFiles(dialog.FileNames);
        }
    }

    private void OnDropDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.StageFiles(paths);
        }

        e.Handled = true;
    }

    // ---------------------------------------------------------------- 结果

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildResultRows();

    private void RebuildResultRows()
    {
        _resultRows.Children.Clear();
        if (_viewModel.Results.Count == 0)
        {
            _resultRows.Children.Add(new TextBlock { Text = "尚无结果。", Foreground = FindBrush("Brush.TextSecondary") });
            return;
        }

        foreach (var result in _viewModel.Results)
        {
            var text = result.Ok
                ? (result.Outputs.Count == 0 ? "完成（无输出）" : string.Join("\n", result.Outputs))
                : $"失败：{result.Error}";
            _resultRows.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = result.Ok ? FindBrush("Brush.TextPrimary") : FindBrush("Brush.Error"),
                Margin = new Thickness(0, 3, 0, 3),
            });
        }
    }

    // ---------------------------------------------------------------- 取消 / 关闭

    private void OnScrimMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 仅点击卡片之外的遮罩区域才取消（卡片子元素命中时 OriginalSource 不是 scrim）。
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.HasSubmitted)
        {
            CloseWithResult();
        }
        else
        {
            CancelAndClose();
        }
    }

    /// <summary>Esc：未提交 → 取消丢弃草稿；已提交 → 关闭并交宿主处置结果。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        if (_viewModel.HasSubmitted)
        {
            CloseWithResult();
        }
        else
        {
            CancelAndClose();
        }
    }

    private void CancelAndClose()
    {
        _viewModel.Cancel();
        DialogResult = false;
        Close();
    }

    private void CloseWithResult()
    {
        DialogResult = true;
        Close();
    }

    /// <summary>从应用级资源取主题 token（亮/暗由 ThemeManager 注入）；缺失回退 Transparent，不崩。</summary>
    private static Brush FindBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
