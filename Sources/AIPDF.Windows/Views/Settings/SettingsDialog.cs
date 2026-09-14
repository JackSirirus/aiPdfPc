// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T5 设置模态：承载 T1 的 7 项设置（FR-38 + 裁决 Q6）——默认缩放 / 默认左栏面板 / 最近文件上限 /
// 主题 / 打印 DPI / 默认输出目录 / 默认渲染 DPI。保存即时写盘并归一化（AC-39.1/39.2）；
// 「仅对新打开文档生效」的注入边界由宿主 MainWindow 保证，本对话框只负责读写设置。
// v1.1 T23：新增「翻译」段——云端服务商（provider + endpoint + model）管理与同意撤销。
//   - 连接配置落 AppSettings.TranslationProviders（绝不存密钥）；密钥经 TranslationCredentialStore
//     写入系统凭据管理器，界面只显示「已保存/未保存」状态，绝不回显密钥本体；
//   - 「撤销同意」经 TranslationConsentService.RevokeConsent，可勾选「同时清除凭据」；
//   - 「清除凭据」只删系统凭据，保留服务商 / 模型 / 同意（下次仅需重填 key）；
//   - 翻译段操作即时落盘；保存其余设置时经 _store.Load() 回读，绝不覆盖已有同意与连接配置（沿用 T16 守卫）。
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
/// 取消 / 关闭返回 null。密码设置不存在（PRD 4.3.3 红线）；翻译密钥只进系统凭据管理器。
/// </summary>
public sealed class SettingsDialog : Window
{
    private readonly SettingsStore _store;
    private readonly ThemeManager? _themeManager;
    private readonly TranslationConsentService _translationConsent;
    private readonly TranslationCredentialStore _translationCredentials;

    private readonly ComboBox _zoomBox = NewCombo();
    private readonly ComboBox _panelBox = NewCombo();
    private readonly Slider _recentSlider = new();
    private readonly TextBlock _recentValue = new();
    private readonly ComboBox _themeBox = NewCombo();
    private readonly TextBox _printDpiBox = new();
    private readonly TextBox _outputDirBox = new();
    private readonly TextBox _renderDpiBox = new();

    // 翻译 · 云端服务商管理（T23）
    private readonly ComboBox _providerCombo = NewCombo();
    private readonly TextBox _providerBox = new();
    private readonly TextBox _endpointBox = new();
    private readonly TextBox _modelBox = new();
    private readonly PasswordBox _keyBox = new();
    private readonly TextBlock _consentStatus = new();
    private readonly TextBlock _translationMessage = new();
    private readonly CheckBox _markerCheck = new();
    private readonly CheckBox _revokeAlsoClear = new();
    private readonly Button _saveProviderButton = new();
    private readonly Button _storeKeyButton = new();
    private readonly Button _revokeButton = new();
    private readonly Button _clearCredentialButton = new();

    private readonly List<TranslationProviderConfig> _providers = [];
    private TranslationProviderConfig? _selectedProvider;
    private bool _suppressProviderSelection;

    /// <summary>保存成功后的已归一化设置；取消 / 关闭为 null。</summary>
    public AppSettings? SavedSettings { get; private set; }

    /// <param name="store">设置存储（读写 settings.json）。</param>
    /// <param name="themeManager">主题管理器；保存后即时应用。</param>
    /// <param name="translationConsent">云端同意服务；缺省按当前存储与凭据新建（测试 / 独立打开时可用）。</param>
    /// <param name="translationCredentials">云端凭据存储；缺省为系统凭据管理器。</param>
    public SettingsDialog(
        SettingsStore store,
        ThemeManager? themeManager = null,
        TranslationConsentService? translationConsent = null,
        TranslationCredentialStore? translationCredentials = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _themeManager = themeManager;
        _translationCredentials = translationCredentials ?? new TranslationCredentialStore();
        _translationConsent = translationConsent ?? new TranslationConsentService(_store, _translationCredentials);

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

        var current = _store.Load();
        _providers.AddRange(current.TranslationProviders);
        Content = BuildContent(current);
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
            FontFamily = FindFontFamily("FontFamily.Display"),
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

        // 8. 翻译 · 云端服务商管理（T23；翻译段操作即时落盘）
        AddSectionHeader(rows, ref row, "翻译");
        AddSpanningRow(rows, ref row, new TextBlock
        {
            Text = "管理云端翻译服务商与同意：密钥只交 Windows 凭据管理器，设置文件绝不含密钥；本段操作即时生效。",
            Style = FindStyle("FieldHint"),
        });

        _providerCombo.SelectionChanged += OnProviderSelectionChanged;
        AddRow(rows, ref row, "云端服务商",
            "选择一个已保存的服务商进行管理；「＋ 新增服务商」填写后再点「保存服务商」。", _providerCombo);

        ApplyFieldStyle(_providerBox, double.NaN);
        AddRow(rows, ref row, "服务商标识",
            "如 deepseek / moonshot；与端点共同作为同意与凭据的寻址目的地。", _providerBox);

        ApplyFieldStyle(_endpointBox, double.NaN);
        AddRow(rows, ref row, "端点 (endpoint)",
            "OpenAI 兼容端点，须为 http(s) 绝对地址；不要在地址里内嵌密钥或 token 参数。", _endpointBox);

        ApplyFieldStyle(_modelBox, double.NaN);
        AddRow(rows, ref row, "模型名",
            "如 deepseek-chat；仅改模型名不重置已有同意。", _modelBox);

        ApplyPasswordStyle(_keyBox);
        _storeKeyButton.Content = "保存密钥";
        _storeKeyButton.Style = FindStyle("SecondaryButton");
        _storeKeyButton.Margin = new Thickness(8, 0, 0, 0);
        _storeKeyButton.Click += OnStoreKeyClick;
        var keyRow = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_storeKeyButton, Dock.Right);
        keyRow.Children.Add(_storeKeyButton);
        keyRow.Children.Add(_keyBox);
        AddRow(rows, ref row, "API Key",
            "仅保存到系统凭据管理器；界面不回显、设置文件不落盘。", keyRow);

