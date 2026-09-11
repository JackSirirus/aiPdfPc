// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Search;
using AIPDF.Windows.Tools;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using AIPDF.Windows.Views.Reader.Panels;

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

        // T8：窗口级唯一搜索栈——一个文本层服务 + 一个索引服务（MUST：每窗口各一份）
        _searchIndexService = new SearchIndexService(_searchTextService);

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

            InitStartPage(viewModel);
        }
    }

    // ---- T6 起始页（空态主界面，FR-33/34/35） ----

    /// <summary>起始页 VM：目录工具与主窗口同源（MainViewModel.Tools，避免重复加载 catalog.json）。</summary>
    private StartPageViewModel? _startPage;

    private void InitStartPage(MainViewModel main)
    {
        if (_startPage is not null)
        {
            return; // DataContext 重复变更不重建（最近列表状态保留）
        }

        _startPage = new StartPageViewModel(main.Tools);
        StartPage.DataContext = _startPage;
        _startPage.DocumentOpenRequested += OnStartPageDocumentOpenRequested;
        _startPage.ToolRequested += OnStartPageToolRequested;
    }

    private void OnStartPageDocumentOpenRequested(object? sender, string path)
        => OpenDocumentTab(path); // 最近列表记录已由 VM（OpenPickedFile/TryOpenRecent）完成

    /// <summary>
    /// 起始页点击工具 → T5 既有工具打开流（EnsureToolTab → 临时标签）。
    /// T7 挂点：单文件工具将改路由到右抽屉【D7】。
    /// </summary>
    private void OnStartPageToolRequested(object? sender, Models.Tool tool)
        => ShowToolPage(tool, (DataContext as MainViewModel)?.Bridge);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.SelectedTool)
            && sender is ViewModels.MainViewModel viewModel)
        {
            ShowToolPage(viewModel.SelectedTool, viewModel.Bridge);
        }
    }

    /// <summary>
    /// 工具选择 → 按 <see cref="ToolChannels"/> 路由：右抽屉（organize / pdfa）内联承载，
    /// 其余工具沿用 ToolPageRegistry resolver 流，作为临时工具标签承载。
    /// 同一工具重复点击仅激活现有实例，页面不重建【D16】。
    /// </summary>
    private void ShowToolPage(Models.Tool? tool, Bridge.EngineBridge? bridge)
    {
        if (tool is null)
        {
            return; // 取消选择不打断当前标签
        }

        if (ToolChannels.RoutesToRightDrawer(tool))
        {
            ShowRightDrawer(tool, bridge);
            return;
        }

        var tab = TabHost.EnsureToolTab(tool, () => ToolPageRegistry.TryCreate(tool, bridge));
        ApplyDocumentContext(tab.Content); // 临时标签：打开时若有激活文档则交接其路径
    }

    // ---- T7 右抽屉（单文件工具内联宿主，决策 7） ----

    /// <summary>右抽屉页面实例缓存（键 = 工具 id）；关闭只隐藏，重开复用同一实例【D16】，不重建。</summary>
    private readonly Dictionary<string, FrameworkElement> _rightDrawerPages = new(StringComparer.Ordinal);

    /// <summary>
    /// 显示右抽屉：页面实例按工具 id 缓存（不重建），标题取工具名，随后做文档上下文交接并展开抽屉。
    /// 解析仍走 <see cref="ToolPageRegistry.TryCreate"/>，resolver 模式不变。
    /// </summary>
    private void ShowRightDrawer(Models.Tool tool, Bridge.EngineBridge? bridge)
    {
        if (!_rightDrawerPages.TryGetValue(tool.Id, out var page))
        {
            var created = ToolPageRegistry.TryCreate(tool, bridge);
            if (created is null)
            {
                return; // 未被注册表认领：不应发生（organize / pdfa 均有注册），保持抽屉不动
            }

            _rightDrawerPages[tool.Id] = created;
            page = created;
        }

        RightDrawerTitle.Text = tool.Name;
        ApplyDocumentContext(page); // 每次打开都交接当前文档（单文件工具以新文件替换旧文件）
        RightDrawerContent.Content = page;
        RightDrawer.Visibility = Visibility.Visible;
    }

    private void OnCloseRightDrawerClick(object sender, RoutedEventArgs e)
        => RightDrawer.Visibility = Visibility.Collapsed;

    /// <summary>当前激活文档标签的路径；非文档标签（或零标签）返回 null。</summary>
    private string? ActiveDocumentPath()
        => TabHost.SelectedTab is { Kind: TabKind.Document, FilePath: { } path } ? path : null;

    /// <summary>
    /// 文档上下文交接：宿主页面（<see cref="FrameworkElement"/>）的视图模型实现
    /// <see cref="IToolDocumentReceiver"/> 时，把当前文档路径交给它（复用工具页既有 AddFiles 语义）。
    /// 无激活文档、或该工具页视图模型未实现接收口时不动作。
    /// </summary>
    private void ApplyDocumentContext(object? hosted)
    {
        if (hosted is not FrameworkElement element || element.DataContext is not IToolDocumentReceiver receiver)
        {
            return;
        }

        if (ActiveDocumentPath() is { } path)
        {
            receiver.AddFiles([path]);
        }
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
    /// 文档标签激活 → 阅读面板切换器可见并把文档挂到面板；工具标签无页面实例 → 详情占位兜底。
    /// </summary>
    private void UpdateTabAreaState()
    {
        var hasTabs = TabHost.HasTabs;
        var active = TabHost.SelectedTab;

        StartPage.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        TabHostControl.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        LeftPanel.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        LeftPanelColumn.MinWidth = hasTabs ? 220 : 0;
        LeftPanelColumn.Width = hasTabs ? new GridLength(264) : new GridLength(0);

        // 阅读面板仅对文档标签显示（搜索面板本体 T8）
        var documentTabActive = active?.Kind == TabKind.Document;
        ReaderPanelSwitcher.Visibility = documentTabActive ? Visibility.Visible : Visibility.Collapsed;
        ReaderPanelHost.Visibility = ReaderPanelSwitcher.Visibility;

        // T4：文档标签激活 → 左栏四面板挂接该文档（同文档幂等，切换工具标签不丢面板状态）
        if (documentTabActive && active!.FilePath is not null)
        {
            AttachPanelsToDocument(active.FilePath);
        }

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

    /// <summary>＋ 新建标签：打开 .pdf 文件对话框；取消不产生空标签。经起始页 VM 记录最近列表（FR-34）。</summary>
    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 PDF 文档",
            Filter = "PDF 文档 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (_startPage is not null)
        {
            _startPage.OpenPickedFile(dialog.FileName); // 记录最近列表 + DocumentOpenRequested → 建标签
        }
        else
        {
            OpenDocumentTab(dialog.FileName);
        }
    }

    /// <summary>
    /// 打开文档标签并承载真实阅读视图（连续滚动/缩放/跳页，FR-01/05/06）；同路径已存在则激活现有标签【D14】。
    /// ReaderViewModel 必须在本方法（UI 线程）构造——其构造捕获 SynchronizationContext 用于渲染结果回传。
    /// LoadDocument 同步且失败不抛：错误经 ErrorMessage/HasError 由 ReaderView 错误条呈现，故不做 try/catch 兜底。
    /// </summary>
    private void OpenDocumentTab(string path)
    {
        var fullPath = Path.GetFullPath(path);

        // 先判重：命中时只激活现有标签，不构造多余的阅读实例（避免 PDFium 句柄泄漏）
        var existing = TabHost.Tabs.FirstOrDefault(t =>
            t.Kind == TabKind.Document && string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            TabHost.SelectedTab = existing;
            if (DataContext is ViewModels.MainViewModel active)
            {
                active.StatusText = $"已打开 {existing.Title}";
            }
            return;
        }

        var vm = new ReaderViewModel();
        vm.LoadDocument(fullPath);
        var view = new ReaderView(vm);

        AttachSearchDocument(fullPath); // 先挂接搜索（面板在标签激活时绑定该 docId）

        var tab = TabHost.OpenDocument(fullPath, () => view);
        RegisterReaderTab(tab, vm, fullPath);

        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"已打开 {tab.Title}";
        }
    }

    // ---- 阅读视图接线（T3 ReaderViewModel ↔ 标签/面板联动） ----

    /// <summary>文档标签的阅读实例登记：TabId → VM + 规范化文档标识 + 页码订阅句柄（供关闭时反订阅与释放）。</summary>
    private sealed class ReaderTabState
    {
        public ReaderTabState(ReaderViewModel viewModel, string docId, PropertyChangedEventHandler handler)
        {
            ViewModel = viewModel;
            DocId = docId;
            Handler = handler;
        }

        public ReaderViewModel ViewModel { get; }

        public string DocId { get; }

        public PropertyChangedEventHandler Handler { get; }
    }

    /// <summary>已打开文档标签的阅读实例（键 = DocumentTabViewModel.TabId）。</summary>
    private readonly Dictionary<string, ReaderTabState> _readerTabs = new(StringComparer.Ordinal);

    /// <summary>登记新建文档标签的阅读实例并订阅其页码变化。</summary>
    private void RegisterReaderTab(DocumentTabViewModel tab, ReaderViewModel vm, string docId)
    {
        PropertyChangedEventHandler handler = (_, e) => OnReaderPageChanged(vm, docId, e);
        vm.PropertyChanged += handler;
        _readerTabs[tab.TabId] = new ReaderTabState(vm, docId, handler);
    }

    /// <summary>
    /// 阅读滚动/跳页 → 记录最近阅读页（书签「收藏当前页」数据源）+ 推送书签面板当前页 + 状态栏页码指示（FR-05）。
    /// </summary>
    private void OnReaderPageChanged(ReaderViewModel vm, string docId, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReaderViewModel.CurrentPageNumber))
        {
            return;
        }

        var zeroBased = vm.CurrentPageNumber - 1;
        if (zeroBased < 0)
        {
            return; // 无文档/加载失败（页码 0）不推送
        }

        _lastKnownPage[docId] = zeroBased;
        _bookmarks.SetCurrentPage(zeroBased);

        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"当前页 {vm.PageIndicatorText}";
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        // V2 挂点：关闭前若有未保存批注，先强制保存（PRD 3.5.2），失败阻止关闭并提示【ST-22】。
        // V1 无批注：直接关闭。
        if ((sender as FrameworkElement)?.DataContext is DocumentTabViewModel tab)
        {
            CloseTab(tab);
        }
    }

    private void OnCloseActiveTabExecuted(object sender, ExecutedRoutedEventArgs e)
        => CloseTab(TabHost.SelectedTab);

    /// <summary>关闭标签：文档标签关闭后反订阅并释放其阅读实例（关闭 PDFium 持久句柄，避免泄漏）。</summary>
    private void CloseTab(DocumentTabViewModel? tab)
    {
        if (tab is null || !TabHost.CloseTab(tab))
        {
            return;
        }

        if (_readerTabs.Remove(tab.TabId, out var reader))
        {
            reader.ViewModel.PropertyChanged -= reader.Handler;
            reader.ViewModel.Dispose();
            DetachSearchDocument(reader.DocId); // T8：解除索引 + 释放文本层句柄
        }
    }

    private void OnFullTextSearchClick(object sender, RoutedEventArgs e)
        => OpenFullTextSearch();

    private void OnFullTextSearchExecuted(object sender, ExecutedRoutedEventArgs e)
        => OpenFullTextSearch();

    /// <summary>
    /// 全文搜索面板接管【D6】【R17】：仅在文档标签激活时（否则状态栏提示）。
    /// 记住当前面板 → 切到搜索面板并聚焦搜索框；Esc/✕ 由 <see cref="ExitFullTextSearch"/> 恢复。
    /// </summary>
    private void OpenFullTextSearch()
    {
        if (TabHost.SelectedTab is not { Kind: TabKind.Document, FilePath: { } path })
        {
            if (DataContext is ViewModels.MainViewModel main)
            {
                main.StatusText = "请先打开一篇 PDF 文档再使用全文搜索。";
            }
            return;
        }

        EnsureSearchPanel(path);
        _panelState.OpenSearch();

        // 程序化勾选「搜索」RadioButton：Checked 事件 → OnReaderPanelChecked（OpenSearch 幂等）→ ShowReaderPanel
        PanelToggleSearch.IsChecked = true;
        ShowReaderPanel(ReaderPanelState.Search);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => _searchPanel?.FocusSearchBox()));
    }

    /// <summary>Esc / 面板 ✕：恢复进入搜索前的面板并清除高亮。</summary>
    private void ExitFullTextSearch()
    {
        if (!_panelState.ExitSearch())
        {
            return; // 未处于搜索面板
        }

        var key = _panelState.ActiveKey;
        var toggle = key switch
        {
            ReaderPanelState.Thumbs => PanelToggleThumbs,
            ReaderPanelState.Outline => PanelToggleOutline,
            ReaderPanelState.Bookmarks => PanelToggleBookmarks,
            _ => null,
        };

        if (toggle is not null && toggle.IsChecked != true)
        {
            toggle.IsChecked = true; // Checked → OnReaderPanelChecked → ShowReaderPanel(key)
        }
        else
        {
            ShowReaderPanel(key);
        }

        ClearActiveSearchHighlight();
    }

    /// <summary>窗口级 Esc：搜索面板激活时消费（恢复原面板）；其余 Esc 交给聚焦控件（如页码输入框）。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _panelState.IsSearchActive)
        {
            e.Handled = true;
            ExitFullTextSearch();
        }
    }

    // ---- T8 搜索栈接线 ----

    /// <summary>打开文档时挂接文本层句柄与索引（docId = 规范化路径，与阅读标签一致）。失败不阻断阅读。</summary>
    private void AttachSearchDocument(string fullPath)
    {
        if (_searchDocs.ContainsKey(fullPath))
        {
            return;
        }

        try
        {
            var doc = _searchTextService.OpenDocument(fullPath);
            _searchDocs[fullPath] = doc;
            _searchIndexService.Attach(fullPath, doc);
        }
        catch (Exception ex)
        {
            // 损坏/加密文件：阅读仍继续，搜索不可用（面板探测/搜索会给出提示）
            Trace.WriteLine($"[MainWindow] 搜索索引挂接失败: {fullPath} ({ex.Message})");
            if (DataContext is ViewModels.MainViewModel main)
            {
                main.StatusText = $"该文档搜索不可用：{ex.Message}";
            }
        }
    }

    /// <summary>关闭文档标签时解除索引并释放文本层句柄（避免句柄泄漏）。</summary>
    private void DetachSearchDocument(string docId)
    {
        try
        {
            _searchIndexService.Detach(docId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 搜索索引解除失败: {docId} ({ex.Message})");
        }

        if (_searchDocs.Remove(docId, out var doc))
        {
            doc.Dispose();
        }
    }

    /// <summary>
    /// 确保搜索面板绑定到指定文档：同 docId 幂等；换文档重建 VM（并反订阅旧事件）。
    /// 面板打开时触发文本层探测（扫描件立即置灰 + 去 OCR）。
    /// </summary>
    private void EnsureSearchPanel(string docId)
    {
        if (!_searchIndexService.IsAttached(docId))
        {
            return; // 未挂接（打开失败）：不建 VM，搜索入口保持空态
        }

        _searchPanel ??= new SearchPanel();

        if (_searchPanelVm is not null
            && string.Equals(_searchPanelVm.DocId, docId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_searchPanelVm is not null)
        {
            _searchPanelVm.HitActivated -= OnSearchHitActivated;
            _searchPanelVm.OcrRequested -= OnSearchOcrRequested;
            _searchPanelVm.CloseRequested -= OnSearchCloseRequested;
            _searchPanelVm.Dispose();
        }

        _searchProvider ??= new SearchIndexProviderAdapter(_searchIndexService);
        _searchPanelVm = new SearchPanelViewModel(_searchProvider, docId);
        _searchPanelVm.HitActivated += OnSearchHitActivated;
        _searchPanelVm.OcrRequested += OnSearchOcrRequested;
        _searchPanelVm.CloseRequested += OnSearchCloseRequested;
        _searchPanel.DataContext = _searchPanelVm;
        _searchPanelVm.BeginProbe();
    }

    /// <summary>命中激活：跳页 + 页内高亮（点击命中至少高亮其全部行矩形）。</summary>
    private void OnSearchHitActivated(object? sender, SearchResultItem item)
    {
        if (TabHost.SelectedTab is { Kind: TabKind.Document } active
            && _readerTabs.TryGetValue(active.TabId, out var reader))
        {
            var rects = item.Rects.Count > 0 ? item.Rects : [item.BoundingRect];
            reader.ViewModel.SetSearchHighlight(item.PageIndex, rects);
            reader.ViewModel.GoToPage(item.PageIndex + 1);
        }

        if (_attachedDocId is not null)
        {
            _lastKnownPage[_attachedDocId] = item.PageIndex;
            _bookmarks.SetCurrentPage(item.PageIndex);
        }

        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"第 {item.PageIndex + 1} 页 · 搜索命中";
        }
    }

    /// <summary>无文本层 → 去 OCR：复用 T7 工具流打开 ocr 工具（幂等，已开仅激活【D16】）。</summary>
    private void OnSearchOcrRequested(object? sender, EventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel main)
        {
            return;
        }

        var ocr = main.Tools.FirstOrDefault(t => string.Equals(t.Id, "ocr", StringComparison.Ordinal));
        if (ocr is null)
        {
            return;
        }

        if (!ReferenceEquals(main.SelectedTool, ocr))
        {
            main.SelectedTool = ocr; // 触发既有关联流（OnViewModelPropertyChanged → ShowToolPage）
        }
        ShowToolPage(ocr, main.Bridge);
        main.StatusText = "已打开 OCR 工具：识别完成后再回来搜索。";
    }

    private void OnSearchCloseRequested(object? sender, EventArgs e)
        => ExitFullTextSearch();

    /// <summary>清除激活文档阅读视图的页内搜索高亮。</summary>
    private void ClearActiveSearchHighlight()
    {
        if (TabHost.SelectedTab is { Kind: TabKind.Document } active
            && _readerTabs.TryGetValue(active.TabId, out var reader))
        {
            reader.ViewModel.ClearSearchHighlight();
        }
    }

    // ---- T4 左栏阅读面板（缩略图/目录/书签/搜索槽位） ----

    /// <summary>左栏面板专用渲染服务：与阅读视图（T3/T7）的服务实例相互独立，V1 刻意隔离。</summary>
    private PdfiumRenderService? _panelRenderer;

    private ThumbPanel? _thumbPanel;
    private OutlinePanel? _outlinePanel;
    private BookmarkPanel? _bookmarkPanel;

    /// <summary>会话书签 VM（单实例，按 docId 分存；关闭应用即失效，V2 持久化挂点见 VM 注释）。</summary>
    private readonly BookmarkPanelViewModel _bookmarks = new();

    /// <summary>当前挂接的文档标识（渲染服务返回的规范化路径）。</summary>
    private string? _attachedDocId;

    /// <summary>各文档最近已知阅读页（0 基；书签「收藏当前页」的数据源，T7 接 ReaderView 后实时推送）。</summary>
    private readonly Dictionary<string, int> _lastKnownPage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>左栏面板接管状态机（T8）：Ctrl+F 记住原面板，Esc/✕ 恢复【D6】【R17】。</summary>
    private readonly ReaderPanelState _panelState = new();

    // ---- T8 全文搜索（面板接管 + 页内高亮 + 无文本层降级） ----

    /// <summary>窗口级唯一文本层服务（T2）；<see cref="_searchIndexService"/> 共用其句柄生命周期。</summary>
    private readonly PdfiumTextService _searchTextService = new();

    /// <summary>窗口级唯一搜索索引服务（T2）：挂接多个文档（docId = 规范化路径）。</summary>
    private readonly SearchIndexService _searchIndexService;

    /// <summary>T2 服务 → 搜索面板消费接口的适配器（窗口内单例）。</summary>
    private SearchIndexProviderAdapter? _searchProvider;

    /// <summary>docId → 文本层文档句柄（与 <see cref="_readerTabs"/> 的 docId 同一规范化路径）。</summary>
    private readonly Dictionary<string, PdfiumTextService.TextDocument> _searchDocs = new(StringComparer.OrdinalIgnoreCase);

    private SearchPanel? _searchPanel;
    private SearchPanelViewModel? _searchPanelVm;

    /// <summary>
    /// 把文档挂到左栏面板（同 docId 幂等）。文档打不开（损坏/缺文件）时状态栏提示并保持面板空态。
    /// V2 挂点：标签关闭时应 renderer.CloseDocument + 书签清理，V1 会话内保留。
    /// </summary>
    private void AttachPanelsToDocument(string path)
    {
        _panelRenderer ??= new PdfiumRenderService();

        string docId;
        try
        {
            docId = _panelRenderer.OpenDocument(path);
        }
        catch (Exception ex)
        {
            if (DataContext is ViewModels.MainViewModel main)
            {
                main.StatusText = $"左栏面板无法解析该文档：{ex.Message}";
            }
            return;
        }

        if (_attachedDocId == docId)
        {
            return;
        }

        _attachedDocId = docId;
        var pageCount = Math.Max(0, _panelRenderer.GetPageCount(docId));

        EnsureSearchPanel(docId); // T8：搜索面板绑定到该文档（同一 docId）

        _thumbPanel = new ThumbPanel
        {
            DataContext = new ThumbPanelViewModel(
                new PdfiumThumbnailRendererAdapter(_panelRenderer), docId, pageCount),
        };
        _outlinePanel = new OutlinePanel
        {
            DataContext = new OutlinePanelViewModel(_panelRenderer, docId),
        };
        _bookmarkPanel = new BookmarkPanel
        {
            DataContext = _bookmarks,
        };
        (_thumbPanel.DataContext as ThumbPanelViewModel)!.NavigateRequested += OnPanelNavigateRequested;
        (_outlinePanel.DataContext as OutlinePanelViewModel)!.NavigateRequested += OnPanelNavigateRequested;
        _bookmarks.NavigateRequested += OnPanelNavigateRequested;

        _bookmarks.AttachDocument(docId);
        _bookmarks.SetCurrentPage(_lastKnownPage.GetValueOrDefault(docId, 0));

        ShowReaderPanel(_panelState.ActiveKey); // 若当前正显示某面板，切换到新文档的实例
    }

    /// <summary>
    /// 面板跳页（缩略图/目录/书签，0 基）：滚动激活文档标签的阅读视图到位（ReaderViewModel.GoToPage 为 1 基）。
    /// 保留最近阅读页与书签当前页的既有记账。
    /// </summary>
    private void OnPanelNavigateRequested(object? sender, int pageIndex)
    {
        if (TabHost.SelectedTab is { Kind: TabKind.Document } active
            && _readerTabs.TryGetValue(active.TabId, out var reader))
        {
            reader.ViewModel.GoToPage(pageIndex + 1);
        }

        if (_attachedDocId is not null)
        {
            _lastKnownPage[_attachedDocId] = pageIndex;
            _bookmarks.SetCurrentPage(pageIndex);
        }

        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"第 {pageIndex + 1} 页";
        }
    }

    /// <summary>面板切换器事件（RadioButton Tag = 面板键）。搜索入口等同 Ctrl+F 的接管语义。</summary>
    private void OnReaderPanelChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key, IsChecked: true })
        {
            if (key == ReaderPanelState.Search)
            {
                _panelState.OpenSearch();
            }
            else
            {
                _panelState.Activate(key);
            }
            ShowReaderPanel(key);
        }
    }

    private void ShowReaderPanel(string key)
    {
        if (ReaderPanelHost is null)
        {
            return; // InitializeComponent 期间初始 IsChecked 触发（T5 已知时序）
        }

        // 离开搜索面板：清除页内高亮（避免切到缩略图/目录后残留）
        if (key != ReaderPanelState.Search)
        {
            ClearActiveSearchHighlight();
        }

        ReaderPanelHost.Content = key switch
        {
            "thumbs" => (object?)_thumbPanel,
            "outline" => (object?)_outlinePanel,
            "bookmarks" => (object?)_bookmarkPanel,
            "search" => (object?)_searchPanel,
            _ => null,
        };
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
