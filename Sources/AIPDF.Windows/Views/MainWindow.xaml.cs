// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Search;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tools;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using AIPDF.Windows.Views.Reader.Panels;
using AIPDF.Windows.Views.Settings;
using AIPDF.Windows.Views.Wizard;

namespace AIPDF.Windows.Views;

/// <summary>
/// 主窗口：顶栏（品牌 + 标签条 + ＋/搜索/⚙）+ 主区（左栏 / TabControl 标签宿主）+ 状态栏。
/// 多标签核心逻辑在 <see cref="TabHostViewModel"/>；本类只做视图联动与事件接线。
/// </summary>
public partial class MainWindow : Window, ISessionRestoreTarget
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

        // T8：书签并入 T5 每文件阅读状态（Q3）；标题取目标页首行（FR-04，复用搜索文本层索引）。
        _bookmarks = new BookmarkPanelViewModel(_readingStates)
        {
            TitleProvider = new SearchTextFirstLineProvider(_searchIndexService),
        };

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

        // T4：关闭标签前强制 flush 未保存批注（失败阻止关闭，ST-22）
        TabHost.BeforeCloseAsync = BeforeCloseTabAsync;

        // T4：状态栏 savepill（四态；失败可点击重试）
        SavePill.DataContext = _savePill;
        _savePill.RetryRequested += async (_, _) => await RetryActiveDocumentSaveAsync();

        // V3 T4：非 PDF 向导结果 Toast 点击 → 打开输出文件夹【ST-29】。
        QueueToast.MouseLeftButtonDown += OnWizardToastClick;

        // T5：启动时孤儿 GC 一次（>90 天未访问删除、超 100 条淘汰最旧，FR-27）；后台执行不阻塞启动。
        RunReadingStateGarbageCollect();

        DataContextChanged += (_, _) => HookViewModel();
        HookViewModel();
        UpdateTabAreaState();

        // V3 T5：设置默认左栏面板作为「无标签/起始页初始态」（FR-38 项 2；文件标签仍以 reading-state 为准，BA-6）。
        // 下方会话恢复若带全局激活面板，会覆盖本初始值（会话仍是启动恢复的既有真相源）。
        ApplyPanelSelection(SettingsInjection.ResolveDefaultPanelKey(_settingsStore.Load().DefaultPanelKey));

        // T6：启动恢复会话（window 几何 → 左栏 → 标签异步；CLI/损坏 session 走默认状态）
        RestoreSession();
    }

    /// <summary>已接线队列取消事件的 MainViewModel（DataContext 变更时反订阅，避免重复弹 Toast）。</summary>
    private MainViewModel? _hookedQueueVm;

    /// <summary>队列取消 Toast 计时器（2s 自动隐藏）。</summary>
    private DispatcherTimer? _queueToastTimer;

    /// <summary>V3 T4：非 PDF 向导结果待打开的输出文件夹（Toast 点击消费）。</summary>
    private string? _wizardResultFolder;

    private void HookViewModel()
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // V2 T7：状态栏队列 UI 之外，订阅「执行中任务被取消」以弹 Toast（排队项取消不触发）。
            if (_hookedQueueVm is not null)
            {
                _hookedQueueVm.Queue.RunningTaskCancelled -= OnQueueRunningTaskCancelled;
            }

            _hookedQueueVm = viewModel;
            viewModel.Queue.RunningTaskCancelled += OnQueueRunningTaskCancelled;

            // V2 T10 FR-21：队列型工具（优化/格式转换/页面整理/安全/工作流程）启动前强制 flush
            // 未保存批注；flush 失败阻止启动并给出「强制继续（批注可能丢失）/取消」选择。
            viewModel.Queue.BeforeStart = (_, token) => FlushAnnotationsBeforeToolAsync(token);

            if (viewModel.SelectedTool is not null)
            {
                ShowToolPage(viewModel.SelectedTool, viewModel.Bridge);
            }

            InitStartPage(viewModel);
        }
    }

    /// <summary>执行中任务被取消 → 状态栏上方 Toast「任务已取消：X」；2s 后自动隐藏。</summary>
    private void OnQueueRunningTaskCancelled(object? sender, ToolTaskItem item)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            QueueToastText.Text = $"任务已取消：{item.Name}";
            QueueToast.Visibility = Visibility.Visible;
            _queueToastTimer?.Stop();
            _queueToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _queueToastTimer.Tick += (_, _) =>
            {
                _queueToastTimer?.Stop();
                QueueToast.Visibility = Visibility.Collapsed;
            };
            _queueToastTimer.Start();
        }));
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

        _startPage = new StartPageViewModel(
            main.Tools,
            // V3 T5：最近文件上限来自设置（DefaultMaxEntries = 20 为旧行为）；保存设置后经 ApplyRecentFilesLimit 即时更新。
            new RecentFilesStore(maxEntries: SettingsInjection.ResolveRecentFilesLimit(_settingsStore.Load().RecentFilesLimit)));
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

        // V2 T10：工具标签激活会改变 ActiveDocumentPath，先在打开前捕获当前文档（交接 + redact 判定共用）。
        var activeDocument = ActiveDocumentPath();

        // FR-22（Q2 裁决：redact 删除全部批注）：文档存在批注（含未保存）时先弹模态警告，取消则不进入参数面板。
        if (string.Equals(tool.Id, "redact", StringComparison.Ordinal)
            && !ConfirmRedactAnnotationDeletion(activeDocument))
        {
            return;
        }

        if (ToolChannels.RoutesToRightDrawer(tool))
        {
            ShowRightDrawer(tool, bridge);
            return;
        }

        // V3 T4：向导工具（21 个）在临时标签兜底之前走居中模态向导。
        if (ToolChannels.RoutesToWizard(tool))
        {
            ShowWizard(tool, bridge, activeDocument);
            return;
        }

        var tab = TabHost.EnsureToolTab(tool, () => ToolPageRegistry.TryCreate(tool, bridge));
        ApplyDocumentContext(tab.Content, activeDocument); // 临时标签：打开时交接当前文档（含被激活前的文档）
    }

    // ---- V3 T4 居中模态向导（Wizard 通道） ----

    /// <summary>
    /// 向导工具承载：<see cref="ToolPageRegistry.TryCreate"/> 每次新建 fresh 页面 + VM——绝不复用
    /// <see cref="TabHost"/> 缓存实例（WPF 单父级），向导会话内供参数步往返（D16），关闭即丢弃（Q10）。
    /// <para>
    /// 复刻标签宿主的上下文接线：<see cref="ApplyDocumentContext"/>（<see cref="IToolDocumentReceiver"/> 文档注入 +
    /// <see cref="IToolStartGated"/><c>ToolTaskQueue.BeforeStart</c> 的 FR-21 flush 门禁）；向导提交走同一全局串行
    /// 队列，不绕过「工具执行前 flush 未保存批注」保证。结果处置：PDF → 开新标签【D5】；非 PDF → Toast + 打开文件夹【ST-29】。
    /// </para>
    /// </summary>
    private void ShowWizard(Models.Tool tool, Bridge.EngineBridge? bridge, string? activeDocument)
    {
        // fresh 实例：每次新建（ToolPageRegistry.TryCreate 内部 new），不进 TabHost 缓存。
        var page = ToolPageRegistry.TryCreate(tool, bridge);

        // 门禁接线（review M1）：文档上下文 + IToolStartGated.StartGate（编辑组直调引擎）；队列型经 Queue.BeforeStart。
        ApplyDocumentContext(page, activeDocument);

        var adapter = page?.DataContext as IWizardToolAdapter;
        var queue = (DataContext as ViewModels.MainViewModel)?.Queue ?? ToolTaskQueue.Shared;
        var viewModel = new WizardViewModel(tool, adapter, queue)
        {
            HostedContent = page, // 参数步内容 = fresh 工具页实例
        };
        viewModel.PinCurrentDocument(activeDocument); // ST-26：当前文档自动入选置顶并标记「当前文档」

        var dialog = new WizardDialog(viewModel);
        if (IsLoaded)
        {
            dialog.Owner = this;
        }

        var submitted = dialog.ShowDialog() == true;
        var results = dialog.Results;
        if (!submitted || results.Count == 0)
        {
            return; // Esc / 点遮罩取消：草稿已丢弃，不产生输出【ST-26】
        }

        var outputs = results.Where(r => r.Ok).SelectMany(r => r.Outputs).ToList();
        var anyFailed = results.Any(r => !r.Ok);
        if (WizardResultPolicy.Decide(outputs, anyFailed) == WizardResultAction.OpenTabs)
        {
            foreach (var output in outputs)
            {
                OpenDocumentTab(output); // D5：PDF 结果开新标签
            }

            if (DataContext is ViewModels.MainViewModel opened)
            {
                opened.StatusText = $"「{tool.Name}」完成：已打开 {outputs.Count} 个结果标签";
            }

            return;
        }

        // ST-29：非 PDF 结果 Toast + 「打开文件夹」（点击 Toast 打开输出目录）。
        var folder = results.FirstOrDefault(r => r.Ok && !string.IsNullOrEmpty(r.OutputDir))?.OutputDir
            ?? outputs.Select(Path.GetDirectoryName).FirstOrDefault(d => !string.IsNullOrEmpty(d));
        ShowWizardFolderToast(folder);
        if (DataContext is ViewModels.MainViewModel done)
        {
            done.StatusText = anyFailed ? $"「{tool.Name}」部分任务失败。" : $"「{tool.Name}」完成：结果已生成。";
        }
    }

    /// <summary>非 PDF 向导结果：可点击 Toast（点击打开输出文件夹，4s 自动隐藏）【ST-29】。</summary>
    private void ShowWizardFolderToast(string? folder)
    {
        _wizardResultFolder = folder;
        QueueToast.IsHitTestVisible = true;
        QueueToastText.Text = string.IsNullOrEmpty(folder) ? "已生成结果。" : "已生成结果：点击此处打开输出文件夹";
        QueueToast.Visibility = Visibility.Visible;

        _queueToastTimer?.Stop();
        _queueToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _queueToastTimer.Tick += (_, _) =>
        {
            _queueToastTimer?.Stop();
            QueueToast.Visibility = Visibility.Collapsed;
            QueueToast.IsHitTestVisible = false;
            _wizardResultFolder = null;
        };
        _queueToastTimer.Start();
    }

    /// <summary>非 PDF 结果 Toast 点击：打开输出文件夹（无文件夹时忽略）。</summary>
    private void OnWizardToastClick(object sender, MouseButtonEventArgs e)
    {
        if (_wizardResultFolder is not { Length: > 0 } folder)
        {
            return;
        }

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 打开输出文件夹失败: {folder} ({ex.Message})");
        }
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
    private void ApplyDocumentContext(object? hosted) => ApplyDocumentContext(hosted, ActiveDocumentPath());

    /// <summary>
    /// 文档上下文交接：宿主页面（<see cref="FrameworkElement"/>）的视图模型实现
    /// <see cref="IToolDocumentReceiver"/> 时，把当前文档路径交给它（复用工具页既有 AddFiles 语义）；
    /// 同时给不经 T7 队列的执行入口（<see cref="IToolStartGated"/>，编辑组）挂接 FR-21 启动前 flush 门禁。
    /// 无激活文档、或该工具页视图模型未实现对应接收口时不动作。
    /// </summary>
    private void ApplyDocumentContext(object? hosted, string? documentPath)
    {
        if (hosted is not FrameworkElement element || element.DataContext is null)
        {
            return;
        }

        // V2 T10 FR-21：编辑组工具直接调引擎（不入队），启动门禁在此挂接。
        if (element.DataContext is IToolStartGated gated)
        {
            gated.StartGate = FlushAnnotationsBeforeToolAsync;
        }

        // V3 T5（FR-38 项 DefaultOutputDir）：仅作工具 VM 的 OutputDir 初始种子，空 = 沿用页面选择（现状）；
        // 仅当 VM 的 OutputDir 尚为空时写入，不覆盖用户/页面已选目录，不改变引擎输出目录语义。
        SettingsInjection.SeedOutputDir(element.DataContext, _settingsStore.Load().DefaultOutputDir);

        if (element.DataContext is IToolDocumentReceiver receiver && documentPath is { } path)
        {
            receiver.AddFiles([path]);
        }
    }

    // ---- T2 浮菜单「用工具处理」：既有通道打开 + 当前文档置顶 + 页范围预填 ----

    /// <summary>
    /// 阅读视图浮菜单子菜单发起：经 <see cref="ShowToolPage"/> 走既有宿主（右抽屉 / 临时标签），
    /// 再把当前文档置顶并标记「当前文档」；仅对确有页范围字段的工具（split/rotate）预填「当前页-当前页」。
    /// <see cref="ToolLaunchRequest.ToolId"/> 为 null 表示「更多工具…」，打开工具箱面板。
    /// </summary>
    private void OnReaderToolLaunchRequested(object? sender, ToolLaunchRequest e)
    {
        if (DataContext is not ViewModels.MainViewModel main)
        {
            return;
        }

        if (e.ToolId is null)
        {
            ToolboxPanel.Visibility = ToolboxPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        var tool = main.Tools.FirstOrDefault(t => string.Equals(t.Id, e.ToolId, StringComparison.Ordinal));
        if (tool is null)
        {
            main.StatusText = $"未找到工具：{e.ToolId}";
            return;
        }

        ShowToolPage(tool, main.Bridge); // 既有通道（右抽屉 / 临时标签）

        var hosted = ResolveHostedToolPage(tool);
        if (hosted?.DataContext is not ICurrentDocumentPinnable pinnable)
        {
            return; // 该工具页未实现统一置顶契约：不降级为仅传路径
        }

        if (ActiveDocumentPath() is { } path)
        {
            pinnable.PinCurrentDocument(path); // 入选 + 置顶 + 标记「当前文档」
        }

        if (e.PrefillPageRange && pinnable.SupportsPageRangePrefill)
        {
            pinnable.PrefillPageRange(e.PageStart, e.PageEnd);
        }

        main.StatusText = $"已用「{tool.Name}」处理当前文档";
    }

    /// <summary>解析工具页当前承载的视图（右抽屉实例缓存 / 临时标签内容）。</summary>
    private FrameworkElement? ResolveHostedToolPage(Models.Tool tool)
    {
        if (ToolChannels.RoutesToRightDrawer(tool))
        {
            return _rightDrawerPages.GetValueOrDefault(tool.Id);
        }

        var tab = TabHost.Tabs.FirstOrDefault(t =>
            t.Kind == TabKind.Tool && t.Tool is not null
            && string.Equals(t.Tool.Id, tool.Id, StringComparison.Ordinal));
        return tab?.Content as FrameworkElement;
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
        LeftPanelColumn.MinWidth = hasTabs ? MinLeftPanelWidth : 0;

        // T6 左栏宽度恢复：显示时套用记忆宽度（不因切标签重置用户拖动值）；隐藏时记住当前宽度。
        if (hasTabs)
        {
            if (LeftPanelColumn.Width.Value <= 0)
            {
                LeftPanelColumn.Width = new GridLength(ClampLeftPanelWidth(_leftPanelWidth));
            }
        }
        else
        {
            if (LeftPanelColumn.Width.Value > 0)
            {
                _leftPanelWidth = LeftPanelColumn.Width.Value;
            }

            LeftPanelColumn.Width = new GridLength(0);
        }

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

        // T4：savepill 随激活文档标签刷新（文档 → 显示该文档状态；工具/零标签 → 隐藏）
        UpdateSavePillForActiveTab();
    }

    // ---- T20 工具箱滑出面板（顶栏入口 + 主区左对齐面板） ----

    /// <summary>顶栏「工具箱」按钮：展开/收起左对齐工具面板；与是否有标签无关，始终可用。</summary>
    private void OnToolboxClick(object sender, RoutedEventArgs e)
        => ToolboxPanel.Visibility = ToolboxPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>面板头 ✕ 关闭工具箱（只隐藏，工具页实例由各自宿主缓存，不重建）。</summary>
    private void OnCloseToolboxClick(object sender, RoutedEventArgs e)
        => ToolboxPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 工具箱条目选择：显式走 <see cref="ShowToolPage"/>（与起始页工具入口同一路径，刻意不另接
    /// SelectedTool 绑定，避免出现第二条打开路径）；随即复位选中项以便重复点击同一工具，并关闭面板。
    /// </summary>
    private void OnToolboxToolSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not Models.Tool tool)
        {
            return;
        }

        list.SelectedIndex = -1; // 复位：允许再次点击同一工具
        ToolboxPanel.Visibility = Visibility.Collapsed;
        ShowToolPage(tool, (DataContext as ViewModels.MainViewModel)?.Bridge);
    }

    // ---- V3 T5 设置 / 关于·许可（FR-38 / AC-38.1 / AC-38.3） ----

    /// <summary>⚙ 按钮：在按钮下方展开「设置 | 关于·许可」下拉菜单（无「检查更新」，AC-38.1）。</summary>
    private void OnSettingsMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 菜单「设置」：打开设置模态（7 项）。保存即时写盘；缩放/面板/DPI/输出目录等注入仅对
    /// 「随后新打开的文档」生效（FR-38 边界，已打开文档不变）；最近文件上限对当前会话即时生效。
    /// </summary>
    private void OnSettingsMenuItemClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settingsStore);
        if (IsLoaded)
        {
            dialog.Owner = this;
        }

        dialog.ShowDialog();
        if (dialog.SavedSettings is { } saved)
        {
            _startPage?.ApplyRecentFilesLimit(SettingsInjection.ResolveRecentFilesLimit(saved.RecentFilesLimit));
            if (DataContext is ViewModels.MainViewModel main)
            {
                main.StatusText = "设置已保存（对随后新打开的文档生效）。";
            }
        }
    }

    /// <summary>菜单「关于·许可」：打开关于模态（版本 / AGPL-3.0-only / LICENSE·NOTICE·第三方声明入口）。</summary>
    private void OnAboutMenuItemClick(object sender, RoutedEventArgs e)
        => AboutDialog.Show(IsLoaded ? this : null);

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
    /// T9：加密文档走【ST-04】模态密码框；解锁成功驻留密码（进程内存），取消/Esc 不产生标签。
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

        // V3 T5：打开前读取设置——仅对本次新打开的文档生效（FR-38 边界）。
        // DefaultRenderDpi 覆盖渲染基准（不参与打印），须在 LoadDocument 前注入以便首次渲染即按新基准。
        var settings = _settingsStore.Load();
        var vm = new ReaderViewModel
        {
            RenderBaseDpi = SettingsInjection.ResolveRenderBaseDpi(settings.DefaultRenderDpi),
        };

        // T9：优先用会话驻留密码免密打开（同路径解锁过一次即命中）。
        vm.LoadDocument(fullPath, _credentials.Get(fullPath));

        // T9【ST-04】加密文档：模态密码框；错误行内红字、不清空、不限次数；取消/Esc 不产生标签。
        if (!vm.HasDocument && vm.LoadRequiresPassword && !PromptForPassword(fullPath, vm))
        {
            vm.Dispose(); // 取消：释放部分创建的渲染服务，不产生残留标签
            return;
        }

        var view = new ReaderView(vm);

        // T5：视图先订阅 ScrollRequested，再套用阅读状态（缩放/页码/面板/书签）——否则恢复的滚动请求丢失。
        // V3 T5：reading-state 未命中时以设置 DefaultZoom / DefaultPanelKey 作为新打开文档的初始值。
        RestoreReadingState(fullPath, vm, settings);

        // T2：浮菜单「用工具处理」接线（可用性探测 + 置顶/页范围预填交回宿主）
        view.ToolAvailabilityProvider = ToolAvailabilityProbe.Check;
        view.ToolLaunchRequested += OnReaderToolLaunchRequested;

        AttachSearchDocument(fullPath); // 先挂接搜索（面板在标签激活时绑定该 docId）

        // T4：登记批注保存来源（防抖/快速通道）并采集冲突基线。
        // T9：携带会话驻留密码 → 加密文档的 save_annotations 逐请求带 options.password，
        //     保存成功后的 close-first 重载亦用同一密码重开句柄。
        EnsureAnnotationSaveService().RegisterDocument(
            new ReaderAnnotationSaveSource(vm, fullPath, _credentials.Get(fullPath)));
        CaptureConflictBaseline(fullPath);

        var tab = TabHost.OpenDocument(fullPath, () => view);
        RegisterReaderTab(tab, vm, fullPath);

        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"已打开 {tab.Title}";
        }
    }

    /// <summary>
    /// FR-32：接受启动参数 / 单实例转发传入的文档路径，每个文件开一个标签（同路径去重【D14】）。
    /// 在 UI 线程调用（非 UI 线程自动 marshal）；单个文件打开失败只记录诊断，不阻断其余文件。
    /// </summary>
    public void OpenExternalDocuments(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Dispatcher.CheckAccess())
        {
            var copy = paths.ToList();
            Dispatcher.BeginInvoke(new Action(() => OpenExternalDocuments(copy)));
            return;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                OpenDocumentTab(path);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MainWindow] 打开外部文档失败: {path} ({ex.Message})");
            }
        }
    }

    /// <summary>
    /// T9【ST-04】：弹出模态密码框反复尝试解锁同一 <paramref name="vm"/>（不清空、不限次数）。
    /// 每次尝试把输入密码交给 <see cref="ReaderViewModel.LoadDocument"/>；成功即驻留进
    /// <see cref="SessionCredentialStore"/> 并返回 true。<b>取消 / Esc 返回 false</b>（宿主不产生标签）。
    /// 密码仅存在于对话框回调闭包与凭据存储内存中，不落盘、不写日志、不进异常消息。
    /// </summary>
    private bool PromptForPassword(string fullPath, ReaderViewModel vm)
    {
        var dialog = new PasswordPromptDialog(Path.GetFileName(fullPath), password =>
        {
            vm.LoadDocument(fullPath, password);
            if (!vm.HasDocument)
            {
                return false; // 密码错误（或该文档仍无法打开）：对话框保持打开重试
            }

            _credentials.Store(fullPath, password); // 解锁成功：进程内存驻留，后续免密
            return true;
        });

        if (IsLoaded)
        {
            dialog.Owner = this; // 启动恢复期窗口尚未 Loaded 时不设 Owner，避免 InvalidOperationException
        }

        return dialog.ShowDialog() == true;
    }

    // ---- 阅读视图接线（T3 ReaderViewModel ↔ 标签/面板联动） ----

    /// <summary>文档标签的阅读实例登记：TabId → VM + 规范化文档标识 + 事件句柄（供关闭时反订阅与释放）。</summary>
    private sealed class ReaderTabState
    {
        public ReaderTabState(ReaderViewModel viewModel, string docId, PropertyChangedEventHandler handler,
            EventHandler<AnnotationChangedEventArgs> annotationHandler)
        {
            ViewModel = viewModel;
            DocId = docId;
            Handler = handler;
            AnnotationHandler = annotationHandler;
        }

        public ReaderViewModel ViewModel { get; }

        public string DocId { get; }

        public PropertyChangedEventHandler Handler { get; }

        public EventHandler<AnnotationChangedEventArgs> AnnotationHandler { get; }
    }

    /// <summary>已打开文档标签的阅读实例（键 = DocumentTabViewModel.TabId）。</summary>
    private readonly Dictionary<string, ReaderTabState> _readerTabs = new(StringComparer.Ordinal);

    /// <summary>登记新建文档标签的阅读实例并订阅其页码/批注变化。</summary>
    private void RegisterReaderTab(DocumentTabViewModel tab, ReaderViewModel vm, string docId)
    {
        PropertyChangedEventHandler handler = (_, e) => OnReaderPageChanged(vm, docId, e);
        EventHandler<AnnotationChangedEventArgs> annotationHandler = (_, e) => OnReaderAnnotationChanged(docId, e);
        vm.PropertyChanged += handler;
        vm.AnnotationChanged += annotationHandler;
        _readerTabs[tab.TabId] = new ReaderTabState(vm, docId, handler, annotationHandler);
    }

    /// <summary>批注新增/修改/删除 → 保存服务：重置 2s 防抖（删除另记录引擎 delete）。</summary>
    private void OnReaderAnnotationChanged(string docId, AnnotationChangedEventArgs e)
    {
        var service = EnsureAnnotationSaveService();
        if (e.Kind == AnnotationChangeKind.Removed)
        {
            service.NotifyAnnotationRemoved(docId, e.Record);
        }
        else
        {
            service.NotifyAnnotationsChanged(docId);
        }
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

        // 阅读位置联动：左栏缩略图面板滚动到当前阅读页（仅当该面板正在显示）
        if (_thumbPanel is { IsVisible: true } && string.Equals(_attachedDocId, docId, StringComparison.OrdinalIgnoreCase))
        {
            _thumbPanel.ScrollToPage(zeroBased);
        }

        if (DataContext is ViewModels.MainViewModel main)
        {
            main.StatusText = $"当前页 {vm.PageIndicatorText}";
        }
    }

    private async void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        // T4：关闭前若有未保存批注，TabHost.CloseTabAsync 经 BeforeCloseAsync 强制保存（跳过防抖）；
        // 失败阻止关闭并提示【ST-22】。成功后才执行本窗口的句柄/面板/搜索清理。
        if ((sender as FrameworkElement)?.DataContext is DocumentTabViewModel tab
            && await TabHost.CloseTabAsync(tab))
        {
            OnTabClosed(tab);
        }
    }

    private async void OnCloseActiveTabExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var tab = TabHost.SelectedTab;
        if (tab is not null && await TabHost.CloseTabAsync(tab))
        {
            OnTabClosed(tab);
        }
    }

    /// <summary>TabHost.CloseTabAsync 的门禁：未保存批注强制 flush，失败返回 false 拦截关闭（AC-14.3/AC-8.2）。</summary>
    private async Task<bool> BeforeCloseTabAsync(DocumentTabViewModel tab)
    {
        if (tab.Kind != TabKind.Document || tab.FilePath is not { } path) return true;

        var service = EnsureAnnotationSaveService();
        var hasUnsaved = service.HasPending(path);
        if (!hasUnsaved) return true;

        var outcome = await service.FlushAsync(path);
        if (CloseFlushDecisionLogic.Decide(true, outcome.Succeeded) == CloseFlushDecision.Blocked)
        {
            MessageBox.Show(
                this,
                $"批注保存失败，标签未关闭。\n{(string.IsNullOrEmpty(outcome.Error) ? "请稍后重试。" : outcome.Error)}",
                "保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    /// <summary>savepill 失败态点击：对当前文档强制 flush 重试。</summary>
    private async Task RetryActiveDocumentSaveAsync()
    {
        if (ActiveDocumentPath() is not { } path) return;
        await EnsureAnnotationSaveService().FlushAsync(path);
    }

    // ------------------------------------------------------------ T10 FR-21/22 批注 × 工具协作

    /// <summary>
    /// FR-21：工具执行前强制 flush 当前所有未保存批注（跳过防抖，每篇最多 30s）。成功才放行；
    /// 失败弹「批注保存失败，请重试」并给出「强制继续（批注可能丢失）/取消」（AC-21.1/21.2）。
    /// flush 走 <see cref="AnnotationSaveService"/> 快速通道，不进 T7 队列（Must-NOT）。
    /// 队列型工具经 <c>ToolTaskQueue.BeforeStart</c> 调用；编辑组经 <see cref="IToolStartGated"/> 调用。
    /// </summary>
    private async Task<bool> FlushAnnotationsBeforeToolAsync(CancellationToken cancellationToken)
    {
        var service = EnsureAnnotationSaveService();
        var pending = _readerTabs.Values
            .Where(reader => service.HasPending(reader.DocId))
            .Select(reader => reader.DocId)
            .ToList();
        if (pending.Count == 0)
        {
            return true; // 无未保存批注：直接启动工具
        }

        foreach (var path in pending)
        {
            AnnotationSaveOutcome outcome;
            try
            {
                outcome = await service.FlushAsync(path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception flushError)
            {
                // F3-BLK-1：flush 不得静默 fault——异常逃逸会让工具静默不执行（既无提示也无校验）。
                // 归一到失败结果，走下方「批注保存失败」弹窗（可重试/强制继续/取消）。
                Trace.WriteLine($"[MainWindow] 工具执行前批注 flush 异常: {path} ({flushError.Message})");
                outcome = AnnotationSaveOutcome.Empty with
                {
                    RequiresFollowUp = true,
                    Succeeded = false,
                    Error = flushError.Message,
                };
            }

            if (ToolStartFlushLogic.Decide(true, outcome.Succeeded, forceContinue: false)
                == ToolStartGateDecision.Start)
            {
                continue; // flush 成功（已取消防抖计时）→ 放行
            }

            var error = string.IsNullOrWhiteSpace(outcome.Error) ? "请稍后重试。" : outcome.Error!;
            var forceContinue = ConfirmationDialog.Show(
                this,
                "批注保存失败",
                "批注保存失败，请重试。",
                $"{Path.GetFileName(path)}：{error}\n强制继续后，工具执行期间批注可能丢失。",
                "强制继续（批注可能丢失）",
                "取消");

            if (ToolStartFlushLogic.Decide(true, flushSucceeded: false, forceContinue)
                == ToolStartGateDecision.Blocked)
            {
                if (DataContext is ViewModels.MainViewModel main)
                {
                    main.StatusText = "批注保存失败，工具未启动。";
                }

                return false; // 用户取消：阻止启动
            }
        }

        return true;
    }

    /// <summary>
    /// FR-22（Q2 裁决：redact 删除全部批注）：文档存在批注（含未保存）时弹模态警告；
    /// 返回 false 表示用户取消（不进入参数面板）。无批注不弹（AC-22.1/22.2）。
    /// </summary>
    private bool ConfirmRedactAnnotationDeletion(string? documentPath)
    {
        if (documentPath is null)
        {
            return true; // 无目标文档：无批注可警告
        }

        var (fileCount, unsavedCount) = RedactAnnotationCounts(documentPath);
        if (!RedactWarningLogic.ShouldWarn(fileCount, unsavedCount))
        {
            return true; // AC-22.2：无批注直接进参数面板
        }

        return ConfirmationDialog.Show(
            this,
            "永久遮盖提示",
            "永久遮盖将删除文档中的全部批注，此操作不可撤销。",
            "若目标 PDF 含交互表单或目录，还会一并删除全部表单字段（Widget）并清空目录/TOC。",
            "继续 redact",
            "取消");
    }

    /// <summary>已保存（PDF 内嵌，不含 Widget）+ 未保存（叠加层）批注计数；查询失败按 0 计（不阻断工具）。</summary>
    private (int FileCount, int UnsavedCount) RedactAnnotationCounts(string documentPath)
    {
        var reader = _readerTabs.Values.FirstOrDefault(
            r => string.Equals(r.DocId, documentPath, StringComparison.OrdinalIgnoreCase));
        if (reader is null)
        {
            return (0, 0);
        }

        var unsaved = reader.ViewModel.GetAllRecords().Count;
        var file = 0;
        try
        {
            file = reader.ViewModel.CountFileAnnotations();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 统计内嵌批注失败: {documentPath} ({ex.Message})");
        }

        return (file, unsaved);
    }

    /// <summary>标签关闭后的清理：反订阅并释放阅读实例、解除搜索索引、注销保存会话、清理面板/书签句柄。</summary>
    private void OnTabClosed(DocumentTabViewModel tab)
    {
        if (_readerTabs.Remove(tab.TabId, out var reader))
        {
            // T5：关闭标签时批量写阅读状态（页码/缩放/面板/书签）+ 同步最近卡片镜像（FR-24）
            SaveReadingState(reader.ViewModel, reader.DocId);

            reader.ViewModel.PropertyChanged -= reader.Handler;
            reader.ViewModel.AnnotationChanged -= reader.AnnotationHandler;
            reader.ViewModel.Dispose();
            DetachSearchDocument(reader.DocId); // T8：解除索引 + 释放文本层句柄

            // T4：注销保存会话 + 清理面板/书签/句柄（MainWindow.xaml.cs:676 挂点）
            _annotationSaves?.UnregisterDocument(reader.DocId);
            _lastKnownPage.Remove(reader.DocId);
            _conflictBaselines.Remove(reader.DocId);
            ClosePanelRendererFor(reader.DocId);
            _bookmarks.RemoveDocument(reader.DocId);
            UpdateSavePillForActiveTab();
        }
    }

    /// <summary>关闭左栏面板渲染句柄（缩略图/大纲独立实例 + 去重字段；文档关闭/重载共用的清理）。</summary>
    private void ClosePanelRendererFor(string docId)
    {
        if (!string.Equals(_attachedDocId, docId, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _panelRenderer?.CloseDocument(_attachedDocId!);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 关闭面板渲染句柄失败: {docId} ({ex.Message})");
        }

        _attachedDocId = null;
        _thumbPanel = null;
        _outlinePanel = null;
        _bookmarkPanel = null;
        if (ReaderPanelHost is not null) ReaderPanelHost.Content = null;
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

    /// <summary>窗口级 Esc：先关闭最上层工具箱面板；其次搜索面板激活时消费（恢复原面板）；其余交给聚焦控件。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (ToolboxPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            ToolboxPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_panelState.IsSearchActive)
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
            // T9：加密文档用会话驻留密码重开文本层句柄，解锁一次后搜索免密。
            var doc = _searchTextService.OpenDocument(fullPath, _credentials.Get(fullPath));
            _searchDocs[fullPath] = doc;
            _searchIndexService.Attach(fullPath, doc);
        }
        catch (Exception ex)
        {
            // 损坏/未解锁文件：阅读仍继续，搜索不可用（面板探测/搜索会给出提示）
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

    /// <summary>书签 VM（单实例，按 docId 分存；T8 注入 T5 阅读状态存储 → 书签并入每文件 reading-state，Q3）。</summary>
    private readonly BookmarkPanelViewModel _bookmarks;

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

    // ---- T4 批注保存链路（防抖 / 快速通道 / savepill / 唯一真相源 + 视口保留 + 键迁移） ----

    /// <summary>批注保存服务（懒建；随窗口存活，不入全局工具队列）。</summary>
    private AnnotationSaveService? _annotationSaves;

    /// <summary>状态栏 savepill 状态（四态 + 重试）。</summary>
    private readonly SavePillViewModel _savePill = new();

    /// <summary>阅读状态存储（T5：键 = SHA256(路径|size|mtime)，实现 IReadingStateMigrator 供 T4 保存后迁移）。</summary>
    private readonly ReadingStateStore _readingStates = new();

    /// <summary>待恢复的阅读状态（打开时命中；面板挂接时消费书签/面板选择）。</summary>
    private readonly Dictionary<string, ReadingStateRestore> _pendingRestore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按文档记录左栏面板选择（reading-state 的每文件面板字段；M-6 单一真相源）。</summary>
    private readonly Dictionary<string, string> _panelKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ST-12 恢复提示条计时器（2s 自动消失）。</summary>
    private DispatcherTimer? _restoreHintTimer;

    /// <summary>本地内存冲突基线（T4 保存成功后刷新；T11 在此之上做保存前 size/mtime 对比）。</summary>
    private readonly Dictionary<string, (long Size, DateTimeOffset Modified)> _conflictBaselines =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近文件存储（保存成功 / 关闭标签时同步卡片体积/进度/缩放镜像）。</summary>
    private readonly RecentFilesStore _recentFiles = new();

    /// <summary>
    /// 应用设置存储（V3 T5）：打开文档 / 打印时读取，设置保存后仅对「随后新打开的文档」生效
    /// （FR-38 边界；已打开文档不受影响）。</summary>
    private readonly SettingsStore _settingsStore = new();

    /// <summary>
    /// T9 会话密码驻留（PRD 4.4.3）：内存路径→密码，解锁一次标签内免密，进程退出清除。
    /// 绝不落盘 / 写日志 / 进异常消息；渲染 / 文本 / 保存 / 就地重载均经此取密码。
    /// </summary>
    private readonly SessionCredentialStore _credentials = new();

    // ---- T6 会话恢复（session.json：tabs / window / leftPanel） ----

    /// <summary>会话存储（%APPDATA%\AIPDF\session.json；与 reading-state 独立存储互不影响）。</summary>
    private readonly SessionStore _sessionStore = new();

    /// <summary>左栏宽度记忆（默认与 XAML 264 一致；拖动/恢复/隐藏时更新）。</summary>
    private double _leftPanelWidth = DefaultLeftPanelWidth;

    /// <summary>会话失效文件汇总 Toast 计时器（2s 自动隐藏）。</summary>
    private DispatcherTimer? _sessionToastTimer;

    private const double DefaultLeftPanelWidth = 264;
    private const double MinLeftPanelWidth = 220;
    private const double MaxLeftPanelWidth = 600;

    private static double ClampLeftPanelWidth(double width)
        => Math.Clamp(width <= 0 ? DefaultLeftPanelWidth : width, MinLeftPanelWidth, MaxLeftPanelWidth);

    /// <summary>
    /// T8：书签自动标题适配器（FR-04）——从窗口级搜索文本层索引取目标页文本首行。
    /// 未挂接 / 无文本层 / 提取失败返回 null，面板回退页码。
    /// </summary>
    private sealed class SearchTextFirstLineProvider : IBookmarkTitleProvider
    {
        private readonly SearchIndexService _index;

        public SearchTextFirstLineProvider(SearchIndexService index) => _index = index;

        public string? GetFirstLine(string docId, int pageIndex)
        {
            if (!_index.IsAttached(docId)) return null;
            var text = _index.GetPageText(docId, pageIndex);
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length > 0) return line;
            }
            return null;
        }
    }

    /// <summary>
    /// 把文档挂到左栏面板（同 docId 幂等）。文档打不开（损坏/缺文件）时状态栏提示并保持面板空态。
    /// T8：Tab 关闭时按 T5 键写入 reading-state 并清理会话书签（见 OnTabClosed）。
    /// </summary>
    private void AttachPanelsToDocument(string path)
    {
        _panelRenderer ??= new PdfiumRenderService();

        string docId;
        try
        {
            // T9：加密文档用会话驻留密码打开缩略图/大纲渲染句柄，解锁一次后面板免密。
            docId = _panelRenderer.OpenDocument(path, _credentials.Get(path));
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

        _bookmarks.AttachDocument(docId); // T8：内部在会话未跟踪时从 reading-state 回读书签（Q3 并入每文件状态）
        // T5：恢复该文档的面板选择（书签已由 AttachDocument 回读，见上；此处不再重复应用）。
        if (_pendingRestore.TryGetValue(docId, out var pending))
        {
            if (!string.IsNullOrEmpty(pending.PanelKey))
            {
                ApplyPanelSelection(pending.PanelKey);
            }

            _pendingRestore.Remove(docId);
        }
        else if (_panelKeys.TryGetValue(docId, out var key) && !string.IsNullOrEmpty(key))
        {
            // V3 T5：无 reading-state 的新打开文档用设置默认面板（ApplyDefaultOpenSettings 已写入 _panelKeys）；
            // 状态命中时走上一分支，reading-state 仍为 SoT。
            ApplyPanelSelection(key);
        }

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
            if (_attachedDocId is not null)
            {
                _panelKeys[_attachedDocId] = key; // T5：每文件面板选择（FR-24 / M-6）
            }

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

    // ------------------------------------------------------------ T4 批注保存链路接线

    /// <summary>懒建批注保存服务（EngineBridge 快速通道；不入全局工具队列）。</summary>
    private AnnotationSaveService EnsureAnnotationSaveService()
    {
        if (_annotationSaves is not null) return _annotationSaves;

        var bridge = (DataContext as ViewModels.MainViewModel)?.Bridge ?? new Bridge.EngineBridge();
        _annotationSaves = new AnnotationSaveService(
            new EngineAnnotationSaveEngine(bridge),
            new AnnotationSaveOptions
            {
                ReloadAsync = ReloadDocumentHandlesAsync,
                Migrator = _readingStates,
                RefreshConflictBaseline = RefreshConflictBaseline,
                SyncRecentCard = SyncRecentCard,
                Prompt = new WpfAnnotationSavePrompt(this),
                OnSavedAs = OnDocumentSavedAs,
            });
        _annotationSaves.StateChanged += OnAnnotationSaveStateChanged;
        return _annotationSaves;
    }

    /// <summary>savepill 状态更新（服务可能从后台线程抛事件，marshal 回 UI 线程）。</summary>
    private void OnAnnotationSaveStateChanged(object? sender, AnnotationSaveStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var active = ActiveDocumentPath();
            if (active is null || !string.Equals(active, e.Path, StringComparison.OrdinalIgnoreCase))
            {
                _savePill.DocumentPath = active;
                _savePill.State = SavePillState.Saved;
                _savePill.ShouldSuggestSaveAs = false;
                return;
            }

            _savePill.DocumentPath = active;
            _savePill.Apply(e.State, e.ShouldSuggestSaveAs, e.Error);
            if (e.State == SavePillState.Failed && e.ShouldSuggestSaveAs
                && DataContext is ViewModels.MainViewModel main)
            {
                main.StatusText = "批注多次保存失败，建议使用「另存为」保存到新位置。";
            }
        }));
    }

    /// <summary>标签切换/打开/关闭时刷新 savepill 显示文档与状态。</summary>
    private void UpdateSavePillForActiveTab()
    {
        var active = ActiveDocumentPath();
        _savePill.DocumentPath = active;
        if (active is null)
        {
            _savePill.State = SavePillState.Saved;
            _savePill.ShouldSuggestSaveAs = false;
            return;
        }

        _savePill.Apply(EnsureAnnotationSaveService().GetState(active), false, null);
    }

    /// <summary>打开文档时采集冲突基线（T11 保存前对比的基准）。</summary>
    private void CaptureConflictBaseline(string path)
    {
        var stamp = ReadingStateKey.TryCapture(path);
        if (stamp is not { } value) return;
        _conflictBaselines[path] = (value.Size, new DateTimeOffset(value.ModifiedTicks, TimeSpan.Zero));
    }

    /// <summary>保存成功后刷新冲突基线为保存后的 size/mtime（BLK-1，避免下次保存误报外部修改）。</summary>
    private void RefreshConflictBaseline(string path, long size, DateTimeOffset modified)
        => _conflictBaselines[path] = (size, modified);

    /// <summary>
    /// T11 另存为成功：把文档标签/宿主状态切到新路径（原文件不变）。
    /// 服务已把其会话基线重绑到新路径；此处关旧标签再按新路径重开——渲染/搜索/面板/书签
    /// 经既有 <see cref="OnTabClosed"/> + <see cref="OpenDocumentTab"/> 流程统一切换。
    /// 用 <see cref="Dispatcher.BeginInvoke(Action)"/> 延迟到会话门禁释放之后，避免死锁/重入。
    /// </summary>
    private void OnDocumentSavedAs(string oldPath, string newPath)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _conflictBaselines.Remove(oldPath);
                CaptureConflictBaseline(newPath);

                var tab = TabHost.Tabs.FirstOrDefault(t => t.Kind == TabKind.Document
                    && string.Equals(t.FilePath, oldPath, StringComparison.OrdinalIgnoreCase));
                if (tab is not null && TabHost.CloseTab(tab)) // 批注已保存成功：同步关闭无需再次 flush
                {
                    OnTabClosed(tab);
                }

                OpenDocumentTab(newPath);

                if (DataContext is ViewModels.MainViewModel main)
                {
                    main.StatusText = $"已另存为 {Path.GetFileName(newPath)}";
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MainWindow] 另存为后切换文档失败: {oldPath} → {newPath} ({ex.Message})");
            }
        }));
    }

    /// <summary>保存成功后同步最近卡片进度元信息（BLK-1(c)）。</summary>
    private void SyncRecentCard(string path, long bytes, int lastPage, double lastZoom)
    {
        try
        {
            var pageCount = 0;
            var reader = _readerTabs.Values.FirstOrDefault(
                r => string.Equals(r.DocId, path, StringComparison.OrdinalIgnoreCase));
            if (reader?.ViewModel.HasDocument == true) pageCount = reader.ViewModel.PageCount;

            _recentFiles.UpdateProgress(path, pageCount, bytes, lastPage, lastZoom);
            _startPage?.ReloadRecentFiles();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 同步最近卡片失败: {path} ({ex.Message})");
        }
    }

    // ------------------------------------------------------------ T5 阅读状态持久化（恢复 / 保存 / GC / 迁移）

    /// <summary>启动时孤儿 GC（FR-27）：后台执行、静默；失败只记录诊断。</summary>
    private void RunReadingStateGarbageCollect()
    {
        Task.Run(() =>
        {
            try
            {
                _readingStates.GarbageCollect();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MainWindow] 阅读状态 GC 失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 打开文件时恢复（M-6/BA-6）：reading-state 命中（size/mtime 键匹配）则为页码/缩放/面板/书签的单一真相源，
    /// 并显示 ST-12 提示条；未命中回退最近记录镜像（仅当其 Bytes 与当前 size 一致）；否则从第 1 页（AC-24.1/25.1）。
    /// </summary>
    private void RestoreReadingState(string path, ReaderViewModel vm, AppSettings settings)
    {
        if (!vm.HasDocument) return;
        try
        {
            var entry = _recentFiles.Load().Items.FirstOrDefault(
                i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            var restore = _readingStates.ResolveRestore(path, entry);
            if (restore is not null)
            {
                ApplyReadingState(path, vm, restore);
                _pendingRestore[path] = restore; // 面板挂接时消费书签/面板选择
                return;
            }

            // V3 T5：reading-state 未命中 → 用设置默认作为新打开文档的初始值（有状态时上面已 return，状态为 SoT）。
            ApplyDefaultOpenSettings(path, vm, settings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 恢复阅读状态失败: {path} ({ex.Message})");
        }
    }

    /// <summary>
    /// 应用设置默认的初始缩放/面板（FR-38 项 1/2；仅无 reading-state 的新打开文档走此路）。
    /// 缩放：数值档直接套用；适配档由 <see cref="ReaderViewModel.QueueInitialFit"/> 排队到视口测量后套用。
    /// 面板：写入 <c>_panelKeys</c>，由 <see cref="AttachPanelsToDocument"/> 挂接时应用
    /// （文件标签的 reading-state 面板仍优先——此路只在状态未命中时走）。
    /// </summary>
    private void ApplyDefaultOpenSettings(string path, ReaderViewModel vm, AppSettings settings)
    {
        var zoom = SettingsInjection.ResolveInitialZoom(hasReadingState: false, 0, settings.DefaultZoom);
        switch (zoom.Mode)
        {
            case InitialZoomMode.FitWidth:
                vm.QueueInitialFit(fitWidth: true, fitPage: false);
                break;
            case InitialZoomMode.FitPage:
                vm.QueueInitialFit(fitWidth: false, fitPage: true);
                break;
            default:
                if (Math.Abs(zoom.Percent - vm.ZoomPercent) > PageLayoutCalculator.Epsilon)
                {
                    vm.ZoomPercent = zoom.Percent;
                }
                break;
        }

        _panelKeys[path] = SettingsInjection.ResolveDefaultPanelKey(settings.DefaultPanelKey);
    }

    private void ApplyReadingState(string path, ReaderViewModel vm, ReadingStateRestore restore)
    {
        // 先缩放（重建布局），再定位页码——避免缩放锚定覆盖目标页。
        if (restore.LastZoom > 0) vm.ZoomPercent = restore.LastZoom;
        if (restore.LastPage >= 1 && restore.LastPage <= vm.PageCount) vm.GoToPage(restore.LastPage);

        _panelKeys[path] = restore.PanelKey;

        // 仅 reading-state 命中（键校验通过）才显示 ST-12；最近记录兜底不提示（PRD 阅读状态恢复规则）。
        if (restore.FromReadingState) ShowRestoreHint(restore.LastPage, restore.LastAccessUtc);
    }

    /// <summary>显示 ST-12 一次性提示条（阅读区顶部居中，2s 自动消失，不打断阅读）。</summary>
    private void ShowRestoreHint(int page, DateTime lastAccessUtc)
    {
        RestoreHintText.Text = $"↩ 已恢复到第 {page} 页 · 上次阅读于 {FormatRelativeTime(lastAccessUtc)}";
        RestoreHint.Visibility = Visibility.Visible;

        _restoreHintTimer?.Stop();
        _restoreHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _restoreHintTimer.Tick += (_, _) =>
        {
            _restoreHintTimer?.Stop();
            RestoreHint.Visibility = Visibility.Collapsed;
        };
        _restoreHintTimer.Start();
    }

    private static string FormatRelativeTime(DateTime lastAccessUtc)
    {
        if (lastAccessUtc == default) return "最近";
        var local = lastAccessUtc.Kind == DateTimeKind.Utc ? lastAccessUtc.ToLocalTime() : lastAccessUtc;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        return local.ToString("yyyy/M/d HH:mm");
    }

    /// <summary>按当前 size/mtime 计算键并写入阅读状态（仅标签关闭 / 应用退出时批量写，FR-24）。</summary>
    private void SaveReadingState(ReaderViewModel vm, string path)
    {
        if (!vm.HasDocument) return; // 打开失败（加载错误）：不写空状态覆盖既有有效状态
        try
        {
            var stamp = ReadingStateKey.TryCapture(path);
            if (stamp is null) return;

            var page = vm.CurrentPageNumber > 0 ? vm.CurrentPageNumber : 1;
            var zoom = vm.ZoomPercent > 0 ? vm.ZoomPercent : 100;

            // 读改写：保留已落盘书签（未挂接的左栏标签在此不丢书签）；
            // 紧接着由面板 PersistAsync 以会话书签覆盖（含删除），书签仍是同一 reading-state 文件。
            var state = _readingStates.Load(stamp.Value.Key) ?? new ReadingState { Path = path };
            state.Path = path;
            state.LastPage = page;
            state.LastZoom = zoom;
            state.PanelKey = _panelKeys.GetValueOrDefault(path, ReaderPanelState.Thumbs);
            state.LastAccessUtc = DateTime.UtcNow;
            _readingStates.Save(stamp.Value.Key, state);

            // T8/Q3：书签写入同一 reading-state 文件（面板读改写，不另设 bookmarks.json）。
            _ = _bookmarks.PersistAsync(path);

            // 最近记录镜像（兜底投影；与 reading-state 不矛盾——同一时刻同源写入）
            _recentFiles.UpdateProgress(path, vm.PageCount, stamp.Value.Size, page, zoom);

            _pendingRestore.Remove(path);
            _panelKeys.Remove(path);
            _startPage?.ReloadRecentFiles();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 保存阅读状态失败: {path} ({ex.Message})");
        }
    }

    /// <summary>应用退出：批量保存全部打开标签的阅读状态（FR-24）+ 会话（FR-28；移动/缩放期间不写）。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        foreach (var reader in _readerTabs.Values.ToList())
        {
            SaveReadingState(reader.ViewModel, reader.DocId);
        }

        SaveSession();

        // T9（PRD 4.4.3）：进程退出即清除驻留密码；会话/状态文件从不含密码，此处只清内存。
        _credentials.ClearAll();
    }

    // ------------------------------------------------------------ T6 会话恢复（启动恢复 / 退出保存）

    /// <summary>
    /// 启动恢复（FR-29）：读取 session → 窗口几何 → 左栏状态 → 标签集合异步加载。
    /// session 缺失/损坏 → 静默返回，保持 XAML 默认居中窗口与空标签（AC-28.2）。
    /// </summary>
    private async void RestoreSession()
    {
        SessionState? state;
        try
        {
            state = _sessionStore.Load();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 读取会话失败: {ex.Message}");
            return;
        }

        if (state is null) return; // 默认状态（AC-28.2）

        try
        {
            await SessionRestoreCoordinator.RestoreAsync(
                state,
                this,
                File.Exists,
                // 每个标签打开前让出 UI 线程：窗口先显示、标签逐个加载，不阻塞启动。
                () => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 会话恢复失败: {ex.Message}");
        }
    }

    /// <summary>① 窗口几何：M-6 钳制到虚拟屏，套用尺寸/位置/最大化态。尺寸非法则不动（保持默认居中）。</summary>
    void ISessionRestoreTarget.ApplyWindow(SessionWindow window)
    {
        if (window is null || window.Width <= 0 || window.Height <= 0) return;

        var minWidth = double.IsNaN(MinWidth) ? 0 : MinWidth;
        var minHeight = double.IsNaN(MinHeight) ? 0 : MinHeight;
        var (x, y, width, height) = SessionWindowMath.ClampToVirtualScreen(
            window.X, window.Y, window.Width, window.Height,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight,
            minWidth,
            minHeight);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x;
        Top = y;
        Width = width;
        Height = height;
        if (window.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// ② 左栏状态：记住宽度（显示时经 <see cref="UpdateTabAreaState"/> 套用）并套用全局激活面板。
    /// 全局面板仅作初始态——文件标签打开时，其 reading-state 面板选择会覆盖（BA-6 优先级）。
    /// </summary>
    void ISessionRestoreTarget.ApplyLeftPanel(SessionLeftPanel leftPanel)
    {
        if (leftPanel is null) return;

        if (leftPanel.Width > 0) _leftPanelWidth = ClampLeftPanelWidth(leftPanel.Width);
        if (TabHost.HasTabs) LeftPanelColumn.Width = new GridLength(_leftPanelWidth);

        ApplyPanelSelection(SessionStore.NormalizePanelKey(leftPanel.ActiveTab));
    }

    /// <summary>③ 打开有效文件标签（异步分批）；active 为最后激活标签，打开后显式选中。</summary>
    void ISessionRestoreTarget.OpenSessionDocument(string path, bool active)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            OpenDocumentTab(path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 恢复标签失败: {path} ({ex.Message})");
            return;
        }

        if (!active) return;

        var fullPath = Path.GetFullPath(path);
        var tab = TabHost.Tabs.FirstOrDefault(t => t.Kind == TabKind.Document
            && string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (tab is not null) TabHost.SelectedTab = tab;
    }

    /// <summary>失效文件汇总 Toast（AC-29.1：「N 个文件已失效，已跳过」，2s 自动隐藏）。</summary>
    void ISessionRestoreTarget.ReportSkippedFiles(int skippedCount)
    {
        if (skippedCount <= 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            SessionToastText.Text = $"{skippedCount} 个文件已失效，已跳过";
            SessionToast.Visibility = Visibility.Visible;
            _sessionToastTimer?.Stop();
            _sessionToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sessionToastTimer.Tick += (_, _) =>
            {
                _sessionToastTimer?.Stop();
                SessionToast.Visibility = Visibility.Collapsed;
            };
            _sessionToastTimer.Start();
        }));
    }

    /// <summary>
    /// 退出保存会话（FR-28）：文档标签集合（路径 + 激活态）、窗口几何（最大化时存还原边界）、
    /// 左栏宽度与全局激活面板。与 reading-state 独立写入，互不影响。
    /// </summary>
    private void SaveSession()
    {
        try
        {
            var state = new SessionState();
            foreach (var tab in TabHost.Tabs)
            {
                if (tab is { Kind: TabKind.Document, FilePath: { } path })
                {
                    state.Tabs.Add(new SessionTab
                    {
                        Path = path,
                        Active = ReferenceEquals(tab, TabHost.SelectedTab),
                    });
                }
            }

            var maximized = WindowState == WindowState.Maximized;
            var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            state.Window = new SessionWindow
            {
                Width = bounds.Width > 0 ? bounds.Width : Width,
                Height = bounds.Height > 0 ? bounds.Height : Height,
                X = bounds.X,
                Y = bounds.Y,
                Maximized = maximized,
            };

            var panelWidth = LeftPanelColumn.Width.Value > 0 ? LeftPanelColumn.Width.Value : _leftPanelWidth;
            state.LeftPanel = new SessionLeftPanel
            {
                Width = ClampLeftPanelWidth(panelWidth),
                ActiveTab = _panelState.ActiveKey,
            };

            _sessionStore.Save(state);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] 保存会话失败: {ex.Message}");
        }
    }

    /// <summary>恢复/切换面板选择（勾选对应 RadioButton → Checked → ShowReaderPanel；幂等）。</summary>
    private void ApplyPanelSelection(string key)
    {
        _panelState.Activate(key);
        var toggle = key switch
        {
            ReaderPanelState.Thumbs => PanelToggleThumbs,
            ReaderPanelState.Outline => PanelToggleOutline,
            ReaderPanelState.Bookmarks => PanelToggleBookmarks,
            ReaderPanelState.Search => PanelToggleSearch,
            _ => null,
        };

        if (toggle is not null && toggle.IsChecked != true)
        {
            toggle.IsChecked = true; // OnReaderPanelChecked → ShowReaderPanel
        }
        else
        {
            ShowReaderPanel(key);
        }
    }

    // ------------------------------------------------------------ 保存成功后 close-first 重载

    /// <summary>
    /// close-first 重载入口（保存成功回调）。marshal 回 UI 线程执行，覆盖全部句柄域。
    /// </summary>
    private Task ReloadDocumentHandlesAsync(string path, string? password, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        return Dispatcher.InvokeAsync(
            () => ReloadDocumentHandles(fullPath, password),
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    /// <summary>
    /// 覆盖全部句柄域的 close-first 重载（M-3）：
    ///  ① 每个标签的 ReaderViewModel 自建 PdfiumRenderService（close→open，保留视口，清 LRU）；
    ///  ② MainWindow._panelRenderer（缩略图/大纲独立实例 + _attachedDocId 去重）→ 关旧句柄重建面板；
    ///  ③ 搜索文本服务 PdfiumTextService + _searchDocs → Detach 释放旧句柄、重新 Attach；
    ///  ④ SearchIndexService + 搜索面板 VM（随 ③ 重建）。
    /// </summary>
    private void ReloadDocumentHandles(string fullPath, string? password)
    {
        // ① 阅读视图渲染句柄（ReaderViewModel.ReloadDocument 内部 close-first + drain + 视口保留）
        foreach (var reader in _readerTabs.Values)
        {
            if (string.Equals(reader.DocId, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                reader.ViewModel.ReloadDocument(password);
            }
        }

        // ③④ 搜索文本句柄 + 索引 + 面板 VM（Detach → 重开文本句柄 → 重新 Attach）
        DetachSearchDocument(fullPath);
        AttachSearchDocument(fullPath);
        ResetSearchPanelViewModel();

        // ② 左栏面板渲染句柄（独立实例；关闭旧句柄后 AttachPanelsToDocument 重开并重建面板）
        if (string.Equals(_attachedDocId, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            ClosePanelRendererFor(fullPath);
            AttachPanelsToDocument(fullPath);
        }

        UpdateSavePillForActiveTab();
    }

    /// <summary>丢弃搜索面板 VM（其索引状态属于旧句柄；重建后重新探测文本层）。</summary>
    private void ResetSearchPanelViewModel()
    {
        if (_searchPanelVm is null) return;
        _searchPanelVm.HitActivated -= OnSearchHitActivated;
        _searchPanelVm.OcrRequested -= OnSearchOcrRequested;
        _searchPanelVm.CloseRequested -= OnSearchCloseRequested;
        _searchPanelVm.Dispose();
        _searchPanelVm = null;
    }
}
