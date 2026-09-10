// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views;

/// <summary>
/// 主窗口：顶栏（品牌 + 标签条 + ＋/搜索/⚙）+ 主区（左栏 / TabControl 标签宿主）+ 状态栏。
/// 多标签核心逻辑在 <see cref="TabHostViewModel"/>；本类只做视图联动与事件接线。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>全文搜索入口命令（Ctrl+F 全局快捷键，FR-09；T8 接管前仅事件桩）。</summary>
    public static readonly RoutedCommand FullTextSearchCommand = new(nameof(FullTextSearchCommand), typeof(MainWindow));

    /// <summary>关闭当前标签命令（Ctrl+W，FR-08）。</summary>
    public static readonly RoutedCommand CloseActiveTabCommand = new(nameof(CloseActiveTabCommand), typeof(MainWindow));

    /// <summary>标签宿主：多标签核心逻辑（同路径去重【D14】/页面实例缓存【D16】/增删）。</summary>
    private TabHostViewModel TabHost { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // Wave 5 工具组导航注册：每组一行（并行任务合并约定，T14 起建立）。
        PageOrganizer.PageOrganizerPages.Register();
        Converter.ConverterPages.Register(); // T16 格式转换组：图片 / Office / 网址 / PDF-A / Markdown
        Optimizer.OptimizerToolPages.Register(); // T15：优化文件组（压缩 / 修复 / OCR）
        Security.SecurityPages.Register(); // T18：安全保护组（解锁 / 加密 / 签名 / 遮盖 / 比较）
        Editor.EditorPages.Register(); // T17：编辑内容组（旋转 / 页码 / 水印 / 裁剪框选 / 编辑 / 表单）
        Workflow.WorkflowPages.Register(); // T19：工作流程组（步骤串联 / 预设 / 批量 / 记录）

        // 标签条与标签宿主共用 TabHostViewModel；其余区域仍用 MainViewModel（窗口 DataContext）。
        TabStrip.DataContext = TabHost;
        TabHostControl.DataContext = TabHost;
        TabHost.PropertyChanged += OnTabHostPropertyChanged;

        DataContextChanged += (_, _) => HookViewModel();
        HookViewModel();
        UpdateTabAreaState();
    }

    private void HookViewModel()
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (viewModel.SelectedTool is not null)
            {
                ShowToolPage(viewModel.SelectedTool, viewModel.Bridge);
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.SelectedTool)
            && sender is ViewModels.MainViewModel viewModel)
        {
            ShowToolPage(viewModel.SelectedTool, viewModel.Bridge);
        }
    }

    /// <summary>
    /// 工具选择 → 临时工具标签承载：ToolPageRegistry resolver 流不变，
    /// 承载方式从 DetailPlaceholder/ContentControl 改挂 TabControl。
    /// 同一工具重复点击仅激活现有标签，页面实例不重建【D16】。
    /// </summary>
    private void ShowToolPage(Models.Tool? tool, Bridge.EngineBridge? bridge)
    {
        if (tool is null)
        {
            return; // 取消选择不打断当前标签
        }

        TabHost.EnsureToolTab(tool, () => ToolPageRegistry.TryCreate(tool, bridge));
    }

    private void OnTabHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabHostViewModel.HasTabs) or nameof(TabHostViewModel.SelectedTab))
        {
            UpdateTabAreaState();
        }
    }

    /// <summary>
    /// 主区/左栏随标签状态联动（PRD 2.1/2.3/2.4）：
    /// 零标签 → 起始页占位 + 左栏收起；有标签 → 标签内容 + 左栏显示；
    /// 文档标签激活 → 阅读面板切换器骨架可见；工具标签无页面实例 → 详情占位兜底。
    /// </summary>
    private void UpdateTabAreaState()
    {
        var hasTabs = TabHost.HasTabs;
        var active = TabHost.SelectedTab;

        StartPagePlaceholder.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        TabHostControl.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        LeftPanel.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        LeftPanelColumn.MinWidth = hasTabs ? 220 : 0;
        LeftPanelColumn.Width = hasTabs ? new GridLength(264) : new GridLength(0);

        // 阅读面板骨架仅对文档标签显示（真实面板 T4 填充，搜索面板 T8）
        var documentTabActive = active?.Kind == TabKind.Document;
        ReaderPanelSwitcher.Visibility = documentTabActive ? Visibility.Visible : Visibility.Collapsed;
        ReaderPanelHost.Visibility = ReaderPanelSwitcher.Visibility;

        // 未被注册表认领的工具：详情占位兜底（保留原工具入口形态）
        var showDetailFallback = active is { Kind: TabKind.Tool, Content: null };
        DetailPlaceholder.Visibility = showDetailFallback ? Visibility.Visible : Visibility.Collapsed;
        if (showDetailFallback
            && DataContext is ViewModels.MainViewModel main
            && !ReferenceEquals(main.SelectedTool, active!.Tool))
        {
            main.SelectedTool = active.Tool; // 详情占位信息与激活标签保持一致
        }
    }

    // ---- 顶栏事件（FR-07/FR-08） ----

    /// <summary>＋ 新建标签：打开 .pdf 文件对话框；取消不产生空标签。</summary>
    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        // V2 挂点：拖拽落区与最近文件入口由 T6 起始页提供；此处保留最小「＋ → 打开」行为。
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 PDF 文档",
            Filter = "PDF 文档 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OpenDocumentTab(dialog.FileName);
    }

    /// <summary>
    /// 打开文档标签；同路径已存在则激活现有标签【D14】。
    /// 当前内容为阅读视图占位；T3/T7 将真实 ReaderView 写入标签 Content（挂点不变）。
    /// </summary>
    private void OpenDocumentTab(string path)
    {
        var tab = TabHost.OpenDocument(path, () => FindResource("ReaderPlaceholderView"));
        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"已打开 {tab.Title}";
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        // V2 挂点：关闭前若有未保存批注，先强制保存（PRD 3.5.2），失败阻止关闭并提示【ST-22】。
        // V1 无批注：直接关闭。
        if ((sender as FrameworkElement)?.DataContext is DocumentTabViewModel tab)
        {
            TabHost.CloseTab(tab);
        }
    }

    private void OnCloseActiveTabExecuted(object sender, ExecutedRoutedEventArgs e)
        => TabHost.CloseTab(TabHost.SelectedTab);

    private void OnFullTextSearchClick(object sender, RoutedEventArgs e)
        => ExecuteFullTextSearchStub();

    private void OnFullTextSearchExecuted(object sender, ExecutedRoutedEventArgs e)
        => ExecuteFullTextSearchStub();

    /// <summary>全文搜索事件桩：T8 将接管为搜索面板与页内高亮（PRD 3.3 FR-09/FR-10）。</summary>
    private void ExecuteFullTextSearchStub()
    {
        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = "全文搜索将在后续版本提供（Ctrl+F）。";
        }
    }

    // ---- 左栏阅读面板切换器（骨架；T4 填充真实面板内容） ----

    private void OnReaderPanelChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Content: string panelName } && ReaderPanelHint is not null)
        {
            ReaderPanelHint.Text = $"「{panelName}」面板占位（后续版本填充）。";
        }
    }

    /// <summary>ST-19 基础：标签条溢出时鼠标滚轮横向滚动（滚动指示箭头后续补齐）。</summary>
    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabStrip.Template.FindName("ScrollHost", TabStrip) is ScrollViewer host && host.ScrollableWidth > 0)
        {
            e.Handled = true;
            host.ScrollToHorizontalOffset(host.HorizontalOffset - e.Delta / 3.0);
        }
    }
}
