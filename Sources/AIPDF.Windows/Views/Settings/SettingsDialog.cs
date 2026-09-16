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
//   - 翻译段操作即时落盘；保存其余设置时经 _store.Load() 回读并【就地修改】，绝不覆盖已有同意与
//     连接配置及其 v1.2 新字段（presetId / apiProtocol / modelProtocols；T2/T16 守卫）。
// 代码构建（沿用 Views/Reader/ConfirmationDialog 范式，避免 XAML wpftmp/资源前向引用坑），
// 视觉沿用 App.xaml 的 design token（Brush.* / FieldLabel / PrimaryButton 等），与既有模态一致。
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

    // 翻译 · 模型供应商主从双栏（T11 骨架 + T13 自定义详情；S1/S3/P22–P23）
    private readonly TextBox _providerSearchBox = new();
    private readonly ListBox _providerList = new();
    private readonly Button _addProviderButton = new();
    private readonly TextBlock _detailTitle = new();
    private readonly TextBlock _detailBadge = new();
    private readonly TextBlock _detailSubtitle = new();
    private readonly TextBox _providerNameBox = new();
    private readonly TextBlock _providerIdText = new();
    // 协议（T14）：分段按钮取代下拉，opencode-go 混合协议显示只读「混合（按模型）」
    private readonly Button _protocolOpenAiButton = NewProtocolButton(TranslationProviderCompliance.OpenAiLabel);
    private readonly Button _protocolAnthropicButton = NewProtocolButton(TranslationProviderCompliance.AnthropicLabel);
    private readonly TextBlock _protocolMixedLabel = new();
    private string _protocol = TranslationProviderConfig.ProtocolOpenAi;

    // 编程套餐合规（T14）：常驻警示 + 改用标准 API + 成功横幅 + 预设模型协议徽章
    private readonly ComboBox _presetModelCombo = NewCombo();
    private readonly Border _complianceBanner = new();
    private readonly TextBlock _complianceBannerText = new();
    private readonly Button _useStandardApiButton = new();
    private readonly Border _standardApiOkBanner = new();
    private bool _suppressModelCombo;

    private readonly TextBox _endpointBox = new();
    private readonly TextBox _modelBox = new();
    private readonly ComboBox _fetchedModelCombo = NewCombo();
    private readonly TextBlock _modelHint = new();
    private readonly PasswordBox _keyBox = new();
    private readonly Button _fetchModelsButton = new();
    private readonly Button _storeKeyButton = new();
    private readonly Button _testConnectionButton = new();
    private readonly TextBlock _connectionStatus = new();
    private readonly TextBlock _consentStatus = new();
    private readonly TextBlock _translationMessage = new();
    private readonly WrapPanel _localPortPanel = new();
    private readonly TextBlock _localLoopbackHint = new();
    private readonly CheckBox _markerCheck = new();
    private readonly CheckBox _revokeAlsoClear = new();
    private readonly Button _saveProviderButton = new();
    private readonly Button _deleteProviderButton = new();
    private readonly Button _revokeButton = new();
    private readonly Button _clearCredentialButton = new();

    /// <summary>本地服务默认地址（设计稿 S2：Ollama）。</summary>
    private const string LocalDefaultEndpoint = "http://127.0.0.1:11434/v1";

    /// <summary>端口建议 chips（设计稿 S2 注④：仅便利填充，不探测、不扫描端口）。</summary>
    private static readonly (int Port, string Name)[] LocalPortSuggestions =
    [
        (11434, "Ollama"),
        (1234, "LM Studio"),
        (8000, "vLLM"),
        (8080, "llama.cpp"),
    ];

    private readonly List<TranslationProviderConfig> _providers = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _fetchedModels = new(StringComparer.Ordinal);
    private ProviderEntry? _selectedEntry;
    private TranslationProviderConfig? _draftConfig;
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
        Width = 820; // T11 主从双栏：左列表 + 右详情需更宽的工作区
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

        // 8. 翻译 · 模型供应商（T11 主从双栏骨架 + T13 自定义详情；S1/S3/P22–P23）
        AddSectionHeader(rows, ref row, "翻译");
        AddSpanningRow(rows, ref row, new TextBlock
        {
            Text = "管理翻译服务商：内置预设、本地服务与自定义供应商。密钥只交 Windows 凭据管理器，设置文件绝不含密钥；供应商改动即时生效。",
            Style = FindStyle("FieldHint"),
        });
        AddSpanningRow(rows, ref row, BuildProviderMasterDetail());

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

        RefreshProviderList();

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

    // ------------------------------------------------------------------ 翻译 · 供应商主从（T11 骨架 + T13 自定义详情）

    /// <summary>
    /// 左栏列表条目：内置预设（含本地服务）或已保存 / 草稿的自定义供应商。
    /// <see cref="IsCustom"/> = 未命中任何预设（<see cref="Preset"/> 为 null）；持久化时 PresetId 恒为 null。
    /// </summary>
    private sealed class ProviderEntry
    {
        public required string Identity { get; init; }

        public TranslationProviderPreset? Preset { get; init; }

        public TranslationProviderConfig? Config { get; init; }

        public bool IsDraft { get; init; }

        public bool IsCustom => Preset is null;

        public bool IsLocal => Preset is { } preset && TranslationProviderConfig.IsLocalPresetId(preset.Id);

        public bool IsConfigured => !IsDraft && Config is not null && Config.IsConfigured;

        public string DisplayName => IsCustom
            ? (string.IsNullOrWhiteSpace(Config?.Provider) ? CustomProviderDraft.UnnamedDisplayName : Config!.Provider)
            : Preset!.DisplayName;
    }

    /// <summary>草稿条目在左栏的稳定身份（不与已保存配置冲突）。</summary>
    private const string DraftIdentity = "＋新增（未保存）";

    /// <summary>
    /// 构建翻译段的主从双栏：左栏分组列表（本地 / 云端 / 自定义 + 「＋ 添加供应商」），
    /// 右栏为统一详情（选中自定义项时呈现 T13 自定义详情）。
    /// </summary>
    private FrameworkElement BuildProviderMasterDetail()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- 左栏 ----
        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(new TextBlock { Text = "搜索供应商", Style = FindStyle("FieldHint") });
        _providerSearchBox.Style = FindStyle("FieldTextBox");
        _providerSearchBox.Margin = new Thickness(0, 3, 0, 4);
        _providerSearchBox.TextChanged += (_, _) => RefreshProviderList(_selectedEntry?.Identity);
        left.Children.Add(_providerSearchBox);

        _providerList.Background = Brushes.Transparent;
        _providerList.BorderThickness = new Thickness(0);
        _providerList.Padding = new Thickness(0);
        _providerList.MaxHeight = 372;
        _providerList.SelectionChanged += OnProviderListSelectionChanged;
        _providerList.ItemContainerStyle = BuildProviderListItemStyle();
        left.Children.Add(_providerList);

        _addProviderButton.Content = "＋ 添加供应商";
        _addProviderButton.Style = FindStyle("SecondaryButton");
        _addProviderButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _addProviderButton.Margin = new Thickness(0, 8, 0, 0);
        _addProviderButton.Click += OnAddProviderClick;
        left.Children.Add(_addProviderButton);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // ---- 右栏 ----
        var right = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
            Content = BuildProviderDetail(),
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>左栏列表项样式：透明底、选中态用 A+C AccentSoft（不新增主题 token）。</summary>
    private static Style BuildProviderListItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindBrush("Brush.TextPrimary")));

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, FindBrush("Brush.AccentSoft")));
        style.Triggers.Add(selected);
        return style;
    }

    /// <summary>右栏详情（所有条目共用；任何【已配置】条目均可「移除」，自定义项允许编辑名称与协议）。</summary>
    private UIElement BuildProviderDetail()
    {
        var panel = new StackPanel();

        _detailTitle.FontSize = 15;
        _detailTitle.FontWeight = FontWeights.SemiBold;
        _detailTitle.TextWrapping = TextWrapping.Wrap;
        _detailTitle.Foreground = FindBrush("Brush.TextPrimary");
        panel.Children.Add(_detailTitle);

        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        _detailBadge.FontSize = 11;
        _detailBadge.Foreground = FindBrush("Brush.AccentText");
        _detailBadge.Background = FindBrush("Brush.AccentSoft");
        _detailBadge.Padding = new Thickness(6, 1, 6, 1);
        badgeRow.Children.Add(_detailBadge);
        panel.Children.Add(badgeRow);

        _detailSubtitle.FontSize = 12;
        _detailSubtitle.TextWrapping = TextWrapping.Wrap;
        _detailSubtitle.Foreground = FindBrush("Brush.TextSecondary");
        _detailSubtitle.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(_detailSubtitle);

        // T14：编程套餐 / 订阅端点常驻合规警示（默认折叠，仅选中带警示的预设时显示）+「改用标准 API」
        panel.Children.Add(BuildComplianceBanner());
        // T14：切换到标准 API 后的成功横幅（P42）
        panel.Children.Add(BuildStandardApiOkBanner());

        // 名称（自定义可编辑；预设 / 本地只读）
        ApplyFieldStyle(_providerNameBox, double.NaN);
        _providerNameBox.TextChanged += (_, _) => UpdateProviderIdPreview();
        panel.Children.Add(DetailField("名称", _providerNameBox,
            "仅用于界面显示与面板切换；自定义项同时作为凭据 / 同意寻址的身份。"));

        _providerIdText.FontSize = 12;
        _providerIdText.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
        _providerIdText.Foreground = FindBrush("Brush.TextTertiary");
        _providerIdText.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(DetailField("provider id（自动生成）", _providerIdText,
            "由名称派生；自定义项 PresetId 为空，请求只依赖地址 + 模型 + 协议 + Key。"));

        // 协议（T14：OpenAI 兼容 / Anthropic 兼容 分段按钮；opencode-go 混合协议只读）
        panel.Children.Add(DetailField("协议", BuildProtocolSelector(),
            "OpenAI 兼容 = POST /chat/completions + Authorization: Bearer；Anthropic 兼容 = POST /v1/messages + x-api-key。切换协议会重置该端点的同意。"));

        // API 地址（自由填写；保留路径）
        ApplyFieldStyle(_endpointBox, double.NaN);
        panel.Children.Add(DetailField("API 地址", _endpointBox,
            "http(s) 绝对地址，可带自建路径（如 https://gw.corp/llm/v1）；保存时路径逐字保留，不会塌陷为域名。"));

        // T12：本地服务端口建议 chips（设计稿 S2 注④：仅便利填充，不探测、不扫描端口）
        _localPortPanel.Margin = new Thickness(0, -4, 0, 6);
        _localPortPanel.Visibility = Visibility.Collapsed;
        foreach (var (port, name) in LocalPortSuggestions)
        {
            var chip = new Button
            {
                Content = $"{port} {name}",
                Style = FindStyle("SecondaryButton"),
                FontSize = 11,
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 0),
            };
            chip.Click += (_, _) => ApplyPortSuggestion(port);
            _localPortPanel.Children.Add(chip);
        }
        panel.Children.Add(_localPortPanel);

        // T12：本地服务 loopback 实时校验提示（非 loopback 标红并阻止保存，设计稿 P13–P15 / P20）
        _localLoopbackHint.Style = FindStyle("FieldHint");
        _localLoopbackHint.TextWrapping = TextWrapping.Wrap;
        _localLoopbackHint.FontSize = 11.5;
        _localLoopbackHint.Margin = new Thickness(0, -6, 0, 8);
        _localLoopbackHint.Visibility = Visibility.Collapsed;
        panel.Children.Add(_localLoopbackHint);

        _endpointBox.TextChanged += (_, _) => OnEndpointChanged();

        // 模型（自由输入 + 拉取）
        var modelRow = new DockPanel { LastChildFill = true };
        ApplyFieldStyle(_modelBox, double.NaN);
        _modelBox.TextChanged += (_, _) => UpdateModelHint();
        _fetchModelsButton.Content = "拉取";
        _fetchModelsButton.Style = FindStyle("SecondaryButton");
        _fetchModelsButton.Margin = new Thickness(8, 0, 0, 0);
        _fetchModelsButton.Click += OnFetchModelsClick;
        DockPanel.SetDock(_fetchModelsButton, Dock.Right);
        modelRow.Children.Add(_fetchModelsButton);
        modelRow.Children.Add(_modelBox);

        var modelPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        modelPanel.Children.Add(new TextBlock { Text = "模型", Style = FindStyle("FieldLabel") });
        modelPanel.Children.Add(modelRow);

        // T14：预设默认模型下拉（混合协议供应商在此展示每个模型的协议徽章）
        _presetModelCombo.Style = FindStyle("FieldComboBox");
        _presetModelCombo.Width = double.NaN;
        _presetModelCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _presetModelCombo.Margin = new Thickness(0, 4, 0, 0);
        _presetModelCombo.Visibility = Visibility.Collapsed;
        _presetModelCombo.SelectionChanged += OnPresetModelSelected;
        modelPanel.Children.Add(_presetModelCombo);

        _fetchedModelCombo.Style = FindStyle("FieldComboBox");
        _fetchedModelCombo.Width = double.NaN;
        _fetchedModelCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _fetchedModelCombo.Margin = new Thickness(0, 4, 0, 0);
        _fetchedModelCombo.Visibility = Visibility.Collapsed;
        _fetchedModelCombo.SelectionChanged += OnFetchedModelSelected;
        modelPanel.Children.Add(_fetchedModelCombo);

        _modelHint.FontSize = 12;
        _modelHint.TextWrapping = TextWrapping.Wrap;
        _modelHint.Margin = new Thickness(0, 4, 0, 0);
        modelPanel.Children.Add(_modelHint);
        panel.Children.Add(modelPanel);

        // API Key
        ApplyPasswordStyle(_keyBox);
        _storeKeyButton.Content = "保存到凭据管理器";
        _storeKeyButton.Style = FindStyle("SecondaryButton");
        _storeKeyButton.Margin = new Thickness(8, 0, 0, 0);
        _storeKeyButton.Click += OnStoreKeyClick;
        var keyRow = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_storeKeyButton, Dock.Right);
        keyRow.Children.Add(_storeKeyButton);
        keyRow.Children.Add(_keyBox);
        panel.Children.Add(DetailField("API Key", keyRow,
            "仅保存到系统凭据管理器；界面不回显、设置文件不落盘。可留空（内网网关常无鉴权）。"));

        // 连接测试
        _testConnectionButton.Content = "测试连接";
        _testConnectionButton.Style = FindStyle("SecondaryButton");
        _testConnectionButton.Click += OnTestConnectionClick;
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        testRow.Children.Add(_testConnectionButton);
        _connectionStatus.FontSize = 12;
        _connectionStatus.VerticalAlignment = VerticalAlignment.Center;
        _connectionStatus.Margin = new Thickness(10, 0, 0, 0);
        _connectionStatus.Foreground = FindBrush("Brush.TextSecondary");
        testRow.Children.Add(_connectionStatus);
        panel.Children.Add(DetailField("连接", testRow,
            "「拉取 / 测试连接」由你显式触发，不替代文档上传同意；本地 loopback 端点不发送密钥。"));

        _consentStatus.FontSize = 12;
        _consentStatus.TextWrapping = TextWrapping.Wrap;
        _consentStatus.Foreground = FindBrush("Brush.TextSecondary");
        _consentStatus.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(_consentStatus);

        _translationMessage.FontSize = 12;
        _translationMessage.TextWrapping = TextWrapping.Wrap;
        _translationMessage.Foreground = FindBrush("Brush.TextSecondary");
        _translationMessage.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(_translationMessage);

        // 操作
        _saveProviderButton.Content = "保存";
        _saveProviderButton.Style = FindStyle("PrimaryButton");
        _saveProviderButton.Margin = new Thickness(0, 0, 8, 8);
        _saveProviderButton.Click += OnSaveProviderClick;

        _deleteProviderButton.Content = "删除";
        _deleteProviderButton.Style = FindStyle("SecondaryButton");
        _deleteProviderButton.Margin = new Thickness(0, 0, 8, 8);
        _deleteProviderButton.Foreground = FindBrush("Brush.Error");
        _deleteProviderButton.Visibility = Visibility.Collapsed;
        _deleteProviderButton.Click += OnDeleteProviderClick;

        _revokeButton.Content = "撤销同意";
        _revokeButton.Style = FindStyle("SecondaryButton");
        _revokeButton.Margin = new Thickness(0, 0, 8, 8);
        _revokeButton.Click += OnRevokeConsentClick;

        _clearCredentialButton.Content = "清除凭据";
        _clearCredentialButton.Style = FindStyle("SecondaryButton");
        _clearCredentialButton.Margin = new Thickness(0, 0, 8, 8);
        _clearCredentialButton.Click += OnClearCredentialClick;

        _revokeAlsoClear.Content = "撤销同意时同时清除凭据（推荐）";
        _revokeAlsoClear.IsChecked = true;
        _revokeAlsoClear.VerticalAlignment = VerticalAlignment.Center;
        _revokeAlsoClear.Margin = new Thickness(0, 0, 16, 8);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(_saveProviderButton);
        actions.Children.Add(_deleteProviderButton);
        actions.Children.Add(_revokeButton);
        actions.Children.Add(_clearCredentialButton);
        actions.Children.Add(_revokeAlsoClear);
        panel.Children.Add(actions);

        SetSecondaryActionsEnabled(false);
        return panel;
    }

    /// <summary>右栏字段：标签 + 控件（+ 可选提示）。</summary>
    private static StackPanel DetailField(string label, FrameworkElement control, string? hint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = label, Style = FindStyle("FieldLabel") });
        control.Margin = new Thickness(control.Margin.Left, 3, control.Margin.Right, control.Margin.Bottom);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(control);

        if (!string.IsNullOrEmpty(hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Style = FindStyle("FieldHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        return panel;
    }

    // ------------------------------------------------------------------ 翻译 · 编程套餐合规与协议（T14）

    /// <summary>编程套餐 / 订阅端点的常驻合规警示条（P37）；「改用标准 API」仅在存在同族标准预设时显示。</summary>
    private Border BuildComplianceBanner()
    {
        _complianceBanner.Visibility = Visibility.Collapsed;
        _complianceBanner.Background = FindBrush("Brush.ConverterWarnBackground");
        _complianceBanner.BorderBrush = FindBrush("Brush.ConverterWarnText");
        _complianceBanner.BorderThickness = new Thickness(1);
        _complianceBanner.CornerRadius = new CornerRadius(6);
        _complianceBanner.Padding = new Thickness(10, 8, 10, 8);
        _complianceBanner.Margin = new Thickness(0, 0, 0, 10);

        _complianceBannerText.FontSize = 12;
        _complianceBannerText.TextWrapping = TextWrapping.Wrap;
        _complianceBannerText.Foreground = FindBrush("Brush.ConverterWarnText");
        _complianceBannerText.VerticalAlignment = VerticalAlignment.Center;

        _useStandardApiButton.Content = "改用标准 API";
        _useStandardApiButton.Style = FindStyle("SecondaryButton");
        _useStandardApiButton.Margin = new Thickness(8, 0, 0, 0);
        _useStandardApiButton.VerticalAlignment = VerticalAlignment.Center;
        _useStandardApiButton.Click += OnUseStandardApiClick;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_complianceBannerText, 0);
        Grid.SetColumn(_useStandardApiButton, 1);
        grid.Children.Add(_complianceBannerText);
        grid.Children.Add(_useStandardApiButton);
        _complianceBanner.Child = grid;
        return _complianceBanner;
    }

    /// <summary>「改用标准 API」成功横幅（P42）；由 <see cref="OnUseStandardApiClick"/> 显式显示。</summary>
    private Border BuildStandardApiOkBanner()
    {
        _standardApiOkBanner.Visibility = Visibility.Collapsed;
        _standardApiOkBanner.Background = FindBrush("Brush.SuccessSoft");
        _standardApiOkBanner.BorderBrush = FindBrush("Brush.Success");
        _standardApiOkBanner.BorderThickness = new Thickness(1);
        _standardApiOkBanner.CornerRadius = new CornerRadius(6);
        _standardApiOkBanner.Padding = new Thickness(10, 8, 10, 8);
        _standardApiOkBanner.Margin = new Thickness(0, 0, 0, 10);
        _standardApiOkBanner.Child = new TextBlock
        {
            Text = "✓ " + TranslationProviderCompliance.StandardApiOkBanner,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FindBrush("Brush.Success"),
        };
        return _standardApiOkBanner;
    }

    /// <summary>协议分段按钮 + 混合协议只读标签（T14）。</summary>
    private FrameworkElement BuildProtocolSelector()
    {
        _protocolOpenAiButton.Click += (_, _) => OnProtocolSelected(TranslationProviderConfig.ProtocolOpenAi);
        _protocolAnthropicButton.Click += (_, _) => OnProtocolSelected(TranslationProviderConfig.ProtocolAnthropic);

        _protocolMixedLabel.FontSize = 12;
        _protocolMixedLabel.VerticalAlignment = VerticalAlignment.Center;
        _protocolMixedLabel.Foreground = FindBrush("Brush.AccentText");
        _protocolMixedLabel.Margin = new Thickness(8, 0, 0, 0);
        _protocolMixedLabel.Visibility = Visibility.Collapsed;

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_protocolOpenAiButton);
        row.Children.Add(_protocolAnthropicButton);
        row.Children.Add(_protocolMixedLabel);
        return row;
    }

    private static Button NewProtocolButton(string content) => new()
    {
        Content = content,
        MinWidth = 104,
        FontSize = 12,
        Padding = new Thickness(10, 5, 10, 5),
        Margin = new Thickness(0, 0, 6, 0),
        Cursor = System.Windows.Input.Cursors.Hand,
        BorderThickness = new Thickness(1),
    };

    /// <summary>设置当前协议并刷新分段按钮选中态（不触碰凭据 / 同意）。</summary>
    private void SelectProtocol(string? protocol)
    {
        _protocol = TranslationProviderCompliance.NormalizeProtocol(protocol);
        UpdateProtocolButtons();
    }

    /// <summary>
    /// 用户点协议分段按钮（T16 F3）：切换协议后，若地址仍是预设默认 base 则同步切到新协议的 base，
    /// 避免「协议 anthropic + 端点仍为 openai base」拼出 <c>/v1/v1</c>；自定义地址绝不覆盖。
    /// </summary>
    private void OnProtocolSelected(string protocol)
    {
        SelectProtocol(protocol);
        SyncDefaultEndpointForProtocol();
    }

    /// <summary>当前生效协议（供请求基址归一化 / 拉取 / 保存使用）。</summary>
    private string SelectedProtocol() => _protocol;

    private void UpdateProtocolButtons()
    {
        var mixed = _selectedEntry?.Preset is { } preset && TranslationProviderCompliance.IsMixedProtocol(preset.Id);
        _protocolOpenAiButton.IsEnabled = !mixed;
        _protocolAnthropicButton.IsEnabled = !mixed;
        _protocolMixedLabel.Text = TranslationProviderCompliance.MixedLabel;
        _protocolMixedLabel.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        StyleProtocolButton(_protocolOpenAiButton, !mixed && _protocol == TranslationProviderConfig.ProtocolOpenAi);
        StyleProtocolButton(_protocolAnthropicButton, !mixed && _protocol == TranslationProviderConfig.ProtocolAnthropic);
    }

    private void StyleProtocolButton(Button button, bool active)
    {
        button.Background = active ? FindBrush("Brush.AccentSoft") : FindBrush("Brush.FieldBackground");
        button.BorderBrush = active ? FindBrush("Brush.Accent") : FindBrush("Brush.Border");
        button.Foreground = active ? FindBrush("Brush.AccentText") : FindBrush("Brush.TextPrimary");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>按当前条目重建预设模型下拉（opencode-go 等混合协议模型带 OpenAI / Anthropic 徽章）。</summary>
    private void PopulatePresetModels(ProviderEntry entry, TranslationProviderConfig? config)
    {
        _suppressModelCombo = true;
        try
        {
            _presetModelCombo.Items.Clear();
            var preset = entry.Preset;
            var models = preset?.DefaultModels ?? System.Array.Empty<string>();
            if (models.Length == 0 || entry.IsCustom)
            {
                _presetModelCombo.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var model in models)
            {
                var badge = TranslationProviderCompliance.ModelProtocolBadge(preset!.Id, model, config?.ModelProtocols);
                _presetModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = badge is null ? model : $"{model}  ·  {badge}",
                    Tag = model,
                });
            }

            _presetModelCombo.Visibility = Visibility.Visible;
            SelectModelComboTag(_modelBox.Text);
        }
        finally
        {
            _suppressModelCombo = false;
        }
    }

    private void SelectModelComboTag(string? model)
    {
        var wanted = model?.Trim() ?? "";
        foreach (var item in _presetModelCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, wanted, StringComparison.OrdinalIgnoreCase))
            {
                _presetModelCombo.SelectedItem = item;
                return;
            }
        }

        _presetModelCombo.SelectedItem = null;
    }

    private void OnPresetModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelCombo) return;
        if (_presetModelCombo.SelectedItem is not ComboBoxItem { Tag: string model }) return;

        _modelBox.Text = model;
        RefreshProtocolForModel();
    }

    /// <summary>混合协议供应商：按当前模型切换协议（设计 P36「该模型使用 … 协议，已自动切换」）。</summary>
    private void RefreshProtocolForModel()
    {
        if (_selectedEntry?.Preset is not { } preset) return;
        if (!TranslationProviderCompliance.IsMixedProtocol(preset.Id)) return;

        var protocol = TranslationProviderCompliance.ModelProtocol(preset.Id, _modelBox.Text, _selectedEntry.Config?.ModelProtocols);
        if (protocol is null) return;

        SelectProtocol(protocol);
        // T16 F3：模型切换导致协议变化时，若地址仍是预设默认 base，同步切到新协议的 base（不产生 /v1/v1）。
        SyncDefaultEndpointForProtocol();
    }

    /// <summary>刷新常驻合规警示：仅当当前预设带 <see cref="TranslationProviderPreset.ComplianceWarning"/> 时显示。</summary>
    private void UpdateComplianceBanner(ProviderEntry entry)
    {
        _standardApiOkBanner.Visibility = Visibility.Collapsed;

        var preset = entry.Preset;
        var notice = preset is null ? null : TranslationProviderCompliance.ComplianceNotice(preset.Id);
        if (notice is null)
        {
            _complianceBanner.Visibility = Visibility.Collapsed;
            return;
        }

        _complianceBannerText.Text = "⚠ " + notice;
        _useStandardApiButton.Visibility =
            TranslationProviderCompliance.StandardEndpointFor(preset!.Id) is null ? Visibility.Collapsed : Visibility.Visible;
        _complianceBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 「改用标准 API」（P37 → P42）：切到同族标准预设（GLM → 智谱 GLM；Kimi → 月之暗面 Kimi）。
    /// 只改编辑器指向，**不删除已保存的套餐凭据 / 同意**（T14 Must NOT：不静默丢弃），并明确提示
    /// 标准端点需单独保存密钥与同意。opencode-go 无同族标准端点，按钮不可见。
    /// </summary>
    private void OnUseStandardApiClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry?.Preset is not { } preset) return;

        var standard = TranslationProviderCompliance.StandardPresetFor(preset.Id);
        if (standard is null)
        {
            ShowTranslationMessage("当前供应商没有对应的标准 API 端点。", isError: true);
            return;
        }

        var preservedModel = _modelBox.Text.Trim();
        RefreshProviderList(standard.Id); // 选中标准预设条目：端点 / 协议取标准默认值
        if (preservedModel.Length > 0) _modelBox.Text = preservedModel;
        RefreshProtocolForModel();

        _complianceBanner.Visibility = Visibility.Collapsed;
        _standardApiOkBanner.Visibility = Visibility.Visible;
        ShowTranslationMessage(
            $"{TranslationProviderCompliance.StandardApiToast}。{TranslationProviderCompliance.StandardApiCredentialNotice}",
            isError: false);
    }

    /// <summary>重建左栏列表（按搜索过滤）并尽量保持选中项；随后刷新右栏详情。</summary>
    private void RefreshProviderList(string? selectIdentity = null)
    {
        var wanted = selectIdentity ?? _selectedEntry?.Identity;
        _suppressProviderSelection = true;
        try
        {
            _providerList.Items.Clear();

            var filter = _providerSearchBox.Text?.Trim() ?? "";
            var consumed = new HashSet<TranslationProviderConfig>();

            AddGroupHeader("本地服务");
            AddEntryItem(BuildPresetEntry(TranslationProviderPreset.Local, consumed), filter);

            AddGroupHeader("云端服务商");
            foreach (var preset in TranslationProviderPreset.All)
            {
                AddEntryItem(BuildPresetEntry(preset, consumed), filter);
            }

            AddGroupHeader("自定义");
            foreach (var config in _providers)
            {
                if (consumed.Contains(config)) continue;
                AddEntryItem(new ProviderEntry { Identity = TranslationProviderIdentity.For(config), Config = config }, filter);
            }

            if (_draftConfig is not null)
            {
                AddEntryItem(new ProviderEntry { Identity = DraftIdentity, Config = _draftConfig, IsDraft = true }, filter);
            }

            var target = _providerList.Items.OfType<ListBoxItem>().FirstOrDefault(item =>
                item.Tag is ProviderEntry entry && string.Equals(entry.Identity, wanted, StringComparison.OrdinalIgnoreCase));
            target ??= _providerList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is ProviderEntry);

            if (target is not null) _providerList.SelectedItem = target;
            else _selectedEntry = null;
        }
        finally
        {
            _suppressProviderSelection = false;
        }

        if (_providerList.SelectedItem is ListBoxItem { Tag: ProviderEntry selected })
        {
            LoadEntryIntoDetail(selected);
        }
        else
        {
            ShowEmptyDetail();
        }
    }

    private ProviderEntry BuildPresetEntry(TranslationProviderPreset preset, HashSet<TranslationProviderConfig> consumed)
    {
        TranslationProviderConfig? match = null;
        foreach (var config in _providers)
        {
            if (!string.Equals(TranslationProviderIdentity.For(config), preset.Id, StringComparison.OrdinalIgnoreCase)) continue;
            match = config;
            consumed.Add(config);
            break;
        }

        return new ProviderEntry { Identity = preset.Id, Preset = preset, Config = match };
    }

    private void AddGroupHeader(string text)
        => _providerList.Items.Add(new ListBoxItem
        {
            Content = new TextBlock { Text = text, Style = FindStyle("FieldHint"), Margin = new Thickness(4, 8, 0, 2) },
            IsEnabled = false,
            Focusable = false,
        });

    private void AddEntryItem(ProviderEntry entry, string filter)
    {
        if (!MatchesFilter(entry, filter)) return;
        _providerList.Items.Add(new ListBoxItem { Tag = entry, Content = BuildEntryRow(entry) });
    }

    private static bool MatchesFilter(ProviderEntry entry, string filter)
        => filter.Length == 0
           || entry.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || entry.Identity.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private UIElement BuildEntryRow(ProviderEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = FindBrush("Brush.TextPrimary"),
        });
        text.Children.Add(new TextBlock
        {
            Text = entry.Identity,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = FindBrush("Brush.TextTertiary"),
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (entry.IsConfigured) status.Children.Add(StatusChip(entry));
        else if (entry.IsCustom) status.Children.Add(TagChip("自定义", FindBrush("Brush.NeutralSoft"), FindBrush("Brush.TextSecondary")));
        else if (entry.IsLocal) status.Children.Add(TagChip("本地", FindBrush("Brush.AccentSoft"), FindBrush("Brush.AccentText")));

        status.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(6, 0, 0, 0),
            Fill = entry.IsConfigured ? FindBrush("Brush.Success") : FindBrush("Brush.NeutralSoft"),
        });
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);

        return grid;
    }

    private Border StatusChip(ProviderEntry entry)
    {
        var mismatch = entry.Config is { } config
                       && _fetchedModels.TryGetValue(entry.Identity, out var models)
                       && models.Count > 0
                       && !string.IsNullOrWhiteSpace(config.Model)
                       && !models.Contains(config.Model, StringComparer.Ordinal);

        return mismatch
            ? TagChip("模型未匹配", FindBrush("Brush.ConverterWarnBackground"), FindBrush("Brush.ConverterWarnText"))
            : TagChip("已配置", FindBrush("Brush.AccentSoft"), FindBrush("Brush.AccentText"));
    }

    private static Border TagChip(string text, Brush background, Brush foreground)
        => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground },
        };

    private void OnProviderListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderSelection) return;
        if (_providerList.SelectedItem is not ListBoxItem { Tag: ProviderEntry entry }) return;

        // 丢弃未保存的草稿：选中其他条目时不残留草稿项。
        if (_draftConfig is not null && !entry.IsDraft)
        {
            _draftConfig = null;
            RefreshProviderList(entry.Identity);
            return;
        }

        LoadEntryIntoDetail(entry);
    }

    private void OnAddProviderClick(object sender, RoutedEventArgs e)
    {
        _draftConfig = new TranslationProviderConfig();
        RefreshProviderList(DraftIdentity);
    }

    private void LoadEntryIntoDetail(ProviderEntry entry)
    {
        _selectedEntry = entry;
        var config = entry.Config;
        var preset = entry.Preset;

        _detailTitle.Text = entry.DisplayName;
        _detailBadge.Text = entry.IsCustom ? "自定义端点" : entry.IsLocal ? "本地服务" : "内置预设";
        _detailSubtitle.Text = entry.IsCustom
            ? "任意 OpenAI 兼容服务：自建网关、公司内网代理、第三方兼容层均可。"
            : entry.IsLocal
                ? "本机 OpenAI 兼容端点；API 地址须为 loopback（127.0.0.1 / ::1 / localhost）。"
                : $"内置预设 {preset!.Id} · {PresetKindLabel(preset.Kind)}";

        _providerNameBox.Text = entry.DisplayName;
        _providerNameBox.IsReadOnly = !entry.IsCustom;
        UpdateProviderIdPreview();

        // T16 F3：默认模型 → 默认协议 → 默认端点必须同源（opencode-go + minimax-m3 → anthropic + AnthropicBaseUrl），
        // 否则 openai base 配 anthropic 协议会被引擎拼出 .../v1/v1/messages。
        var model = config?.Model ?? (preset is { DefaultModels.Length: > 0 } ? preset.DefaultModels[0] : "");
        var protocol = config?.ApiProtocol
            ?? DefaultProtocolFor(preset, model, config?.ModelProtocols)
            ?? TranslationProviderConfig.ProtocolOpenAi;
        SelectProtocol(protocol);

        _endpointBox.Text = config?.Endpoint
            ?? (entry.IsLocal ? LocalDefaultEndpoint : DefaultEndpointForProtocol(preset, protocol))
            ?? "";
        // T12：本地服务显示端口建议 chips 并做 loopback 实时校验
        _localPortPanel.Visibility = entry.IsLocal ? Visibility.Visible : Visibility.Collapsed;
        _modelBox.Text = model;

        _keyBox.Clear();
        _connectionStatus.Text = "";
        _translationMessage.Text = "";
        ResetFetchedModels();
        RestoreFetchedModels(entry);
        PopulatePresetModels(entry, config);
        RefreshProtocolForModel();
        UpdateModelHint();
        UpdateComplianceBanner(entry);

        // T16 F1：任何【已配置】条目（含内置预设，如 DeepSeek / OpenAI）都可移除；未配置的预设无可删配置，
        // 按钮隐藏；自定义草稿（未保存）仍可移除（仅丢弃内存草稿）。
        _deleteProviderButton.Visibility = entry.IsCustom || entry.IsConfigured ? Visibility.Visible : Visibility.Collapsed;

        if (!entry.IsDraft && config is { IsConfigured: true })
        {
            UpdateConsentStatus(config);
            SetSecondaryActionsEnabled(true);

            // R5-3：归一化会保留端点校验失败的条目（不再静默丢弃），在此提示原因引导用户修正。
            if (!string.IsNullOrEmpty(config.ValidationError))
            {
                ShowTranslationMessage(config.ValidationError, isError: true);
            }
        }
        else
        {
            SetSecondaryActionsEnabled(false);
            _consentStatus.Text = entry.IsCustom
                ? "新自定义供应商：填写名称 / API 地址 / 模型后点「保存」。"
                : "尚未配置：填写 API 地址与模型后点「保存」。";
        }
    }

    private void ShowEmptyDetail()
    {
        _detailTitle.Text = "未选择供应商";
        _detailBadge.Text = "";
        _detailSubtitle.Text = "在左侧选择一个供应商，或点「＋ 添加供应商」新建自定义供应商。";
        _providerNameBox.Text = "";
        _providerIdText.Text = "";
        _endpointBox.Text = "";
        _modelBox.Text = "";
        _keyBox.Clear();
        _connectionStatus.Text = "";
        _translationMessage.Text = "";
        _consentStatus.Text = "";
        _deleteProviderButton.Visibility = Visibility.Collapsed;
        SetSecondaryActionsEnabled(false);
        ResetFetchedModels();
        _complianceBanner.Visibility = Visibility.Collapsed;
        _standardApiOkBanner.Visibility = Visibility.Collapsed;
        _presetModelCombo.Items.Clear();
        _presetModelCombo.Visibility = Visibility.Collapsed;
        UpdateProtocolButtons();
    }

    private void ResetFetchedModels()
    {
        _fetchedModelCombo.Items.Clear();
        _fetchedModelCombo.Visibility = Visibility.Collapsed;
        _modelHint.Text = "";
        _modelHint.Foreground = FindBrush("Brush.TextSecondary");
    }

    private void RestoreFetchedModels(ProviderEntry entry)
    {
        if (!_fetchedModels.TryGetValue(entry.Identity, out var models) || models.Count == 0) return;

        foreach (var model in models)
        {
            _fetchedModelCombo.Items.Add(new ComboBoxItem { Content = model, Tag = model });
        }

        _fetchedModelCombo.Visibility = Visibility.Visible;
    }

    private void UpdateProviderIdPreview()
    {
        if (_selectedEntry is null)
        {
            _providerIdText.Text = "";
            return;
        }

        _providerIdText.Text = _selectedEntry.IsCustom
            ? new CustomProviderDraft { Name = _providerNameBox.Text }.ProviderId
            : _selectedEntry.Identity;
    }

    private void OnFetchedModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fetchedModelCombo.SelectedItem is ComboBoxItem { Tag: string model })
        {
            _modelBox.Text = model;
        }
    }

    private void UpdateModelHint()
    {
        _modelHint.Text = "";
        _modelHint.Foreground = FindBrush("Brush.TextSecondary");

        if (_selectedEntry is null) return;
        if (!_fetchedModels.TryGetValue(_selectedEntry.Identity, out var models) || models.Count == 0) return;

        if (CustomProviderDraft.ModelListHint(_modelBox.Text, models) is not { } hint) return;

        _modelHint.Text = hint;
        _modelHint.Foreground = FindBrush("Brush.ConverterWarnText");
    }

    private void PopulateFetchedModels(ProviderEntry entry, IReadOnlyList<string> models)
    {
        _fetchedModels[entry.Identity] = models;
        _fetchedModelCombo.Items.Clear();
        foreach (var model in models)
        {
            _fetchedModelCombo.Items.Add(new ComboBoxItem { Content = model, Tag = model });
        }

        _fetchedModelCombo.Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateModelHint();
        ShowTranslationMessage(
            models.Count > 0
                ? $"拉取到 {models.Count} 个模型；可从下拉选择或保留手填值。"
                : "服务端返回空模型列表；请手动输入模型名。",
            isError: false);
    }

    private string? ResolveKeyForFetch(ProviderEntry entry)
    {
        var config = entry.Config;
        if (entry.IsDraft || config is null || !config.IsConfigured) return null;

        // 规则 7：仅对已保存且非 loopback 的端点携带密钥；本地 loopback 一律零密钥外发。
        if (TranslationProviderConfig.IsLoopbackEndpoint(config.Endpoint)) return null;

        try
        {
            return _translationCredentials.Get(TranslationProviderIdentity.For(config), config.Endpoint);
        }
        catch (CredentialStoreUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// T12：地址输入变化时刷新本地 loopback 校验（仅本地服务显示；其他供应商清除提示与红框）。
    /// </summary>
    private void OnEndpointChanged()
    {
        if (_selectedEntry?.IsLocal == true) UpdateLocalEndpointValidation();
        else ClearLocalEndpointValidation();
    }

    private void ClearLocalEndpointValidation()
    {
        _localLoopbackHint.Visibility = Visibility.Collapsed;
        _localLoopbackHint.Text = "";
        _endpointBox.ClearValue(Control.BorderBrushProperty);
    }

    /// <summary>
    /// T12：本地服务地址实时校验（设计稿 P13–P15 / P20）。空 / 格式错 / 非 loopback 一律标红并禁用
    /// 「拉取 / 保存」；合法 loopback 给出「文本不出本机」绿色提示。非 loopback 不得以「本地」名义绕过云端同意。
    /// </summary>
    private void UpdateLocalEndpointValidation()
    {
        if (_selectedEntry?.IsLocal != true) { ClearLocalEndpointValidation(); return; }
        _localPortPanel.Visibility = Visibility.Visible;

        var raw = _endpointBox.Text.Trim();
        _localLoopbackHint.Visibility = Visibility.Visible;

        if (ValidateLocalEndpoint(raw) is { } error)
        {
            SetLocalEndpointError(error);
            return;
        }

        _localLoopbackHint.Text = "✓ 地址在本机（loopback），文本不出本机；只有云端供应商才会上传文本。";
        _localLoopbackHint.Foreground = FindBrush("Brush.Success");
        _endpointBox.ClearValue(Control.BorderBrushProperty);
        _saveProviderButton.IsEnabled = true;
        _fetchModelsButton.IsEnabled = true;
    }

    private void SetLocalEndpointError(string message)
    {
        _localLoopbackHint.Text = message;
        _localLoopbackHint.Foreground = FindBrush("Brush.Danger");
        _endpointBox.BorderBrush = FindBrush("Brush.Danger");
        _saveProviderButton.IsEnabled = false;
        _fetchModelsButton.IsEnabled = false;
    }

    /// <summary>
    /// T12：点击端口建议 chip 仅替换地址中的端口段（设计稿 S2 注④：不探测、不扫描端口）。
    /// 当前地址非合法绝对地址时回落为 127.0.0.1 模板。
    /// </summary>
    private void ApplyPortSuggestion(int port)
        => _endpointBox.Text = ApplyPortSuggestionToAddress(_endpointBox.Text, port);

    /// <summary>
    /// T12：端口建议 chip 的地址替换逻辑（纯函数，便于单测）。仅替换端口段并保留主机与路径
    /// （设计稿 S2 注④：不探测、不扫描端口）；当前地址非合法 http(s) 绝对地址时回落为 127.0.0.1 模板。
    /// </summary>
    internal static string ApplyPortSuggestionToAddress(string? current, int port)
    {
        var value = current?.Trim() ?? "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var builder = new UriBuilder(uri) { Port = port };
            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return $"http://127.0.0.1:{port}/v1";
    }

    /// <summary>
    /// T12：本地服务端点校验（实时提示与保存共用，纯函数便于单测）。返回 null 表示合法 loopback；
    /// 否则返回错误文案。非 loopback 一律拒绝，防止以「本地」名义绕过云端同意。
    /// </summary>
    internal static string? ValidateLocalEndpoint(string? endpoint)
    {
        var raw = endpoint?.Trim() ?? "";
        if (raw.Length == 0)
        {
            return "请填写本机 OpenAI 兼容地址，例如 " + LocalDefaultEndpoint + "。";
        }

        // 设计稿 P14：必须显式以 http:// 或 https:// 开头（scheme-less 输入不会被静默补全）。
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "地址格式无效；示例：" + LocalDefaultEndpoint + "（须以 http:// 或 https:// 开头）。";
        }

        var normalized = ProviderUrl.NormalizeRequestBase(raw, TranslationProviderConfig.ProtocolOpenAi);
        if (normalized.Length == 0)
        {
            return "地址格式无效；示例：" + LocalDefaultEndpoint + "（须以 http:// 或 https:// 开头）。";
        }

        if (!TranslationProviderConfig.IsLoopbackEndpoint(normalized))
        {
            return TranslationProviderConfig.LoopbackViolationMessage(normalized)
                + " 非 loopback 地址不能以「本地服务」名义保存，请按云端供应商处理。";
        }

        return null;
    }

    private async void OnFetchModelsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;

        var entry = _selectedEntry;
        var protocol = SelectedProtocol();
        var requestBase = ProviderUrl.NormalizeRequestBase(_endpointBox.Text, protocol);
        if (requestBase.Length == 0)
        {
            ShowTranslationMessage("API 地址无效，无法拉取模型列表。", isError: true);
            return;
        }

        // T12：本地服务必须为 loopback；非 loopback 不得以「本地」名义发起请求（防绕过云端同意）。
        if (entry.IsLocal && !TranslationProviderConfig.IsLoopbackEndpoint(requestBase))
        {
            ShowTranslationMessage(TranslationProviderConfig.LoopbackViolationMessage(requestBase), isError: true);
            return;
        }

        var key = ResolveKeyForFetch(entry);
        SetFetchBusy(true);
        try
        {
            var models = await new LocalModelFetcher().FetchAsync(requestBase, key, protocol);
            PopulateFetchedModels(entry, models);
        }
        catch (LocalModelFetchException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
        }
        catch (ArgumentException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
        }
        finally
        {
            SetFetchBusy(false);
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;

        var entry = _selectedEntry;
        var protocol = SelectedProtocol();
        var requestBase = ProviderUrl.NormalizeRequestBase(_endpointBox.Text, protocol);
        if (requestBase.Length == 0)
        {
            ShowConnectionStatus("✗ 地址无效，无法测试连接。", isError: true);
            return;
        }

        // T12：本地服务必须为 loopback；非 loopback 不得以「本地」名义发起请求（防绕过云端同意）。
        if (entry.IsLocal && !TranslationProviderConfig.IsLoopbackEndpoint(requestBase))
        {
            ShowConnectionStatus("✗ " + TranslationProviderConfig.LoopbackViolationMessage(requestBase), isError: true);
            return;
        }

        var key = ResolveKeyForFetch(entry);
        SetFetchBusy(true);
        var watch = Stopwatch.StartNew();
        try
        {
            await new LocalModelFetcher().FetchAsync(requestBase, key, protocol);
            watch.Stop();
            ShowConnectionStatus($"✓ 测试连接通过 · {watch.ElapsedMilliseconds} ms", isError: false);
        }
        catch (LocalModelFetchException ex)
        {
            ShowConnectionStatus($"✗ 测试连接失败：{ex.Message}", isError: true);
            // T14 / P38–P41：按状态码 / 服务端代码归类失败态（协议不匹配 / 密钥不匹配 / 套餐额度不足 / 已下线模型）。
            var banner = TranslationProviderCompliance.FailureBanner(
                TranslationProviderCompliance.ClassifyFailure(ex.StatusCode, ex.Message, _modelBox.Text));
            if (banner is not null) ShowTranslationMessage(banner, isError: true);
        }
        catch (ArgumentException ex)
        {
            ShowConnectionStatus($"✗ 测试连接失败：{ex.Message}", isError: true);
        }
        finally
        {
            SetFetchBusy(false);
        }
    }

    private void ShowConnectionStatus(string text, bool isError)
    {
        _connectionStatus.Text = text;
        _connectionStatus.Foreground = isError ? FindBrush("Brush.Error") : FindBrush("Brush.TextSecondary");
    }

    private void SetFetchBusy(bool busy)
    {
        _fetchModelsButton.IsEnabled = !busy;
        _testConnectionButton.IsEnabled = !busy;
        _saveProviderButton.IsEnabled = !busy;
    }

    private void SetSecondaryActionsEnabled(bool enabled)
    {
        _storeKeyButton.IsEnabled = enabled;
        _revokeButton.IsEnabled = enabled;
        _clearCredentialButton.IsEnabled = enabled;
    }

    /// <summary>
    /// 条目的默认协议（T16 F3）：混合协议预设按默认模型解析（opencode-go + minimax-m3 → anthropic），
    /// 其余取预设 DefaultProtocol；无预设（自定义）返回 null（不臆造协议）。
    /// </summary>
    internal static string? DefaultProtocolFor(
        TranslationProviderPreset? preset,
        string? model,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (preset is null) return null;

        return TranslationProviderCompliance.ModelProtocol(preset.Id, model, overrides)
               ?? TranslationProviderCompliance.NormalizeProtocol(preset.DefaultProtocol);
    }

    /// <summary>
    /// 选定供应商时的默认端点（T16 F3）：必须与生效协议【同源】—— anthropic 用 <see cref="TranslationProviderPreset.AnthropicBaseUrl"/>
    /// （不含 <c>/v1</c>，引擎拼 <c>/v1/messages</c>），openai 用 <see cref="TranslationProviderPreset.OpenAiBaseUrl"/>。
    /// 否则「openai base + anthropic 协议」会被引擎拼出 <c>.../v1/v1/messages</c> 的错误路径。
    /// 协议侧的 base 缺失时回退另一侧（预设只有单一 base 时保持既有可见地址，不塌陷为空）。
    /// </summary>
    internal static string? DefaultEndpointForProtocol(TranslationProviderPreset? preset, string? protocol)
    {
        if (preset is null) return null;

        var baseUrl = TranslationProviderCompliance.NormalizeProtocol(protocol) == TranslationProviderConfig.ProtocolAnthropic
            ? preset.AnthropicBaseUrl ?? preset.OpenAiBaseUrl
            : preset.OpenAiBaseUrl ?? preset.AnthropicBaseUrl;
        return string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl;
    }

    /// <summary>
    /// 协议切换时同步预设默认端点（T16 F3）：仅当当前地址仍是该预设的任一默认 base（用户未自定义）时才替换，
    /// 保证协议与端点同源、不产生重复版本段；返回是否发生替换。
    /// </summary>
    private bool SyncDefaultEndpointForProtocol()
    {
        if (_selectedEntry?.Preset is not { } preset) return false;
        if (!IsPresetDefaultEndpoint(preset, _endpointBox.Text)) return false;

        var next = DefaultEndpointForProtocol(preset, _protocol);
        if (string.IsNullOrEmpty(next)) return false;
        if (string.Equals(_endpointBox.Text.Trim(), next, StringComparison.OrdinalIgnoreCase)) return false;

        _endpointBox.Text = next;
        return true;
    }

    /// <summary>当前地址是否仍等于该预设的 OpenAI / Anthropic 默认 base（= 用户未自定义端点）。</summary>
    private static bool IsPresetDefaultEndpoint(TranslationProviderPreset preset, string? endpoint)
    {
        var value = endpoint?.Trim() ?? "";
        if (value.Length == 0) return false;

        return string.Equals(value, preset.OpenAiBaseUrl, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, preset.AnthropicBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static string PresetKindLabel(TranslationProviderPresetKind kind) => kind switch
    {
        TranslationProviderPresetKind.CodingPlan => "编程套餐",
        TranslationProviderPresetKind.CloudSubscription => "订阅网关",
        _ => "标准 API",
    };

    /// <summary>刷新所选目的地的同意 / 凭据状态（只显示「已保存/未保存」，绝不回显密钥）。</summary>
    private void UpdateConsentStatus(TranslationProviderConfig provider)
    {
        try
        {
            // T8：凭据 / 同意按稳定身份键寻址（显示名 / 大小写不影响定位）。
            var identity = TranslationProviderIdentity.For(provider);
            var hasCredential = !string.IsNullOrEmpty(_translationCredentials.Get(identity, provider.Endpoint));
            var consented = _translationConsent.IsConsentedUsingStoredKey(identity, provider.Endpoint);
            _consentStatus.Text =
                $"同意：{(consented ? "已同意" : "未同意")} · 凭据：{(hasCredential ? "已保存" : "未保存")}（系统凭据管理器）";
        }
        catch (CredentialStoreUnavailableException ex)
        {
            _consentStatus.Text = $"同意：未知 · 凭据：不可用（{ex.Message}）";
        }
    }

    /// <summary>
    /// 保存当前供应商：自定义走 <see cref="CustomProviderDraft"/>（PresetId=null、端点路径逐字保留）；
    /// 预设 / 本地写出 PresetId，本地强制 loopback 校验，预设不自动启用套餐合规标记。
    /// </summary>
    private void OnSaveProviderClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null)
        {
            ShowTranslationMessage("请先在左侧选择一个供应商。", isError: true);
            return;
        }

        var entry = _selectedEntry;
        var protocol = SelectedProtocol();
        var endpoint = _endpointBox.Text.Trim();
        var model = _modelBox.Text.Trim();

        // T14 / P40：已下线模型 id 不发起调用，直接拦截并给出退休清单与替代模型。
        if (TranslationProviderCompliance.IsRetiredModel(model))
        {
            ShowTranslationMessage(TranslationProviderCompliance.RetiredModelBanner, isError: true);
            return;
        }

        if (entry.IsCustom)
        {
            var draft = new CustomProviderDraft
            {
                Name = _providerNameBox.Text,
                Protocol = protocol,
                Endpoint = endpoint,
                Model = model,
            };

            if (draft.Validate() is { } error)
            {
                ShowTranslationMessage(error, isError: true);
                return;
            }

            var config = draft.ToConfig();
            PersistProvider(config, entry.IsDraft ? null : entry.Config);
            _draftConfig = null;
            RefreshProviderList(TranslationProviderIdentity.For(config));
            ShowTranslationMessage(
                "已保存自定义供应商：API 地址（含路径）逐字保留，PresetId 为空（自定义）。如未保存密钥，请填写后点「保存到凭据管理器」。",
                isError: false);
            return;
        }

        var preset = entry.Preset!;
        if (TranslationProviderConfig.Validate(preset.DisplayName, endpoint) is { } presetError)
        {
            ShowTranslationMessage(presetError, isError: true);
            return;
        }

        if (entry.IsLocal && ValidateLocalEndpoint(endpoint) is { } localEndpointError)
        {
            ShowTranslationMessage(localEndpointError, isError: true);
            return;
        }

        var presetConfig = new TranslationProviderConfig
        {
            Provider = preset.DisplayName,
            Endpoint = endpoint,
            Model = model,
            PresetId = preset.Id,
            ApiProtocol = SettingsStore.NormalizeApiProtocol(protocol),
            ModelProtocols = preset.DefaultModelProtocols,
        };
        PersistProvider(presetConfig, entry.Config);
        RefreshProviderList(preset.Id);
        ShowTranslationMessage("已保存供应商配置。", isError: false);
    }

    private void OnDeleteProviderClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is not { } entry) return;

        // 草稿：仅丢弃内存中的未保存项（无配置 / 凭据 / 同意可清理）。
        if (entry.IsDraft)
        {
            _draftConfig = null;
            RefreshProviderList();
            ShowTranslationMessage("已丢弃未保存的供应商草稿。", isError: false);
            return;
        }

        var config = entry.Config;
        if (config is null || !_providers.Contains(config))
        {
            // 未配置的预设没有可删除的配置（按钮已隐藏）；兜底不谎报成功。
            ShowTranslationMessage("该供应商尚未配置，无需移除。", isError: true);
            return;
        }

        // T16 F1：内置预设（DeepSeek / OpenAI 等）与自定义供应商一律可移除；确认弹窗说明不可逆后果。
        var confirmed = IsDeleteConfirmed(MessageBox.Show(
            this,
            DeleteConfirmMessage(entry.DisplayName),
            "移除供应商",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning));

        var outcome = RemoveProviderWithConfirmation(
            confirmed, _providers, config, _translationCredentials, _translationConsent, SaveProviders);
        if (outcome is null) return; // 未确认：配置 / 凭据 / 同意原样保留

        RefreshProviderList();
        ShowTranslationMessage(outcome.Message, isError: outcome.CleanupFailed);
    }

    /// <summary>移除确认文案（T16 F1；设计稿 P24：不可逆，同时删除配置、凭据与同意）。</summary>
    internal static string DeleteConfirmMessage(string displayName)
        => $"移除供应商「{displayName}」？此操作不可撤销，将同时删除：\n"
           + "· 该供应商的连接配置（地址 / 模型 / 协议）\n"
           + "· 系统凭据管理器中的 API Key（若已保存）\n"
           + "· 该服务商与端点的同意记录（若已授予）\n"
           + "移除后再次使用需重新填写密钥并重新同意。";

    /// <summary>仅显式「是」才执行移除（T16 F1：未确认不删除任何内容）。</summary>
    internal static bool IsDeleteConfirmed(MessageBoxResult result) => result == MessageBoxResult.Yes;

    /// <summary>移除结果（T16 F2）：<see cref="CleanupFailed"/> 为 true 时配置项保留、消息如实描述失败项。</summary>
    internal sealed record ProviderRemovalOutcome(
        bool ProviderRemoved,
        bool CredentialCleared,
        bool ConsentRevoked,
        bool CleanupFailed,
        string Message);

    /// <summary>
    /// 带确认门的移除流程（T16 F1）：<paramref name="confirmed"/> 为 false 时不做任何修改并返回 null。
    /// </summary>
    internal static ProviderRemovalOutcome? RemoveProviderWithConfirmation(
        bool confirmed,
        List<TranslationProviderConfig> providers,
        TranslationProviderConfig config,
        TranslationCredentialStore credentials,
        TranslationConsentService consent,
        Action persist)
        => confirmed ? RemoveProvider(providers, config, credentials, consent, persist) : null;

    /// <summary>
    /// 移除供应商（T16 F1/F2）：先清理系统凭据与同意，【全部清理成功后】才从配置列表移除并落盘；
    /// 任一清理失败即保留配置、返回如实描述的失败结果（绝不静默当成功），便于用户处理后重试。
    /// </summary>
    internal static ProviderRemovalOutcome RemoveProvider(
        List<TranslationProviderConfig> providers,
        TranslationProviderConfig config,
        TranslationCredentialStore credentials,
        TranslationConsentService consent,
        Action persist)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(persist);

        var identity = TranslationProviderIdentity.For(config);
        var endpoint = config.Endpoint;

        var credentialCleared = false;
        var consentRevoked = false;
        var failures = new List<string>();
        var completed = new List<string>();

        try
        {
            credentialCleared = credentials.Delete(identity, endpoint);
            if (credentialCleared) completed.Add("凭据已清除");
        }
        catch (CredentialStoreUnavailableException ex)
        {
            failures.Add("凭据清除失败：" + ex.Message);
        }

        try
        {
            // 凭据清除已单独执行并单独反馈；此处不再 alsoClearCredential，避免重复删除与级联失败。
            consentRevoked = consent.RevokeConsent(identity, endpoint, alsoClearCredential: false);
            if (consentRevoked) completed.Add("同意已撤销");
        }
        catch (CredentialStoreUnavailableException ex)
        {
            failures.Add("同意撤销失败：" + ex.Message);
        }

        if (failures.Count > 0)
        {
            var partial = completed.Count > 0 ? "；已完成：" + string.Join("、", completed) : "";
            return new ProviderRemovalOutcome(
                ProviderRemoved: false,
                CredentialCleared: credentialCleared,
                ConsentRevoked: consentRevoked,
                CleanupFailed: true,
                Message: $"移除未完成：{string.Join("；", failures)}{partial}。供应商配置未移除（避免凭据残留失去清理入口），请处理后重试。");
        }

        providers.Remove(config);
        persist();

        return new ProviderRemovalOutcome(
            ProviderRemoved: true,
            CredentialCleared: credentialCleared,
            ConsentRevoked: consentRevoked,
            CleanupFailed: false,
            Message: $"已移除供应商「{config.Provider}」：{(credentialCleared ? "凭据已清除" : "无已保存凭据")}，"
                     + $"{(consentRevoked ? "同意已撤销" : "无同意记录")}；再次使用需重新配置并同意。");
    }

    private void OnStoreKeyClick(object sender, RoutedEventArgs e)
    {
        var target = _selectedEntry?.Config;
        if (_selectedEntry is null || target is null || _selectedEntry.IsDraft || !target.IsConfigured)
        {
            ShowTranslationMessage("请先保存供应商，再保存密钥。", isError: true);
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
            _translationCredentials.Store(TranslationProviderIdentity.For(target), target.Endpoint, key);
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

        UpdateConsentStatus(target);
        ShowTranslationMessage("密钥已保存到系统凭据管理器；设置文件不含密钥。", isError: false);
    }

    private void OnRevokeConsentClick(object sender, RoutedEventArgs e)
    {
        var target = _selectedEntry?.Config;
        if (target is null) return;

        var alsoClearCredential = _revokeAlsoClear.IsChecked == true;
        bool removed;
        try
        {
            removed = _translationConsent.RevokeConsent(
                TranslationProviderIdentity.For(target), target.Endpoint, alsoClearCredential);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
            return;
        }

        UpdateConsentStatus(target);
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
        var target = _selectedEntry?.Config;
        if (target is null) return;

        bool removed;
        try
        {
            removed = _translationCredentials.Delete(TranslationProviderIdentity.For(target), target.Endpoint);
        }
        catch (CredentialStoreUnavailableException ex)
        {
            ShowTranslationMessage(ex.Message, isError: true);
            return;
        }

        UpdateConsentStatus(target);
        ShowTranslationMessage(
            removed
                ? "已清除该服务商的凭据；服务商 / 模型 / 同意保留，下次只需重填密钥。"
                : "系统凭据管理器中未找到该服务商的凭据。",
            isError: false);
    }

    /// <summary>新增 / 更新连接配置并即时落盘；编辑既有条目按引用替换，避免改名时产生重复项。</summary>
    private void PersistProvider(TranslationProviderConfig config, TranslationProviderConfig? replacing)
    {
        if (replacing is not null)
        {
            var index = _providers.IndexOf(replacing);
            if (index >= 0)
            {
                _providers[index] = config;
            }
            else
            {
                AddOrReplaceByDestination(config);
            }
        }
        else
        {
            AddOrReplaceByDestination(config);
        }

        SaveProviders();
    }

    private void AddOrReplaceByDestination(TranslationProviderConfig config)
    {
        var destination = Destination(config);
        var index = _providers.FindIndex(p => string.Equals(Destination(p), destination, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _providers[index] = config;
        else _providers.Add(config);
    }

    /// <summary>写入设置（Load→就地改 TranslationProviders→Save），随后回读保持内存与归一化结果一致。</summary>
    private void SaveProviders()
    {
        var settings = _store.Load();
        settings.TranslationProviders = [.. _providers];
        _store.Save(settings);

        _providers.Clear();
        _providers.AddRange(_store.Load().TranslationProviders);
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
        // v1.1/T2：本对话框只即时管理翻译服务商 / 同意；保存其余设置时必须回读已有记录并**就地修改**。
        // 逐字段重建 AppSettings 会把同意记录、连接配置及其 v1.2 新字段（presetId / apiProtocol /
        // modelProtocols）重置为默认（clobber）；就地修改从根上杜绝该面（T16/T23 守卫）。
        var settings = _store.Load();
        ApplyDialogValues(
            settings,
            zoom: SelectedValue(_zoomBox),
            panelKey: SelectedValue(_panelBox),
            recentFilesLimit: (int)Math.Round(_recentSlider.Value),
            theme: SelectedValue(_themeBox),
            printDpi: ParseInt(_printDpiBox.Text, AppSettings.PrintDpiDefault),
            outputDir: _outputDirBox.Text,
            renderDpi: ParseDouble(_renderDpiBox.Text, AppSettings.RenderDpiDefault),
            markerEnabled: _markerCheck.IsChecked == true);

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

    /// <summary>
    /// 把对话框当前值写入【既有】设置对象（就地修改）：本对话框只负责这 8 项，
    /// 其余字段（翻译默认项、同意记录、连接配置及其 presetId / apiProtocol / modelProtocols 等新字段）
    /// 原样保留。保存必须走这里，不得回归为顶层重建（T2 防 clobber；回归见 SettingsDialogTests）。
    /// </summary>
    internal static void ApplyDialogValues(
        AppSettings settings,
        string zoom,
        string panelKey,
        int recentFilesLimit,
        string theme,
        int printDpi,
        string outputDir,
        double renderDpi,
        bool markerEnabled)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.DefaultZoom = zoom;
        settings.DefaultPanelKey = panelKey;
        settings.RecentFilesLimit = recentFilesLimit;
        settings.Theme = theme;
        settings.PrintDpi = printDpi;
        settings.DefaultOutputDir = outputDir;
        settings.DefaultRenderDpi = renderDpi;
        settings.TranslationMarkerEnabled = markerEnabled;
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