        _consentStatus.FontSize = 12;
        _consentStatus.TextWrapping = TextWrapping.Wrap;
        _consentStatus.Foreground = FindBrush("Brush.TextSecondary");
        AddSpanningRow(rows, ref row, _consentStatus);

        _translationMessage.FontSize = 12;
        _translationMessage.TextWrapping = TextWrapping.Wrap;
        _translationMessage.Foreground = FindBrush("Brush.TextSecondary");
        AddSpanningRow(rows, ref row, _translationMessage);

        _revokeAlsoClear.Content = "撤销同意时同时清除凭据（推荐）";
        _revokeAlsoClear.IsChecked = true;
        _revokeAlsoClear.VerticalAlignment = VerticalAlignment.Center;
        _revokeAlsoClear.Margin = new Thickness(0, 4, 16, 8);

        _saveProviderButton.Content = "保存服务商";
        _saveProviderButton.Style = FindStyle("SecondaryButton");
        _saveProviderButton.Margin = new Thickness(0, 0, 8, 8);
        _saveProviderButton.Click += OnSaveProviderClick;

        _revokeButton.Content = "撤销同意";
        _revokeButton.Style = FindStyle("SecondaryButton");
        _revokeButton.Margin = new Thickness(0, 0, 8, 8);
        _revokeButton.Click += OnRevokeConsentClick;

        _clearCredentialButton.Content = "清除凭据";
        _clearCredentialButton.Style = FindStyle("SecondaryButton");
        _clearCredentialButton.Margin = new Thickness(0, 0, 8, 8);
        _clearCredentialButton.Foreground = FindBrush("Brush.Error");
        _clearCredentialButton.Click += OnClearCredentialClick;

        var translationActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        translationActions.Children.Add(_revokeAlsoClear);
        translationActions.Children.Add(_saveProviderButton);
        translationActions.Children.Add(_revokeButton);
        translationActions.Children.Add(_clearCredentialButton);
        AddSpanningRow(rows, ref row, translationActions);

        // 产物页脚标记（§9.3）：一般文档可关；法律 / 医学高风险模板强制显示、不受此开关影响。
        _markerCheck.Content = "产物 PDF 页脚添加「机器翻译 · 请核对」标记（一般文档可关）";
        _markerCheck.IsChecked = current.TranslationMarkerEnabled;
        _markerCheck.FontSize = 12;
        _markerCheck.Cursor = System.Windows.Input.Cursors.Hand;
        _markerCheck.VerticalAlignment = VerticalAlignment.Center;
        _markerCheck.Margin = new Thickness(0, 12, 0, 0);
        AddSpanningRow(rows, ref row, _markerCheck);
        AddSpanningRow(rows, ref row, new TextBlock
        {
            Text = "法律 / 医学高风险模板强制显示该页脚标记，不受此开关影响；结果区「机器翻译 · 请核对」标记始终常驻。",
            Style = FindStyle("FieldHint"),
        });

        RefreshProviderCombo();

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

