// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 阅读视图 code-behind：滚动/视口上报、滚动请求执行、Ctrl+滚轮档位转发、页码输入焦点与红边抖动动画。
// T2 增设：页面拖选（文本层即时判定 H-5）、选中浮菜单（复制/高亮/笔记/用工具处理▾）、
//           悬停编辑/删除/改色（FR-13/H-9）、笔记输入弹层、Toast 反馈。
// 纯判定/几何逻辑全部在 SelectionInputController / FloatingMenuLayout / SelectionMenuState /
// AnnotationHitTester（可 xunit 直测）；本文件只做 WPF 事件接线与弹层生命周期。
using System.ComponentModel;
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

    /// <summary>拖选起点所在页容器（坐标换算的锚点）。</summary>
    private FrameworkElement? _activePageElement;

    private int _selectionPageIndex = -1;
    private int _selectionAnchorChar = -1;
    private int _selectionEndChar = -1;
    private IReadOnlyList<PdfRect> _selectionPtRects = [];

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

    // ------------------------------------------------------------ 滚动 ↔ 模型

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 浮菜单不阻塞滚动：滚动即关闭所有弹层与选区（Q12/WPF 陷阱）
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

    private void OnPageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ReaderPageItemViewModel page) return;

        // 新交互：先收起旧弹层与旧选区
        HideMenus();
        ClearSelectionVisuals();

        _activePageElement = element;
        var pos = e.GetPosition(element);
        var hasText = _viewModel.PageHasTextLayer(page.Index);
        var canAnnotate = _viewModel.CanAnnotate(page.Index);

        if (!_selection.Begin(page.Index, pos.X, pos.Y, _viewModel.HasDocument, canAnnotate, hasText))
        {
            // 无文档 / 旋转页禁选（M-11）：不拉选区
            _activePageElement = null;
            return;
        }

        element.CaptureMouse();
        e.Handled = true;
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ReaderPageItemViewModel page) return;

        if (_selection.IsDragging)
        {
            var pos = e.GetPosition(element);
            // 鼠标捕获下所有移动都路由到锚点元素，pageIndex 不变：按指针越出锚点页纵向边界判定跨页
            var anchor = element.DataContext as ReaderPageItemViewModel;
            if (anchor is not null && (pos.Y < -0.5 || pos.Y > anchor.DisplayHeight + 0.5))
            {
                _selection.MarkCrossPage();
            }

            _selection.Update(page.Index, pos.X, pos.Y);
            UpdateDragVisual(page);
            return;
        }

        // 悬停批注检测（选区拖拽期间不触发；选区/笔记/子菜单弹层打开时不抢焦点）
        if (_menu.Mode is MenuMode.Selection or MenuMode.ToolSubmenu or MenuMode.NoteEditor or MenuMode.ColorPalette)
        {
            return;
        }

        var hover = e.GetPosition(element);
        var hitId = AnnotationHitTester.HitTest(page.Annotations, hover.X, hover.Y);
        if (hitId is null)
        {
            if (_hoverAnnotationId is not null) ScheduleHideHover();
            return;
        }

        if (_hoverAnnotationId == hitId && _hoverPageIndex == page.Index) return;
        ShowHover(element, page, hitId);
    }

    private void OnPageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selection.IsDragging) return;
        if (sender is FrameworkElement element) element.ReleaseMouseCapture();
        if (_activePageElement is null) { _selection.Cancel(); return; }

        var result = _selection.Complete();

        if (!result.HasSelection)
        {
            ClearSelectionVisuals();
            return;
        }

        if (result.Gate == SelectionGate.DisabledNoTextLayer)
        {
            // ST-23：无文本层页保留拖选矩形，但不弹菜单
            return;
        }

        if (result.CanShowMenu && ComputeSelectionGeometry(result, _activePageElement))
        {
            _anchorElement = _activePageElement;
            _anchorPageRectDip = ComputeSelectionDipUnion(_selectionPtRects);
            _menu.Open();
            SyncPopups();
            // StaysOpen=False 的弹层需等本次鼠标事件结束后再开，否则被同一次抬起立即关掉
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_menu.Mode == MenuMode.Selection) PositionOpenPopup();
            }));
        }
        else
        {
            ClearSelectionVisuals();
        }
    }

    private void OnPageMouseLeave(object sender, MouseEventArgs e)
    {
        if (_selection.IsDragging) return;
        if (_hoverAnnotationId is not null) ScheduleHideHover();
    }

    /// <summary>拖拽中显示矩形选框（跨页则清除锚点页选框）。</summary>
    private void UpdateDragVisual(ReaderPageItemViewModel page)
    {
        var anchor = _activePageElement?.DataContext as ReaderPageItemViewModel;
        if (anchor is null) return;

        if (_selection.IsCrossPage || !ReferenceEquals(page, anchor))
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

    /// <summary>释放后按起止字符下标算文本选区矩形；失败返回 false（不弹菜单）。</summary>
    private bool ComputeSelectionGeometry(SelectionResult result, FrameworkElement element)
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

    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        if (_selectionPtRects.Count == 0) { CloseAllAndClear(); return; }
        var rect = AnnotationGeometry.Union(_selectionPtRects);
        _viewModel.AddHighlight(_selectionPageIndex, rect);
        CloseAllAndClear();
        ShowToast(ReaderStrings.HighlightApplied);
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

    private void ShowHover(FrameworkElement element, ReaderPageItemViewModel page, string annotationId)
    {
        CancelHideHover();
        _hoverPageIndex = page.Index;
        _hoverAnnotationId = annotationId;

        AnnotationOverlay? overlay = null;
        foreach (var o in page.Annotations)
        {
            if (o.Id == annotationId) { overlay = o; break; }
        }
        if (overlay is null) { HideHover(); return; }

        var record = _viewModel.FindAnnotation(page.Index, annotationId);
        HoverEditButton.IsEnabled = record?.Kind == AnnotationKind.Note;

        _anchorElement = element;
        _anchorPageRectDip = (overlay.Value.X, overlay.Value.Y, overlay.Value.Width, overlay.Value.Height);

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
        if (_activePageElement?.DataContext is ReaderPageItemViewModel page)
        {
            page.SetSelectionRects([]);
        }
        _activePageElement = null;
        _selectionPageIndex = -1;
        _selectionAnchorChar = -1;
        _selectionEndChar = -1;
        _selectionPtRects = [];
        _selection.Cancel();
    }

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
        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
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

    // ------------------------------------------------------------ 页码跳转（FR-05）

    private void OnJumpBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.JumpToPageCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.JumpText = "";
            _viewModel.IsJumpInvalid = false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // T2：Esc 逐层收起弹层（子菜单→主菜单→关闭）
        if (e.Key == Key.Escape && _menu.IsOpen)
        {
            e.Handled = true;
            if (!_menu.Back()) CloseAllAndClear();
            else SyncPopups();
            return;
        }

        // Ctrl+G 定位页码输入框（FR-05）
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control && !JumpBox.IsKeyboardFocused)
        {
            e.Handled = true;
            JumpBox.Focus();
            JumpBox.SelectAll();
        }
    }

    /// <summary>非法页码红边抖动（FR-05 AC-05.2）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.IsJumpInvalid) && _viewModel.IsJumpInvalid)
        {
            PlayInvalidJumpAnimation();
        }
    }

    private void PlayInvalidJumpAnimation()
    {
        if (JumpBox.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            JumpBox.RenderTransform = transform;
        }

        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-5, TimeSpan.FromMilliseconds(60)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(5, TimeSpan.FromMilliseconds(120)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-4, TimeSpan.FromMilliseconds(180)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(4, TimeSpan.FromMilliseconds(240)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(300)));
        transform.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    /// <summary>笔记输入上下文（新建=先落高亮后替换；编辑=按 Id 更新）。</summary>
    private sealed record NoteContext(int PageIndex, PdfRect Rect, string? AnchorHighlightId, string? EditingId);
}
