// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 阅读视图 code-behind：滚动/视口上报、滚动请求执行、Ctrl+滚轮档位转发、页码输入焦点与红边抖动动画。
// T2 增设：页面拖选（文本层即时判定 H-5）、选中浮菜单（复制/高亮/笔记/用工具处理▾）、
//           悬停编辑/删除/改色（FR-13/H-9）、笔记输入弹层、Toast 反馈。
// 纯判定/几何逻辑全部在 SelectionInputController / FloatingMenuLayout / SelectionMenuState /
// AnnotationHitTester（可 xunit 直测）；本文件只做 WPF 事件接线与弹层生命周期。
// V3 T9（C1）：鼠标/选择/命中 handler 从「页元素」迁移到「对页组元素」；组内取点(组坐标)经
//              ReaderViewModel.TryMapGroupPoint / HitTestAnnotationAt 的 page↔group 映射换算到页局部。
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 虚拟化连续阅读视图。可独立实例化预览：
/// <c>new ReaderView()</c> 使用自带空模型；宿主（T5 标签）用 <c>new ReaderView(viewModel)</c> 注入。
/// </summary>
public partial class ReaderView : UserControl
{
    private readonly ReaderViewModel _viewModel;

    /// <summary>Ctrl+滚轮高分辨率增量累积（一个物理档位 = 120 delta）。</summary>
    private double _wheelAccum;

    // ---- T2 选区 / 菜单状态 ----

    private readonly SelectionInputController _selection = new();
    private readonly SelectionMenuState _menu = new();

    /// <summary>拖选起点所在对页组容器（M8：组坐标 → 页局部坐标换算的锚点）。</summary>
    private FrameworkElement? _activeGroupElement;

    /// <summary>拖选起点所在页（页局部坐标与选区绘制的锚点）。</summary>
    private ReaderPageItemViewModel? _activeAnchorPage;

    private int _selectionPageIndex = -1;
    private int _selectionAnchorChar = -1;
    private int _selectionEndChar = -1;
    private IReadOnlyList<PdfRect> _selectionPtRects = [];

    /// <summary>v1.1 T14：选区文本快照（点击浮层「译」/「复制」时使用；关闭选区即清空）。</summary>
    private string _selectedText = "";

    /// <summary>当前弹层定位锚（页容器 + 页内 DIP 矩形）。</summary>
    private FrameworkElement? _anchorElement;
    private (double L, double T, double W, double H) _anchorPageRectDip;

    // ---- 悬停 ----
    private int _hoverPageIndex = -1;
    private string? _hoverAnnotationId;
    private readonly DispatcherTimer _hoverHideTimer;

    // ---- 笔记输入 ----
    private NoteContext? _noteContext;

    // ---- Toast ----
    private readonly DispatcherTimer _toastTimer;

    // ---- V3 T11（C3）手写/形状绘制 ----
    /// <summary>绘制模式开关（关闭时完全保持 V2 文字选中/浮菜单行为）。</summary>
    private bool _drawMode;

    /// <summary>当前绘制工具：ink/square/circle/line/arrow（与工具栏 RadioButton.Tag 对应）。</summary>
    private string _drawTool = "ink";

    /// <summary>当前描边色（#RRGGBB）。</summary>
    private string _drawColor = "#000000";

    /// <summary>当前线宽（pt）。</summary>
    private double _drawWidth = 2;

    // ---- V4 T4 图章 ----
    /// <summary>当前预置图章 token（与 <see cref="StampPresets.All"/> 对应）。</summary>
    private string _stampToken = "approved";

    /// <summary>图片图章字节；非 null 时优先于预置 token（拖拽落位一次后清空）。</summary>
    private byte[]? _stampImage;

    private bool _drawing;
    private int _drawPageIndex = -1;
    private ReaderPageItemViewModel? _drawPage;
    private PdfPoint _drawStart;
    private double _drawLastX;
    private double _drawLastY;
    private readonly List<PdfPoint> _inkPoints = [];

    /// <summary>程序化开关弹层时抑制 Closed 回调（避免把自身关闭误判为用户点击外部）。</summary>
    private bool _suppressClosed;

    public ReaderView() : this(new ReaderViewModel())
    {
    }

    /// <summary>宿主注入构造：DataContext 由本视图接管（预览与 T5 宿主统一走这里）。</summary>
    public ReaderView(ReaderViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.ScrollRequested += OnViewModelScrollRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => ScrollHost.Focus();

        _hoverHideTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(220) };
        _hoverHideTimer.Tick += OnHoverHideTick;