        // 内容过高时滚动（翻译段使对话框可能超出小屏）；顶部标题与底部按钮固定。
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 200),
            Content = rows,
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
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

    /// <summary>追加跨两列的分段标题（用于「翻译」段）。</summary>
    private static void AddSectionHeader(Grid grid, ref int row, string text)
    {
        row++;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock
        {
            Text = text,
            Style = FindStyle("SectionTitle"),
            Margin = new Thickness(0, 18, 0, 2),
        };
        Grid.SetRow(header, row);
        Grid.SetColumnSpan(header, 2);
        grid.Children.Add(header);
    }

    /// <summary>追加跨两列的自适应控件（状态行 / 操作按钮组）。</summary>
    private static void AddSpanningRow(Grid grid, ref int row, FrameworkElement control)
    {
        row++;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        control.Margin = new Thickness(0, 2, 0, 4);
        Grid.SetRow(control, row);
        Grid.SetColumnSpan(control, 2);
        grid.Children.Add(control);
    }

    // ------------------------------------------------------------------ 翻译交互（T23）

    /// <summary>重建服务商下拉：已保存项 + 末尾「＋ 新增服务商」；重建后同步编辑器。</summary>
    private void RefreshProviderCombo(TranslationProviderConfig? select = null)
    {
        _suppressProviderSelection = true;
        _providerCombo.Items.Clear();
        foreach (var provider in _providers)
        {
            _providerCombo.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider });
        }
        _providerCombo.Items.Add(new ComboBoxItem { Content = "＋ 新增服务商…", Tag = null });

        ComboBoxItem? target = null;
        if (select is not null)
        {
            foreach (ComboBoxItem item in _providerCombo.Items)
            {
                if (ReferenceEquals(item.Tag, select))
                {
                    target = item;
                    break;
                }
            }
        }
        target ??= _providerCombo.Items.Count > 0 ? (ComboBoxItem)_providerCombo.Items[0] : null;
        _providerCombo.SelectedItem = target;
        _suppressProviderSelection = false;

        SyncEditorWithSelection();
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderSelection) return;
        SyncEditorWithSelection();
    }

    private void SyncEditorWithSelection()
    {
        if (_providerCombo.SelectedItem is ComboBoxItem { Tag: TranslationProviderConfig provider })
        {
            LoadProviderIntoEditor(provider);
        }
        else
        {
            StartNewProvider();
        }
    }

    private void LoadProviderIntoEditor(TranslationProviderConfig provider)
    {
        _selectedProvider = provider;
        _providerBox.Text = provider.Provider;
        _endpointBox.Text = provider.Endpoint;
        _modelBox.Text = provider.Model;
        _keyBox.Clear();
        _translationMessage.Text = "";
        UpdateConsentStatus(provider);
    }

    private void StartNewProvider()
    {
        _selectedProvider = null;
        _providerBox.Text = "";
        _endpointBox.Text = "";
        _modelBox.Text = "";
        _keyBox.Clear();
        _translationMessage.Text = "";
        _consentStatus.Text = "新建服务商：填写服务商 / 端点 / 模型后点「保存服务商」。";
        _storeKeyButton.IsEnabled = false;
        _revokeButton.IsEnabled = false;
        _clearCredentialButton.IsEnabled = false;
    }

    /// <summary>刷新所选目的地的同意 / 凭据状态（只显示「已保存/未保存」，绝不回显密钥）。</summary>
    private void UpdateConsentStatus(TranslationProviderConfig provider)
    {
        _storeKeyButton.IsEnabled = true;
        _revokeButton.IsEnabled = true;
        _clearCredentialButton.IsEnabled = true;

        try
        {
            var hasCredential = !string.IsNullOrEmpty(_translationCredentials.Get(provider.Provider, provider.Endpoint));
            var consented = _translationConsent.IsConsentedUsingStoredKey(provider.Provider, provider.Endpoint);
            _consentStatus.Text =
                $"同意：{(consented ? "已同意" : "未同意")} · 凭据：{(hasCredential ? "已保存" : "未保存")}（系统凭据管理器）";
        }
        catch (CredentialStoreUnavailableException ex)
        {
            _consentStatus.Text = $"同意：未知 · 凭据：不可用（{ex.Message}）";
        }
    }

    private void OnSaveProviderClick(object sender, RoutedEventArgs e)
    {
        var provider = _providerBox.Text.Trim();
        var endpoint = _endpointBox.Text.Trim();
        var model = _modelBox.Text.Trim();

        if (TranslationProviderConfig.Validate(provider, endpoint) is { } error)
        {
            ShowTranslationMessage(error, isError: true);
            return;
        }

        var config = new TranslationProviderConfig { Provider = provider, Endpoint = endpoint, Model = model };
        UpsertProvider(config);
        RefreshProviderCombo(config);
        ShowTranslationMessage("已保存服务商；如未保存密钥，请填写后点「保存密钥」。", isError: false);
    }

    private void OnStoreKeyClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            ShowTranslationMessage("请先选择并保存一个服务商。", isError: true);
            return;
        }

        var key = _keyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowTranslationMessage("请填写要保存的 API Key。", isError: true);
            return;
        }

        try
        {
            _translationCredentials.Store(_selectedProvider.Provider, _selectedProvider.Endpoint, key);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
            return;
        }
        finally
        {
            _keyBox.Clear(); // 立即清空，界面不驻留密钥
        }

        UpdateConsentStatus(_selectedProvider);
        ShowTranslationMessage("密钥已保存到系统凭据管理器；设置文件不含密钥。", isError: false);
    }

    private void OnRevokeConsentClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null) return;

        var alsoClearCredential = _revokeAlsoClear.IsChecked == true;
        bool removed;
        try
        {
            removed = _translationConsent.RevokeConsent(
                _selectedProvider.Provider, _selectedProvider.Endpoint, alsoClearCredential);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
            return;
        }

        UpdateConsentStatus(_selectedProvider);
        ShowTranslationMessage(
            removed
                ? (alsoClearCredential
                    ? "已撤销同意并清除凭据；下次使用需重新同意并填写密钥。"
                    : "已撤销同意；凭据保留，下次使用需重新同意。")
                : "未找到该目的地的同意记录（可能已过期或未授予）。",
            isError: false);
    }

    private void OnClearCredentialClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null) return;

        bool removed;
        try
        {
            removed = _translationCredentials.Delete(_selectedProvider.Provider, _selectedProvider.Endpoint);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
            return;
        }

        UpdateConsentStatus(_selectedProvider);
        ShowTranslationMessage(
            removed
                ? "已清除该服务商的凭据；服务商 / 模型 / 同意保留，下次只需重填密钥。"
                : "系统凭据管理器中未找到该服务商的凭据。",
            isError: false);
    }

    /// <summary>新增 / 更新连接配置（按 provider + endpoint origin 去重）并即时落盘。</summary>
    private void UpsertProvider(TranslationProviderConfig config)
    {
        var destination = Destination(config);
        var index = _providers.FindIndex(p => string.Equals(Destination(p), destination, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _providers[index] = config;
        }
        else
        {
            _providers.Add(config);
        }

        var settings = _store.Load();
        settings.TranslationProviders = [.. _providers];
        _store.Save(settings);
    }

    private static string Destination(TranslationProviderConfig provider)
        => provider.Provider.Trim() + "\n" + TranslationConsent.NormalizeEndpoint(provider.Endpoint);

    private void ShowTranslationMessage(string text, bool isError)
    {
        _translationMessage.Text = text;
        _translationMessage.Foreground = isError ? FindBrush("Brush.Error") : FindBrush("Brush.TextSecondary");
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
        // v1.1：本对话框只即时管理翻译服务商 / 同意；保存其余设置时必须回读已有记录并整体保留，
        // 否则会把同意记录与连接配置一起覆盖清空（T16/T23 守卫）。
        var existing = _store.Load();
        var settings = new AppSettings
        {
            DefaultZoom = SelectedValue(_zoomBox),
            DefaultPanelKey = SelectedValue(_panelBox),
            RecentFilesLimit = (int)Math.Round(_recentSlider.Value),
            Theme = SelectedValue(_themeBox),
            PrintDpi = ParseInt(_printDpiBox.Text, AppSettings.PrintDpiDefault),
            DefaultOutputDir = _outputDirBox.Text,
            DefaultRenderDpi = ParseDouble(_renderDpiBox.Text, AppSettings.RenderDpiDefault),
            TranslationConsents = existing.TranslationConsents,
            TranslationProviders = existing.TranslationProviders, // 服务商管理已即时落盘，不在此覆盖
            TranslationDefaultSourceLanguage = existing.TranslationDefaultSourceLanguage,
            TranslationDefaultTargetLanguage = existing.TranslationDefaultTargetLanguage,
            TranslationTemplateId = existing.TranslationTemplateId,
            TranslationCustomPrompt = existing.TranslationCustomPrompt,
            TranslationGlossaryPath = existing.TranslationGlossaryPath,
            TranslationCostLimitEnabled = existing.TranslationCostLimitEnabled,
            TranslationCostLimitTokens = existing.TranslationCostLimitTokens,
            TranslationMarkerEnabled = _markerCheck.IsChecked == true,
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

    private static void ApplyPasswordStyle(PasswordBox box)
    {
        box.FontSize = 13;
        box.Padding = new Thickness(8, 6, 8, 6);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.Background = FindBrush("Brush.FieldBackground");
        box.BorderBrush = FindBrush("Brush.Border");
        box.BorderThickness = new Thickness(1);
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

    /// <summary>从 App 级资源取字体族；缺失回退 sans UI 字体（不静默变衬线）。</summary>
    private static FontFamily FindFontFamily(string key)
        => Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI Variable Display, Segoe UI");

    /// <summary>从 App 级资源取画刷；缺失返回默认（Transparent），避免对话框构造期拿不到资源时崩溃。</summary>
    private static Brush FindBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