        _toastTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(2) };
        _toastTimer.Tick += OnToastTick;
    }

    /// <summary>视图持有的阅读模型（供宿主联动：页码、缩放、跳页等）。</summary>
    public ReaderViewModel ViewModel => _viewModel;

    /// <summary>工具可用性提供者（宿主注入；如无 LibreOffice 的转 Word 置灰 + tooltip）。</summary>
    public Func<string, ToolAvailability>? ToolAvailabilityProvider { get; set; }

    /// <summary>「用工具处理」发起：宿主经既有 ToolChannels/ShowToolPage 打开并做置顶/页范围预填。</summary>
    public event EventHandler<ToolLaunchRequest>? ToolLaunchRequested;

    /// <summary>
    /// v1.1 T14：用户点击浮出「译」按钮（仅点击才触发，绝不选中即译）。
    /// 宿主负责确保右栏可见、切到「划词」Tab 并执行翻译。
    /// </summary>
    public event EventHandler<SelectionTranslationEventArgs>? TranslateSelectionRequested;

    /// <summary>
    /// v1.1 T14（F1b）：在无文本层页拖选 → 划词不可用。宿主让右栏「划词」Tab 显示替代路径
    /// （用整篇翻译 / 仅本页 OCR），不做「框选没反应」。
    /// </summary>
    public event EventHandler? SelectionNoTextLayerRequested;

    /// <summary>
    /// V4 Option 3：锁定标签「解锁」请求。宿主负责弹出既有【ST-04】密码框、把密码驻留
    /// <c>SessionCredentialStore</c> 并调用 <see cref="ReaderViewModel.Unlock"/>；取消则保持锁定。
    /// </summary>
    public event EventHandler? UnlockRequested;

    /// <summary>锁定遮罩「解锁」按钮：仅发事件，密码流程由宿主负责（视图不接触密码）。</summary>
    private void OnUnlockClick(object sender, RoutedEventArgs e) => UnlockRequested?.Invoke(this, EventArgs.Empty);

    // A+C 方案 A（底栏合一）：原状态栏「锁定」按钮已移入主窗口状态栏，
    // 故 RelockRequested 事件与 OnRelockClick 随之移除；宿主直接调用 RelockDocument。

    // ------------------------------------------------------------ 滚动 ↔ 模型

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 浮菜单不阻塞滚动：滚动即关闭所有弹层与选区（Q12/WPF 陷阱）；绘制中滚动则取消本次绘制。
        if (_drawing) CancelDrawing();
        if (_menu.IsOpen || _hoverAnnotationId is not null)
        {
            CloseAllAndClear();
        }

        if (ScrollHost.ViewportHeight <= 0) return; // 尚未完成测量
        _viewModel.UpdateViewport(ScrollHost.VerticalOffset, ScrollHost.ViewportHeight, ScrollHost.ViewportWidth);
    }

    private void OnViewModelScrollRequested(object? sender, ReaderScrollRequestEventArgs e)
    {
        // 缩放改写各页尺寸后需等一拍布局更新，否则立即滚动会被旧 extent 截断
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => ScrollHost.ScrollToVerticalOffset(e.Offset)));
    }

    // ------------------------------------------------------------ Ctrl+滚轮缩放（FR-06）

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        e.Handled = true;
        _wheelAccum += e.Delta;
        var steps = (int)(_wheelAccum / 120.0);
        if (steps == 0) return;
        _wheelAccum -= steps * 120.0;
        _viewModel.ZoomByWheelSteps(steps);
    }

    // ------------------------------------------------------------ T2 拖选

    private void OnGroupMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ReaderSpreadGroupViewModel group) return;

        // 新交互：先收起旧弹层与旧选区
        HideMenus();
        ClearSelectionVisuals();

        // M8：组内取点 → page↔group 映射（组内页偏移 + 页局部坐标）；点落在组间隙/页外不拉选区
        var pos = e.GetPosition(element);
        if (!_viewModel.TryMapGroupPoint(group.Index, pos.X, pos.Y, out var pageIndex, out var localX, out var localY))
        {
            return;
        }

        // V3 T11（C3）：绘制模式优先——不进入文字选区，直接开始拖拽绘制。
        if (_drawMode)
        {
            if (!_viewModel.CanAnnotate(pageIndex)) return;
            BeginDrawing(element, pageIndex, localX, localY);
            e.Handled = true;
            return;
        }

        _activeGroupElement = element;
        _activeAnchorPage = _viewModel.Pages[pageIndex];
        var hasText = _viewModel.PageHasTextLayer(pageIndex);
        var canAnnotate = _viewModel.CanAnnotate(pageIndex);

        if (!_selection.Begin(pageIndex, localX, localY, _viewModel.HasDocument, canAnnotate, hasText))
        {
            // 无文档 / 旋转页禁选（M-11）：不拉选区
            _activeGroupElement = null;
            _activeAnchorPage = null;
            return;
        }

        element.CaptureMouse();
        e.Handled = true;
    }

    private void OnGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ReaderSpreadGroupViewModel group) return;

        var pos = e.GetPosition(element);
        var mapped = _viewModel.TryMapGroupPoint(group.Index, pos.X, pos.Y, out var pageIndex, out var localX, out var localY);

        // V3 T11（C3）：绘制拖拽优先（mouse 已捕获，不触发选区/悬停）。
        if (_drawing)
        {
            ContinueDrawing(mapped ? pageIndex : -1, localX, localY);
            return;
        }

        if (_selection.IsDragging)
        {
            // M8：组坐标映射到页局部后更新拖选；指针进入组间隙/页外标记跨页（释放不弹菜单）
            if (mapped) _selection.Update(pageIndex, localX, localY);
            else _selection.MarkCrossPage();
            UpdateDragVisual(mapped ? pageIndex : -1);
            return;
        }

        // 悬停批注检测（选区拖拽期间不触发；选区/笔记/子菜单弹层打开时不抢焦点）
        if (_menu.Mode is MenuMode.Selection or MenuMode.ToolSubmenu or MenuMode.NoteEditor or MenuMode.ColorPalette)
        {
            return;
        }

        if (!mapped)
        {
            if (_hoverAnnotationId is not null) ScheduleHideHover();
            return;
        }

        // M8：批注命中经组→页映射（组内页偏移 + 页局部坐标）后走 AnnotationHitTester
        var hitId = _viewModel.HitTestAnnotationAt(group.Index, pos.X, pos.Y, out var hitPage);
        if (hitId is null)
        {
            if (_hoverAnnotationId is not null) ScheduleHideHover();
            return;
        }

        if (_hoverAnnotationId == hitId && _hoverPageIndex == hitPage) return;
        ShowHover(element, hitPage, hitId);
    }

    private void OnGroupMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // V3 T11（C3）：结束绘制并落批注。
        if (_drawing)
        {
            if (sender is FrameworkElement drawElement) drawElement.ReleaseMouseCapture();
            CompleteDrawing();
            return;
        }

        if (!_selection.IsDragging) return;
        if (sender is FrameworkElement element) element.ReleaseMouseCapture();
        if (_activeAnchorPage is null) { _selection.Cancel(); return; }

        var result = _selection.Complete();

        if (!result.HasSelection)
        {
            ClearSelectionVisuals();
            return;
        }

        if (result.Gate == SelectionGate.DisabledNoTextLayer)
        {
            // ST-23：无文本层页保留拖选矩形，但不弹菜单；
            // v1.1 T14（F1b）：同时通知宿主让「划词」Tab 显示替代路径，不做「框选没反应」。
            SelectionNoTextLayerRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (result.CanShowMenu && ComputeSelectionGeometry(result))
        {
            _anchorElement = _activeGroupElement;
            _anchorPageRectDip = ComputeSelectionDipUnion(_selectionPtRects);
            _selectedText = _viewModel.GetSelectionText(_selectionPageIndex, _selectionAnchorChar, _selectionEndChar);
            // v1.1 T14：仅 ≥2 字可选文字时「译」可用（选到单个字/标点不提供划词）。
            TranslateSelectionButton.IsEnabled = _selectedText.Trim().Length >= 2;
            _menu.Open();
            // StaysOpen=False 的弹层必须等本次鼠标抬起事件结束后再开，
            // 否则同一次抬起会被当作「点击外部」立即关掉（WPF 陷阱）。
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_menu.Mode == MenuMode.Selection) SyncPopups();
            }));
        }
        else
        {
            ClearSelectionVisuals();
        }
    }

    private void OnGroupMouseLeave(object sender, MouseEventArgs e)
    {
        if (_drawing) return; // 绘制中鼠标已捕获，越过组边界不算离开
        if (_selection.IsDragging) return;
        if (_hoverAnnotationId is not null) ScheduleHideHover();
    }

    /// <summary>拖拽中显示矩形选框（跨页则清除锚点页选框）。坐标均为锚点页页局部 DIP。</summary>
    private void UpdateDragVisual(int pageIndex)
    {
        var anchor = _activeAnchorPage;
        if (anchor is null) return;

        if (_selection.IsCrossPage || pageIndex != anchor.Index)
        {
            // 跨页选区不弹菜单：清除锚点页选框
            anchor.SetSelectionRects([]);
            return;
        }

        var left = Math.Min(_selection.AnchorX, _selection.CurrentX);
        var top = Math.Min(_selection.AnchorY, _selection.CurrentY);
        var width = Math.Abs(_selection.CurrentX - _selection.AnchorX);
        var height = Math.Abs(_selection.CurrentY - _selection.AnchorY);
        anchor.SetSelectionRects([new SearchHighlight(left, top, width, height)]);
    }

    /// <summary>释放后按起止字符下标算文本选区矩形；失败返回 false（不弹菜单）。坐标已是页局部（经组映射）。</summary>
    private bool ComputeSelectionGeometry(SelectionResult result)
    {
        if (!_viewModel.TryHitCharAtPoint(result.PageIndex, result.StartX, result.StartY, out var a)) return false;
        if (!_viewModel.TryHitCharAtPoint(result.PageIndex, result.EndX, result.EndY, out var b)) return false;

        var ptRects = _viewModel.GetSelectionRects(result.PageIndex, a, b);
        if (ptRects.Count == 0) return false;

        _selectionPageIndex = result.PageIndex;
        _selectionAnchorChar = a;
        _selectionEndChar = b;
        _selectionPtRects = ptRects;

        var page = _viewModel.Pages[result.PageIndex];
        var dip = new List<SearchHighlight>(ptRects.Count);
        foreach (var r in ptRects) dip.Add(PageLayoutCalculator.ToPageDip(r, page.WidthPt, page.DisplayWidth));
        page.SetSelectionRects(dip);
        return true;
    }

    // ------------------------------------------------------------ T2 选中浮菜单动作

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = _selectionPageIndex >= 0
            ? _viewModel.GetSelectionText(_selectionPageIndex, _selectionAnchorChar, _selectionEndChar)
            : string.Empty;

        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[ReaderView] 复制失败: {ex.Message}"); }
        }

        CloseAllAndClear();
        ShowToast(ReaderStrings.Copied);
    }

    /// <summary>
    /// v1.1 T14：点击浮层「译」→ 把选中文本交给宿主翻译（**仅点击才发请求，不选中即译**）；
    /// 随后收起浮层与选区。少于 2 字不触发（按钮此时已禁用，此处双保险）。
    /// </summary>
    private void OnTranslateSelectionClick(object sender, RoutedEventArgs e)
    {
        var text = _selectedText;
        if (string.IsNullOrWhiteSpace(text) && _selectionPageIndex >= 0)
        {
            text = _viewModel.GetSelectionText(_selectionPageIndex, _selectionAnchorChar, _selectionEndChar);
        }

        var trimmed = (text ?? "").Trim();
        if (trimmed.Length < 2)
        {
            ShowToast(ReaderStrings.TranslateTooShort);
            return;
        }

        var pageIndex = _selectionPageIndex;
        // T19：在清空选区前捕获包围盒（PDF 点），供「替换标注」写入 sidecar 后按页叠加显示。
        var selectionRect = _selectionPtRects.Count > 0 ? AnnotationGeometry.Union(_selectionPtRects) : default;
        CloseAllAndClear();
        TranslateSelectionRequested?.Invoke(this, new SelectionTranslationEventArgs(trimmed, pageIndex, selectionRect));
    }

    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        if (_selectionPtRects.Count == 0) { CloseAllAndClear(); return; }
        // V4 T3：下发**逐行**选区矩形（压成单个包围盒会把行间空隙也涂满）
        _viewModel.AddHighlight(_selectionPageIndex, _selectionPtRects);
        CloseAllAndClear();
        ShowToast(ReaderStrings.HighlightApplied);
    }

    private void OnUnderlineClick(object sender, RoutedEventArgs e)
        => ApplyTextMarkup(AnnotationKind.Underline, ReaderStrings.UnderlineApplied);

    private void OnStrikeoutClick(object sender, RoutedEventArgs e)
        => ApplyTextMarkup(AnnotationKind.Strikeout, ReaderStrings.StrikeoutApplied);

    /// <summary>
    /// 应用文本标记（V4 T3 浮菜单「下划线」「删除线」）：逐行选区下发。
    /// 仅在真的创建成功时提示——旋转页/越界由 VM 返回 null，避免「没加上却提示成功」。
    /// </summary>
    private void ApplyTextMarkup(AnnotationKind kind, string appliedText)
    {
        if (_selectionPtRects.Count == 0) { CloseAllAndClear(); return; }
        var created = _viewModel.AddTextMarkup(kind, _selectionPageIndex, _selectionPtRects);
        CloseAllAndClear();
        if (created is not null)
        {
            ShowToast(appliedText);
        }
    }

    private void OnNoteClick(object sender, RoutedEventArgs e)
    {
        if (_selectionPtRects.Count == 0) { CloseAllAndClear(); return; }

        // 先落高亮（用户即时看到锚点），确认后替换为 Note（含标记圆点 R4）
        var rect = AnnotationGeometry.Union(_selectionPtRects);
        var highlight = _viewModel.AddHighlight(_selectionPageIndex, rect);
        _noteContext = new NoteContext(_selectionPageIndex, rect, highlight?.Id, null);

        NoteBox.Text = string.Empty;
        _menu.OpenNoteEditor();
        SyncPopups();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => NoteBox.Focus()));
    }

    private void OnNoteConfirmClick(object sender, RoutedEventArgs e)
    {
        var text = NoteBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ShowToast(ReaderStrings.NoteEditorTitle);
            return;
        }

        if (_noteContext is { } ctx)
        {
            if (ctx.EditingId is not null)
            {
                var existing = _viewModel.FindAnnotation(ctx.PageIndex, ctx.EditingId);
                if (existing is not null) _viewModel.UpdateAnnotation(existing.WithNote(text));
            }
            else
            {
                // 高亮 → 笔记（替换锚点高亮，避免双重渲染）
                if (ctx.AnchorHighlightId is not null)
                {
                    _viewModel.RemoveAnnotation(ctx.PageIndex, ctx.AnchorHighlightId);
                }
                _viewModel.AddNote(ctx.PageIndex, ctx.Rect, text);
            }
        }

        _noteContext = null;
        CloseAllAndClear();
        ShowToast(ReaderStrings.NoteApplied);
    }

    private void OnNoteCancelClick(object sender, RoutedEventArgs e)
    {
        // 取消笔记：保留已落下的高亮（「先落高亮」语义）
        _noteContext = null;
        CloseAllAndClear();
    }

    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        RefreshToolAvailability();
        _menu.OpenToolSubmenu();
        SyncPopups();
    }

    private void OnMenuBackClick(object sender, RoutedEventArgs e)
    {
        if (_menu.Back()) SyncPopups();
    }

    private void OnToolItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string toolId }) return;
        var item = ToolSubmenuCatalog.Find(toolId);
        if (item is null) return;

        var availability = ToolAvailabilityProvider?.Invoke(toolId) ?? ToolAvailability.Enabled;
        if (!availability.Available) return;

        var request = new ToolLaunchRequest
        {
            ToolId = toolId,
            PinCurrentDocument = true,
            // H-6：页范围预填仅对确有页范围字段的工具（split/rotate）；compress/pdf_to_word 仅置顶
            PrefillPageRange = item.Value.SupportsPageRange,
            PageStart = _viewModel.CurrentPageNumber,
            PageEnd = _viewModel.CurrentPageNumber,
        };

        CloseAllAndClear();
        ToolLaunchRequested?.Invoke(this, request);
    }

    private void OnMoreToolsClick(object sender, RoutedEventArgs e)
    {
        CloseAllAndClear();
        ToolLaunchRequested?.Invoke(this, ToolLaunchRequest.MoreTools());
    }

    private void RefreshToolAvailability()
    {
        ApplyToolButton(ToolCompressButton, "compress");
        ApplyToolButton(ToolSplitButton, "split");
        ApplyToolButton(ToolRotateButton, "rotate");
        ApplyToolButton(ToolWordButton, "pdf_to_word");
    }

    private void ApplyToolButton(Button button, string toolId)
    {
        var availability = ToolAvailabilityProvider?.Invoke(toolId) ?? ToolAvailability.Enabled;
        button.IsEnabled = availability.Available;
        button.ToolTip = availability.Available
            ? null
            : $"{ReaderStrings.ToolUnavailable}：{availability.DisabledReason}";
    }

    // ------------------------------------------------------------ T2 悬停编辑/删除/改色

    private void ShowHover(FrameworkElement element, int pageIndex, string annotationId)
    {
        CancelHideHover();
        _hoverPageIndex = pageIndex;
        _hoverAnnotationId = annotationId;

        var page = _viewModel.Pages[pageIndex];
        AnnotationOverlay? overlay = null;
        foreach (var o in page.Annotations)
        {
            if (o.Id == annotationId) { overlay = o; break; }
        }
        if (overlay is null) { HideHover(); return; }

        var record = _viewModel.FindAnnotation(pageIndex, annotationId);
        // V4 T9：把选中项交给属性面板（面板据此显示颜色/线宽/不透明度）
        _viewModel.Properties.Select(record);
        HoverEditButton.IsEnabled = record?.Kind == AnnotationKind.Note;

        _anchorElement = element;
        // M8：批注矩形为页局部 DIP；弹层锚定在组容器上，故加上页在组内偏移。
        _anchorPageRectDip = (overlay.Value.X + page.OffsetX, overlay.Value.Y + page.OffsetY,
            overlay.Value.Width, overlay.Value.Height);

        _menu.OpenHover();
        SyncPopups();
    }

    private void OnHoverEditClick(object sender, RoutedEventArgs e)
    {
        if (_hoverAnnotationId is null || _hoverPageIndex < 0) return;
        var record = _viewModel.FindAnnotation(_hoverPageIndex, _hoverAnnotationId);
        if (record is null || record.Kind != AnnotationKind.Note) return;

        _noteContext = new NoteContext(_hoverPageIndex, record.Rect, null, record.Id);
        NoteBox.Text = record.Note ?? string.Empty;
        _hoverAnnotationId = null;

        _menu.OpenNoteEditor();
        SyncPopups();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => NoteBox.Focus()));
    }

    private void OnHoverDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_hoverAnnotationId is null) return;
        _viewModel.RemoveAnnotation(_hoverPageIndex, _hoverAnnotationId);
        HideHover();
        ShowToast(ReaderStrings.AnnotationDeleted);
    }

    private void OnHoverRecolorClick(object sender, RoutedEventArgs e)
    {
        if (_hoverAnnotationId is null) return;
        _menu.OpenColorPalette();
        SyncPopups();
    }

    private void OnColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (_hoverAnnotationId is null || sender is not Button { Tag: string hex }) return;
        var record = _viewModel.FindAnnotation(_hoverPageIndex, _hoverAnnotationId);
        if (record is not null) _viewModel.UpdateAnnotation(record.WithColor(hex));
        HideHover();
    }

    private void HideHover()
    {
        CancelHideHover();
        _hoverAnnotationId = null;
        _hoverPageIndex = -1;
        // V4 T9：清空属性面板的选中（面板随选中出现/消失）
        _viewModel.Properties.Select(null);
        if (_menu.Mode is MenuMode.HoverActions or MenuMode.ColorPalette)
        {
            _menu.Close();
            SyncPopups();
        }
    }

    private void ScheduleHideHover() => _hoverHideTimer.Start();

    private void CancelHideHover() => _hoverHideTimer.Stop();

    private void OnHoverHideTick(object? sender, EventArgs e)
    {
        _hoverHideTimer.Stop();
        HideHover();
    }

    private void OnHoverPopupMouseEnter(object sender, MouseEventArgs e) => CancelHideHover();

    private void OnHoverPopupMouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverAnnotationId is not null) ScheduleHideHover();
    }

    // ------------------------------------------------------------ T2 弹层同步 / 定位

    /// <summary>按当前 <see cref="SelectionMenuState.Mode"/> 开关/切换弹层并重定位。</summary>
    private void SyncPopups()
    {
        _suppressClosed = true;
        try
        {
            SelectionMainPanel.Visibility = _menu.Mode == MenuMode.Selection ? Visibility.Visible : Visibility.Collapsed;
            SelectionToolPanel.Visibility = _menu.Mode == MenuMode.ToolSubmenu ? Visibility.Visible : Visibility.Collapsed;
            SelectionPopup.IsOpen = _menu.Mode is MenuMode.Selection or MenuMode.ToolSubmenu;

            HoverMainPanel.Visibility = _menu.Mode == MenuMode.HoverActions ? Visibility.Visible : Visibility.Collapsed;
            HoverColorPanel.Visibility = _menu.Mode == MenuMode.ColorPalette ? Visibility.Visible : Visibility.Collapsed;
            HoverPopup.IsOpen = _menu.Mode is MenuMode.HoverActions or MenuMode.ColorPalette;

            NotePopup.IsOpen = _menu.Mode == MenuMode.NoteEditor;
        }
        finally
        {
            _suppressClosed = false;
        }

        PositionOpenPopup();
    }

    private void PositionOpenPopup()
    {
        if (_anchorElement is null) return;

        Popup? target = _menu.Mode switch
        {
            MenuMode.Selection or MenuMode.ToolSubmenu => SelectionPopup,
            MenuMode.HoverActions or MenuMode.ColorPalette => HoverPopup,
            MenuMode.NoteEditor => NotePopup,
            _ => null,
        };
        if (target?.Child is not FrameworkElement child) return;

        var viewport = (ScrollHost.ActualWidth, ScrollHost.ActualHeight);
        if (viewport.Item1 <= 0 || viewport.Item2 <= 0) return;

        var anchor = ToViewport(_anchorElement, _anchorPageRectDip);
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var placement = FloatingMenuLayout.Compute(anchor, viewport, (child.DesiredSize.Width, child.DesiredSize.Height));
        target.HorizontalOffset = placement.Left;
        target.VerticalOffset = placement.Top;
    }

    private (double, double, double, double) ToViewport(FrameworkElement element, (double L, double T, double W, double H) rect)
    {
        var transform = element.TransformToAncestor(ScrollHost);
        var topLeft = transform.Transform(new Point(rect.L, rect.T));
        var bottomRight = transform.Transform(new Point(rect.L + rect.W, rect.T + rect.H));
        return (topLeft.X, topLeft.Y, Math.Max(0, bottomRight.X - topLeft.X), Math.Max(0, bottomRight.Y - topLeft.Y));
    }

    private void OnSelectionPopupClosed(object? sender, EventArgs e)
    {
        if (_suppressClosed) return;
        if (_menu.Mode is MenuMode.Selection or MenuMode.ToolSubmenu)
        {
            // 用户点击弹层外部：收起并清除选区
            _menu.Close();
            ClearSelectionVisuals();
        }
    }

    private void OnHoverPopupClosed(object? sender, EventArgs e)
    {
        if (_suppressClosed) return;
        _hoverAnnotationId = null;
        _hoverPageIndex = -1;
    }

    /// <summary>收起全部弹层（不清理选区显示）。</summary>
    private void HideMenus()
    {
        CancelHideHover();
        _hoverAnnotationId = null;
        _hoverPageIndex = -1;
        if (_menu.IsOpen)
        {
            _menu.Close();
            SyncPopups();
        }
    }

    /// <summary>收起全部弹层并清除选区显示/字符状态。</summary>
    private void CloseAllAndClear()
    {
        HideMenus();
        ClearSelectionVisuals();
    }

    private void ClearSelectionVisuals()
    {
        if (_activeAnchorPage is not null)
        {
            _activeAnchorPage.SetSelectionRects([]);
        }
        _activeGroupElement = null;
        _activeAnchorPage = null;
        _selectionPageIndex = -1;
        _selectionAnchorChar = -1;
        _selectionEndChar = -1;
        _selectionPtRects = [];
        _selectedText = "";
        _selection.Cancel();
    }

    /// <summary>选区矩形并集（页局部 DIP）+ 页在组内偏移 → 组局部 DIP（弹层锚定在组容器上）。</summary>
    private (double L, double T, double W, double H) ComputeSelectionDipUnion(IReadOnlyList<PdfRect> rects)
    {
        if (rects.Count == 0 || _selectionPageIndex < 0 || _selectionPageIndex >= _viewModel.Pages.Count) return (0, 0, 0, 0);
        var page = _viewModel.Pages[_selectionPageIndex];
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            var d = PageLayoutCalculator.ToPageDip(r, page.WidthPt, page.DisplayWidth);
            left = Math.Min(left, d.X);
            top = Math.Min(top, d.Y);
            right = Math.Max(right, d.X + d.Width);
            bottom = Math.Max(bottom, d.Y + d.Height);
        }
        return (left + page.OffsetX, top + page.OffsetY, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // ------------------------------------------------------------ Toast

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------ V3 T11（C3）手写/形状绘制

    /// <summary>绘制总开关切换：开启时收起既有浮层/选区，关闭时取消在途绘制。</summary>
    private void OnDrawModeChanged(object sender, RoutedEventArgs e)
    {
        _drawMode = DrawModeToggle.IsChecked == true;
        CancelDrawing();
        if (_drawMode) CloseAllAndClear();
    }

    private void OnDrawToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tool })
        {
            _drawTool = tool;
            CancelDrawing();
        }
    }

    private void OnDrawColorChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string color }) _drawColor = color;
    }

    /// <summary>V4 T4：预置图章下拉切换（SelectedValuePath=Token）。</summary>
    private void OnStampPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StampPresetBox.SelectedItem is StampPreset preset)
        {
            _stampToken = preset.Token;
            _stampImage = null;   // 选了预置图章即放弃图片图章
        }
    }

    /// <summary>
    /// V4 T4：选一张图片作为图片图章。图片字节随批注动作以 base64 下发引擎（不落临时文件）；
    /// 选好后自动切到图章工具，用户直接在页面上拖拽确定位置与大小。
    /// </summary>
    private void OnStampImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = ReaderStrings.StampPickImageToolTip,
            Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            _stampImage = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowToast(ReaderStrings.StampImageFailed);
            return;
        }

        if (DrawModeToggle.IsChecked != true)
        {
            DrawModeToggle.IsChecked = true;   // 触发 OnDrawModeChanged → 进入绘制模式
        }

        StampToolButton.IsChecked = true;      // 触发 OnDrawToolChecked → _drawTool = "stamp"
        ShowToast(ReaderStrings.StampImageActive);
    }

    private void OnDrawWidthChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string raw }
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
        {
            _drawWidth = width;
        }
    }

    private void BeginDrawing(FrameworkElement element, int pageIndex, double localX, double localY)
    {
        _drawing = true;
        _drawPageIndex = pageIndex;
        _drawPage = _viewModel.Pages[pageIndex];
        _drawStart = new PdfPoint(localX, localY);
        _drawLastX = localX;
        _drawLastY = localY;
        _inkPoints.Clear();
        _inkPoints.Add(_drawStart);
        _activeGroupElement = element;
        element.CaptureMouse();
        UpdateDrawPreview(localX, localY);
    }

    private void ContinueDrawing(int pageIndex, double localX, double localY)
    {
        if (!_drawing || _drawPage is null) return;
        if (pageIndex >= 0 && pageIndex != _drawPageIndex) return; // 单页内绘制（跨页忽略）

        if (_drawTool == "ink")
        {
            var dx = localX - _drawLastX;
            var dy = localY - _drawLastY;
            if (dx * dx + dy * dy >= 1.0) // 距上一点 ≥1DIP 才采样，避免海量重复点
            {
                _inkPoints.Add(new PdfPoint(localX, localY));
                _drawLastX = localX;
                _drawLastY = localY;
            }
        }
        else
        {
            _drawLastX = localX;
            _drawLastY = localY;
        }

        UpdateDrawPreview(localX, localY);
    }

    /// <summary>拖拽预览：借 <see cref="AnnotationOverlay.FromRecord"/> 的几何投影，保证预览与落笔一致。</summary>
    private void UpdateDrawPreview(double currentX, double currentY)
    {
        if (_drawPage is null) return;
        var overlay = BuildPreviewOverlay(currentX, currentY);
        _drawPage.DrawPreview = overlay?.Geometry;
        _drawPage.DrawPreviewThickness = overlay?.StrokeThickness ?? 0;
        _drawPage.DrawPreviewX = overlay?.X ?? 0;
        _drawPage.DrawPreviewY = overlay?.Y ?? 0;
    }

    private AnnotationOverlay? BuildPreviewOverlay(double currentX, double currentY)
    {
        var page = _drawPage;
        if (page is null || page.WidthPt <= 0 || page.DisplayWidth <= 0) return null;

        var end = new PdfPoint(currentX, currentY);
        AnnotationRecord? temp = null;
        switch (_drawTool)
        {
            case "ink":
                if (_inkPoints.Count < 2) return null;
                temp = AnnotationRecord.CreateInk(_drawPageIndex, new IReadOnlyList<PdfPoint>[] { _inkPoints }, _drawColor, _drawWidth);
                break;
            case "square":
            case "circle":
                temp = AnnotationRecord.CreateShape(
                    _drawPageIndex,
                    _drawTool == "square" ? ShapeKind.Square : ShapeKind.Circle,
                    AnnotationGeometry.BoundingBox([_drawStart, end]), _drawColor, _drawWidth);
                break;
            case "line":
            case "arrow":
                temp = AnnotationRecord.CreateShape(
                    _drawPageIndex,
                    _drawTool == "line" ? ShapeKind.Line : ShapeKind.Arrow,
                    _drawStart, end, _drawColor, _drawWidth);
                break;
            case "stamp":
                // 预览不解码图片（拖拽过程中每次移动都会重建叠加层），图片图章只画占位框
                temp = AnnotationRecord.CreateStamp(
                    _drawPageIndex,
                    AnnotationGeometry.BoundingBox([_drawStart, end]),
                    _stampImage is null ? _stampToken : null,
                    _stampImage is null ? null : [],
                    _drawColor);
                break;
        }

        return temp is null ? null : AnnotationOverlay.FromRecord(temp, page.WidthPt, page.DisplayWidth);
    }

    /// <summary>释放时落批注：过小/退化拖拽不创建（与文字选区「微小拖拽视为点击」一致）。</summary>
    private void CompleteDrawing()
    {
        var pageIndex = _drawPageIndex;
        var end = new PdfPoint(_drawLastX, _drawLastY);
        AnnotationRecord? record = null;

        try
        {
            switch (_drawTool)
            {
                case "ink":
                    if (_inkPoints.Count >= 2)
                    {
                        record = _viewModel.AddInk(
                            pageIndex, new IReadOnlyList<PdfPoint>[] { _inkPoints.ToArray() }, _drawColor, _drawWidth);
                    }
                    break;

                case "square":
                case "circle":
                {
                    var rect = AnnotationGeometry.BoundingBox([_drawStart, end]);
                    if (rect.Width >= 1 && rect.Height >= 1)
                    {
                        record = _viewModel.AddShape(
                            pageIndex,
                            _drawTool == "square" ? ShapeKind.Square : ShapeKind.Circle,
                            rect, _drawColor, _drawWidth);
                    }
                    break;
                }

                case "line":
                case "arrow":
                {
                    var dx = end.X - _drawStart.X;
                    var dy = end.Y - _drawStart.Y;
                    if (dx * dx + dy * dy >= 4.0)
                    {
                        record = _viewModel.AddShape(
                            pageIndex,
                            _drawTool == "line" ? ShapeKind.Line : ShapeKind.Arrow,
                            _drawStart, end, _drawColor, _drawWidth);
                    }
                    break;
                }

                case "stamp":
                {
                    var rect = AnnotationGeometry.BoundingBox([_drawStart, end]);
                    if (rect.Width >= 8 && rect.Height >= 8)
                    {
                        record = _viewModel.AddStamp(pageIndex, rect, _stampToken, _stampImage, _drawColor);
                        if (record is not null && _stampImage is not null)
                        {
                            _stampImage = null;   // 图片图章落位一次后回到预置图章，避免连点误放
                        }
                    }
                    break;
                }
            }
        }
        finally
        {
            CancelDrawing();
        }

        if (record is not null)
        {
            ShowToast(_drawTool switch
            {
                "ink" => ReaderStrings.InkApplied,
                "stamp" => ReaderStrings.StampApplied,
                _ => ReaderStrings.ShapeApplied,
            });
        }
    }

    // ---- V4 T8 朗读条（宿主注入执行体：本视图只负责交互与状态呈现）----

    /// <summary>朗读开关（播放/暂停/继续）。</summary>
    public event EventHandler? SpeechToggleRequested;

    /// <summary>停止朗读。</summary>
    public event EventHandler? SpeechStopRequested;

    /// <summary>语速变更（-10 ~ 10）。</summary>
    public event EventHandler<int>? SpeechRateChanged;

    private void OnSpeechToggleClick(object sender, RoutedEventArgs e)
        => SpeechToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnSpeechStopClick(object sender, RoutedEventArgs e)
        => SpeechStopRequested?.Invoke(this, EventArgs.Empty);

    private void OnSpeechRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeechRateBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var rate))
        {
            SpeechRateChanged?.Invoke(this, rate);
        }
    }

    // ---- V4 T9 批注属性面板（改动交回 VM → 既有保存链路）----

    private void OnPropertiesColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            _viewModel.Properties.ApplyColor(hex);
        }
    }

    private void OnPropertiesWidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PropertiesWidthBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var width))
        {
            _viewModel.Properties.ApplyLineWidth(width);
        }
    }

    private void OnPropertiesOpacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PropertiesOpacityBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var opacity))
        {
            _viewModel.Properties.ApplyOpacity(opacity);
        }
    }

    private void OnPropertiesDismissClick(object sender, RoutedEventArgs e)
        => _viewModel.Properties.Dismiss();

    private void CancelDrawing()
    {
        _drawing = false;
        _drawPageIndex = -1;
        if (_drawPage is not null)
        {
            _drawPage.DrawPreview = null;
            _drawPage.DrawPreviewThickness = 0;
            _drawPage.DrawPreviewX = 0;
            _drawPage.DrawPreviewY = 0;
        }
        _drawPage = null;
        _inkPoints.Clear();
    }

    // ------------------------------------------------------------ 页码跳转（FR-05）
    // A+C 方案 A（底栏合一）：JumpBox 与 Enter/Esc 处理已移到 MainWindow（状态栏），
    // 本视图不再持有页码输入框；Ctrl+G 由 MainWindow 的窗口级 PreviewKeyDown 处理。

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // V3 T11（C3）：Esc 先结束绘制/退出绘制模式，再处理既有弹层逐层收起。
        if (e.Key == Key.Escape && (_drawing || _drawMode))
        {
            e.Handled = true;
            CancelDrawing();
            if (_drawMode) DrawModeToggle.IsChecked = false; // 触发 OnDrawModeChanged → CancelDrawing
            return;
        }

        // T2：Esc 逐层收起弹层（子菜单→主菜单→关闭）
        if (e.Key == Key.Escape && _menu.IsOpen)
        {
            e.Handled = true;
            if (!_menu.Back()) CloseAllAndClear();
            else SyncPopups();
            return;
        }
    }

    /// <summary>对开切换时收起浮层（组容器会重建，锚点失效）；锁定/解锁同理由。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.SpreadEnabled))
        {
            // V3 T9：单页/双页切换会整体重建组容器，先收起弹层与选区，避免锚点指向已回收元素。
            CancelDrawing();
            CloseAllAndClear();
            return;
        }

        // V4 Option 3：锁定/解锁会整体替换页项（锁定释放、解锁重建），先收起弹层与选区避免悬空锚点。
        if (e.PropertyName == nameof(ReaderViewModel.IsLocked))
        {
            CancelDrawing();
            CloseAllAndClear();
        }
    }

    /// <summary>笔记输入上下文（新建=先落高亮后替换；编辑=按 Id 更新）。</summary>
    private sealed record NoteContext(int PageIndex, PdfRect Rect, string? AnchorHighlightId, string? EditingId);
}
