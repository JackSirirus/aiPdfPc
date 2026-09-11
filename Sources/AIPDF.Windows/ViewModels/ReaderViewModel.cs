// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 虚拟化阅读视图 ViewModel：连续滚动的渲染请求状态机 + 缩放体系 + 页码跳转。
//
// 渲染消费（T1 PdfiumRenderService 真实 API，以 f85c02f 提交源码为准）：
//   OpenDocument(path, password) → docId（规范化路径）；GetPageCount / GetPageSize（打开时已缓存全量页尺寸）；
//   RenderPageAsync(docId, pageIndex, dpi, zoomPct) → Task<BitmapSource>（后台线程，冻结位图）。
// 已知 API 缺口（见 .omo/notepads/pdf-reader-impl/learnings.md，本任务用适配层绕过）：
//   ① RenderPageAsync 无 CancellationToken —— 缩放/滚出视口后在途结果按「规格不匹配」丢弃，不绑定；
//   ② 无显式页缓存释放 API —— 视口外页释放本侧位图引用并发出 PageReleased 事件（T1 LRU 按字节预算自淘汰），
//      未来 T1 若提供 ReleasePage(docId, pageIndex) 即为该事件挂点；
//   ③ 无渲染优先级 API —— 可视区与缓冲页同批请求，V1 依赖 T1 后台线程的近 FIFO 顺序。
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Views.Reader;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.ViewModels;

/// <summary>滚动请求参数：目标纵向偏移（DIP）。视图收到后调用 ScrollViewer.ScrollToVerticalOffset。</summary>
public sealed class ReaderScrollRequestEventArgs(double offset) : EventArgs
{
    /// <summary>目标滚动偏移（视口顶部，DIP）。</summary>
    public double Offset { get; } = offset;
}

/// <summary>
/// 单页视图模型：持有页尺寸（pt）与当前 zoom 下的显示尺寸（DIP）。
/// 占位容器始终按显示尺寸绘制（骨架保持正确宽高比【ST-10】）；渲染完成后替换位图，无闪烁。
/// </summary>
public partial class ReaderPageItemViewModel : ObservableObject
{
    public ReaderPageItemViewModel(int index, double widthPt, double heightPt)
    {
        Index = index;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    /// <summary>页索引（0 起）。</summary>
    public int Index { get; }

    /// <summary>页码（1 起，展示用）。</summary>
    public int Number => Index + 1;

    /// <summary>页宽（PDF 点）。</summary>
    public double WidthPt { get; }

    /// <summary>页高（PDF 点）。</summary>
    public double HeightPt { get; }

    /// <summary>显示宽（DIP）= pt × zoom × DpiScale。</summary>
    [ObservableProperty]
    private double _displayWidth;

    /// <summary>显示高（DIP）。</summary>
    [ObservableProperty]
    private double _displayHeight;

    /// <summary>渲染完成的位图（冻结，可跨线程）。为 null 时显示骨架占位。</summary>
    [ObservableProperty]
    private BitmapSource? _bitmap;

    /// <summary>渲染请求在途。</summary>
    [ObservableProperty]
    private bool _isRendering;

    /// <summary>是否已持有位图（决定占位/位图切换）。</summary>
    public bool IsRendered => Bitmap is not null;

    /// <summary>页内搜索高亮（DIP，相对页左上角）。由 <see cref="ReaderViewModel.SetSearchHighlight"/> 填充，
    /// 缩放变更时按新显示尺寸重算（见 <see cref="RebuildHighlights"/>）。</summary>
    public ObservableCollection<SearchHighlight> Highlights { get; } = [];

    /// <summary>本页是否持有搜索命中矩形（缩放重算的触发条件，避免空集合无谓清空通知）。</summary>
    internal bool HasSearchRects => _searchRects.Count > 0;

    /// <summary>搜索命中的源矩形（PDF 点，左上原点）；缩放重算的真源。</summary>
    private IReadOnlyList<PdfRect> _searchRects = [];

    /// <summary>设置本页高亮源矩形并立即按当前显示比例换算（命中跳转时调用）。</summary>
    internal void SetSearchRects(IReadOnlyList<PdfRect> rects)
    {
        _searchRects = rects ?? [];
        RebuildHighlights();
    }

    /// <summary>清空本页高亮（切换命中/退出搜索）。</summary>
    internal void ClearSearchRects()
    {
        _searchRects = [];
        if (Highlights.Count > 0) Highlights.Clear();
    }

    /// <summary>按当前 <see cref="DisplayWidth"/> 重新换算全部高亮（缩放变更/首次设置）。</summary>
    internal void RebuildHighlights()
    {
        Highlights.Clear();
        if (_searchRects.Count == 0 || WidthPt <= 0) return;
        foreach (var rect in _searchRects)
        {
            Highlights.Add(PageLayoutCalculator.ToSearchHighlightDip(rect, WidthPt, DisplayWidth));
        }
    }

    /// <summary>在途渲染请求的规格（dpi, zoomPct）；结果到达时规格不匹配即丢弃（无取消 API 的适配）。</summary>
    internal (int Dpi, int ZoomPct)? PendingKey { get; set; }

    /// <summary>当前位图对应的渲染规格。</summary>
    internal (int Dpi, int ZoomPct)? RenderedKey { get; set; }

    // ------------------------------------------------------------------ 批注（V2 T1）

    /// <summary>
    /// 页内批注叠加条目（DIP，相对页左上角）。独立于搜索高亮 <see cref="Highlights"/>：
    /// ReaderView 用独立的 IsHitTestVisible=False 层承载，不改动 <c>SetSearchHighlight</c> 语义。
    /// </summary>
    public ObservableCollection<AnnotationOverlay> Annotations { get; } = [];

    /// <summary>拖选中的选区显示矩形（DIP，相对页左上角）。由 ReaderView 交互临时填充，
    /// 释放/清除后为空；与持久批注层分离，避免交互态污染批注记录。</summary>
    public ObservableCollection<SearchHighlight> SelectionRects { get; } = [];

    /// <summary>设置本页临时选区矩形（DIP）。传空即清除。</summary>
    internal void SetSelectionRects(IReadOnlyList<SearchHighlight> rects)
    {
        SelectionRects.Clear();
        if (rects is null) return;
        foreach (var r in rects) SelectionRects.Add(r);
    }

    /// <summary>批注真源记录（PDF 点，左上原点）。叠加层为其 DIP 投影；滚出视口只释放投影不丢记录。</summary>
    private readonly List<AnnotationRecord> _annotationRecords = [];

    /// <summary>本页是否存在批注记录（缩放重算触发条件）。</summary>
    internal bool HasAnnotationRecords => _annotationRecords.Count > 0;

    /// <summary>本页批注记录只读视图。</summary>
    internal IReadOnlyList<AnnotationRecord> AnnotationRecords => _annotationRecords;

    /// <summary>整体替换本页批注记录并重建投影（T3/T4 写回后刷新用）。</summary>
    internal void SetAnnotationRecords(IReadOnlyList<AnnotationRecord> records)
    {
        _annotationRecords.Clear();
        if (records is not null) _annotationRecords.AddRange(records);
        RebuildAnnotations();
    }

    /// <summary>追加一条批注记录并重建投影。返回 false 表示同一 Id 已存在（幂等）。</summary>
    internal bool AddAnnotationRecord(AnnotationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_annotationRecords.Any(a => a.Id == record.Id)) return false;
        _annotationRecords.Add(record);
        RebuildAnnotations();
        return true;
    }

    /// <summary>按 Id 移除批注记录并重建投影。</summary>
    internal bool RemoveAnnotationRecord(string annotationId)
    {
        var removed = _annotationRecords.RemoveAll(a => a.Id == annotationId);
        if (removed > 0) RebuildAnnotations();
        return removed > 0;
    }

    /// <summary>按 Id 替换批注记录（FR-13 编辑笔记 / 改色后就地更新，Id 不变）并重建投影。</summary>
    internal bool UpdateAnnotationRecord(AnnotationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var index = _annotationRecords.FindIndex(a => a.Id == record.Id);
        if (index < 0) return false;
        _annotationRecords[index] = record;
        RebuildAnnotations();
        return true;
    }

    /// <summary>按当前 <see cref="DisplayWidth"/> 重算全部批注叠加（缩放变更/记录变化）。</summary>
    internal void RebuildAnnotations()
    {
        Annotations.Clear();
        if (_annotationRecords.Count == 0 || WidthPt <= 0) return;
        foreach (var record in _annotationRecords)
        {
            Annotations.Add(AnnotationOverlay.FromRecord(record, WidthPt, DisplayWidth));
        }
    }

    /// <summary>只释放 DIP 投影（PageReleased 钩子【缺口②】）；批注记录保留，滚回视口时经
    /// <see cref="EnsureAnnotationsProjected"/> 重建，保证虚拟化不丢批注。</summary>
    internal void ReleaseAnnotationOverlays()
    {
        if (Annotations.Count > 0) Annotations.Clear();
    }

    /// <summary>页重新进入视口时按需重建投影（释放后滚回/宿主重挂）。</summary>
    internal void EnsureAnnotationsProjected()
    {
        if (HasAnnotationRecords && Annotations.Count == 0) RebuildAnnotations();
    }

    partial void OnBitmapChanged(BitmapSource? value) => OnPropertyChanged(nameof(IsRendered));
}

/// <summary>
/// 阅读视图模型：虚拟化连续滚动（可见页 ± 缓冲页渲染请求，其余占位）+ 九档缩放阶梯 + 适配宽度/页面 + 页码跳转。
/// 必须在 UI 线程构造（捕获 SynchronizationContext 用于渲染结果回传；测试环境无同步上下文时内联执行）。
/// </summary>
public partial class ReaderViewModel : ObservableObject, IDisposable
{
    /// <summary>可视区两侧的渲染缓冲页数（FR-01：仅可视页 ± 缓冲页持有位图）。</summary>
    public const int DefaultBufferPages = 2;

    private readonly PdfiumRenderService? _injectedService;
    private PdfiumRenderService? _service;
    private readonly bool _ownsService;
    private readonly ConcurrentDictionary<int, (int Dpi, int ZoomPct)> _inFlight = new();
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;

    // ---- 选区文本命中（T2 H-5）：惰性打开的独立文本层句柄 ----
    // 渲染/文本各持句柄是 V1 刻意取舍（notepad）；此处选区命中再持一份，仅在首次拖选时惰性打开，
    // 不选中的用户零开销。独立于 MainWindow 搜索栈，保证 ReaderView 可独立实例化与测试。
    private readonly PdfiumTextService? _injectedTextService;
    private PdfiumTextService? _textService;
    private PdfiumTextService.TextDocument? _textDoc;
    private string? _documentPath;
    private readonly Dictionary<int, bool> _textLayerCache = new();

    private string? _docId;
    private List<PageSize> _pageSizes = [];
    private int[] _pageRotations = [];
    private double[] _offsets = [];
    private double[] _heights = [];
    private double _scrollTop;
    private double _viewportHeight;
    private double _viewportWidth;
    private double _appliedZoom = 100;
    private bool _hasLayout;

    /// <summary>当前持有高亮的页索引（-1 = 无）；切换命中时只清上一页，避免遍历全部页。</summary>
    private int _searchHighlightPage = -1;

    /// <summary>构造。renderService 为空时首次打开文档自建并持有（随 Dispose 释放）；测试/宿主可注入。
    /// textService 同样为空时惰性自建（仅首次选区命中）；测试可注入独立实例。</summary>
    public ReaderViewModel(PdfiumRenderService? renderService = null, int bufferPages = DefaultBufferPages,
        PdfiumTextService? textService = null)
    {
        _injectedService = renderService;
        _ownsService = renderService is null;
        _injectedTextService = textService;
        BufferPages = Math.Max(0, bufferPages);
    }

    /// <summary>可视区两侧缓冲页数。</summary>
    public int BufferPages { get; }

    /// <summary>页面集合（虚拟化 ItemsControl 的 ItemsSource；仅持有尺寸元数据，位图按需）。</summary>
    public ObservableCollection<ReaderPageItemViewModel> Pages { get; } = [];

    /// <summary>视口外页释放通知：参数为 (页索引, 估算字节数)。
    /// T1 无显式释放 API（缺口②），LRU 按字节预算自淘汰；宿主可订阅此事件做观测，未来接到 T1 ReleasePage。</summary>
    public event EventHandler<(int PageIndex, long EstimatedBytes)>? PageReleased;

    /// <summary>请求视图滚动到指定偏移（跳页、缩放锚定均经此事件，由视图执行实际滚动）。</summary>
    public event EventHandler<ReaderScrollRequestEventArgs>? ScrollRequested;

    /// <summary>
    /// 批注集合发生变化（新增/移除/更新）。T4 保存链路据此重置 2s 防抖并提交引擎；
    /// T2 只创建/修改客户端批注，不实现保存。
    /// </summary>
    public event EventHandler? AnnotationsChanged;

    /// <summary>
    /// 携带变化类别与记录快照的批注事件（T4 保存链路消费）：
    /// Removed 携带移除前的记录，供保存会话决定是否需要引擎 delete（T4 H-4）。
    /// </summary>
    public event EventHandler<AnnotationChangedEventArgs>? AnnotationChanged;

    private void RaiseAnnotationsChanged() => AnnotationsChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseAnnotationChanged(AnnotationChangeKind kind, AnnotationRecord record)
    {
        RaiseAnnotationsChanged();
        AnnotationChanged?.Invoke(this, new AnnotationChangedEventArgs(kind, record));
    }

    // ------------------------------------------------------------------ 绑定状态

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicatorText))]
    [NotifyPropertyChangedFor(nameof(ZoomDisplayText))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    private double _zoomPercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicatorText))]
    private int _currentPageNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicatorText))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private int _pageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicatorText))]
    private bool _hasDocument;

    [ObservableProperty]
    private string _jumpText = "";

    [ObservableProperty]
    private bool _isJumpInvalid;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    private bool _isFitWidthActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    private bool _isFitPageActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>状态栏页码指示（FR-05 / ST-03：加载中显示「— / …」）。</summary>
    public string PageIndicatorText => HasDocument && PageCount > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{CurrentPageNumber} / {PageCount}")
        : "— / …";

    /// <summary>状态栏缩放显示。</summary>
    public string ZoomDisplayText => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(ZoomPercent)}%");

    /// <summary>是否存在加载错误（控制错误条显隐）。</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>缩放端点【ST-11】：到 400% 后放大按钮禁用。</summary>
    public bool CanZoomIn => ZoomPercent < PageLayoutCalculator.LadderPercent[^1] - PageLayoutCalculator.Epsilon;

    /// <summary>缩放端点【ST-11】：到 25% 后缩小按钮禁用。</summary>
    public bool CanZoomOut => ZoomPercent > PageLayoutCalculator.LadderPercent[0] + PageLayoutCalculator.Epsilon;

    // ------------------------------------------------------------------ 文档加载

    /// <summary>
    /// 打开文档并构建虚拟化布局（页尺寸一次取自 T1 缓存）。失败写入 ErrorMessage（ST-06，模态对话框由宿主 T5 负责）。
    /// </summary>
    public void LoadDocument(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ErrorMessage = null;

        try
        {
            var svc = _injectedService ?? _service;
            if (svc is null) svc = CreateService();
            _service = svc;
            var docId = svc.OpenDocument(path, password);

            // 重复加载：释放上一份文档（T1 句柄去重同路径，不同路径才真正关闭）
            if (_docId is not null && _docId != docId)
            {
                try { svc.CloseDocument(_docId); }
                catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 关闭旧文档失败: {ex.Message}"); }
            }

            var count = svc.GetPageCount(docId);
            var sizes = new List<PageSize>(Math.Max(0, count));
            var rotations = new int[Math.Max(0, count)];
            for (var i = 0; i < count; i++)
            {
                // T1 打开时已解析全部页尺寸，这里零解析成本
                sizes.Add(svc.GetPageSize(docId, i) ?? new PageSize(595, 842));
                // /Rotate 度数（M-11 禁选判定；打开时一次解析）
                rotations[i] = svc.GetPageRotation(docId, i);
            }

            _docId = docId;
            _service = svc;
            _pageSizes = sizes;
            _pageRotations = rotations;
            _inFlight.Clear();
            _scrollTop = 0;

            // 文档切换：关闭旧选区文本句柄并清空缓存（新路径惰性重开）
            if (!string.Equals(_documentPath, docId, StringComparison.OrdinalIgnoreCase))
            {
                CloseTextDocument();
            }
            _documentPath = docId;
            _textLayerCache.Clear();

            Pages.Clear();
            for (var i = 0; i < count; i++) Pages.Add(new ReaderPageItemViewModel(i, sizes[i].Width, sizes[i].Height));

            PageCount = count;
            HasDocument = count > 0;
            CurrentPageNumber = count > 0 ? 1 : 0;
            _hasLayout = count > 0;

            RebuildLayout(ZoomPercent);

            if (_hasLayout)
            {
                // 回到文档顶部并触发首批渲染（视口未测量时由视图首帧 ScrollChanged 补触发）
                UpdateViewportCore(0);
                ScrollRequested?.Invoke(this, new ReaderScrollRequestEventArgs(0));
            }
        }
        catch (PdfiumOpenException ex)
        {
            _hasLayout = false;
            HasDocument = false;
            PageCount = 0;
            _pageRotations = [];
            Pages.Clear();
            ErrorMessage = ReaderStrings.LoadErrorPrefix + ex.Message;
        }
    }

    private static PdfiumRenderService CreateService() => new();

    /// <summary>
    /// Close-first 重载（T4 唯一真相源，BLK-2/BLK-3）：就地保存后旧句柄对应的渲染位图不再包含
    /// 新写入的内嵌注释，必须关闭旧句柄并重开同路径（换新代际 + 空 LRU），否则
    /// <see cref="PdfiumRenderService.OpenDocument"/> 按路径去重返回旧句柄 → 叠加层清了但注释不出现。
    ///
    /// 视口保留（AC-20.2）：重载前后保留滚动偏移与缩放，仅清页位图引用与页级叠加投影；
    /// 未保存批注记录保留并按坐标重投影。CloseDocument 内部阻塞等待在途原生调用归零（drain）。
    /// </summary>
    public void ReloadDocument(string? password = null)
    {
        if (_documentPath is null || _service is null || _docId is null) return;

        var path = _documentPath;
        var savedScroll = _scrollTop;
        var savedPage = CurrentPageNumber;
        var savedZoom = ZoomPercent;

        // 1) 作废页位图与叠加投影（记录保留，重载后重投影）
        foreach (var page in Pages)
        {
            page.Bitmap = null;
            page.RenderedKey = null;
            page.PendingKey = null;
            page.IsRendering = false;
            page.ReleaseAnnotationOverlays();
        }
        _inFlight.Clear();

        // 2) 独立文本层句柄先关（惰性重开用新文件内容）
        CloseTextDocument();
        _textLayerCache.Clear();

        // 3) close-first：CloseDocument 排空在途后关旧句柄，再开新句柄（新代际/空 LRU）
        try
        {
            _service.CloseDocument(_docId);
            _docId = _service.OpenDocument(path, password);
            _documentPath = _docId;

            var count = _service.GetPageCount(_docId);
            var sizes = new List<PageSize>(Math.Max(0, count));
            var rotations = new int[Math.Max(0, count)];
            for (var i = 0; i < count; i++)
            {
                sizes.Add(_service.GetPageSize(_docId, i) ?? new PageSize(595, 842));
                rotations[i] = _service.GetPageRotation(_docId, i);
            }

            _pageSizes = sizes;
            _pageRotations = rotations;

            // 页数不变（就地保存不改变页数）：保留页 VM 与批注记录，仅重建布局
            if (count != Pages.Count)
            {
                var existingRecords = Pages
                    .SelectMany(p => p.AnnotationRecords)
                    .Where(r => r.PageIndex < count)
                    .GroupBy(r => r.PageIndex)
                    .ToDictionary(g => g.Key, g => g.ToList());
                Pages.Clear();
                for (var i = 0; i < count; i++)
                {
                    var item = new ReaderPageItemViewModel(i, sizes[i].Width, sizes[i].Height);
                    if (existingRecords.TryGetValue(i, out var records)) item.SetAnnotationRecords(records);
                    Pages.Add(item);
                }
            }

            PageCount = count;
            HasDocument = count > 0;
            _hasLayout = count > 0;

            // 4) 重建布局（含未保存批注按坐标重投影），恢复缩放与视口
            RebuildLayout(savedZoom);
            if (!_hasLayout) return;

            if (CurrentPageNumber != savedPage && savedPage > 0 && savedPage <= PageCount)
            {
                CurrentPageNumber = savedPage;
            }

            UpdateViewportCore(savedScroll);
            ScrollRequested?.Invoke(this, new ReaderScrollRequestEventArgs(savedScroll));
        }
        catch (PdfiumOpenException ex)
        {
            _hasLayout = false;
            HasDocument = false;
            PageCount = 0;
            ErrorMessage = ReaderStrings.LoadErrorPrefix + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 视口驱动（视图 ScrollChanged / 缩放锚定调用）

    /// <summary>视图上报视口变化（滚动/尺寸）。计算可见 ± 缓冲页集合：进入则请求渲染，离开则释放。</summary>
    public void UpdateViewport(double scrollTop, double viewportHeight, double viewportWidth)
    {
        _viewportHeight = viewportHeight;
        _viewportWidth = viewportWidth;
        UpdateViewportCore(scrollTop);
    }

    private void UpdateViewportCore(double scrollTop)
    {
        _scrollTop = scrollTop;
        if (!_hasLayout) return;

        var (first, last) = PageLayoutCalculator.VisibleRange(_offsets, _heights, scrollTop, _viewportHeight, BufferPages);
        if (last >= first)
        {
            for (var i = first; i <= last; i++) StartRender(Pages[i]);
        }

        // 视口外页释放：丢本侧位图引用 + 取消在途标记（缺口②适配，LRU 挂点见 PageReleased）
        var wantedCount = last - first + 1;
        if (wantedCount < Pages.Count)
        {
            foreach (var page in Pages)
            {
                if (page.Index >= first && page.Index <= last) continue;
                ReleasePage(page);
            }
        }

        var current = PageLayoutCalculator.CurrentPageByMaxOverlap(_offsets, _heights, scrollTop, _viewportHeight);
        if (current >= 0 && current + 1 != CurrentPageNumber) CurrentPageNumber = current + 1;
    }

    private void ReleasePage(ReaderPageItemViewModel page)
    {
        if (page.Bitmap is not null)
        {
            var bytes = (long)page.Bitmap.PixelWidth * page.Bitmap.PixelHeight * 4;
            page.Bitmap = null;
            page.RenderedKey = null;
            PageReleased?.Invoke(this, (page.Index, bytes));
        }

        // 页级批注叠加层随页释放（记录保留，滚回视口重建），与搜索高亮层互不影响。
        page.ReleaseAnnotationOverlays();

        if (page.PendingKey is not null)
        {
            // 清除在途标记：结果到达时因规格不匹配被丢弃（缺口①适配）
            page.PendingKey = null;
            page.IsRendering = false;
        }
    }

    /// <summary>请求渲染一页（若尚无该规格的位图/在途请求）。渲染全走 T1 后台线程，UI 线程零渲染。</summary>
    private void StartRender(ReaderPageItemViewModel page)
    {
        var service = _service;
        var docId = _docId;
        if (service is null || docId is null) return;

        var key = CurrentRenderKey();
        if (page.RenderedKey == key || page.PendingKey == key) return;

        // 页重新进入渲染窗口：若批注投影曾随释放清空，按当前显示尺寸重建（虚拟化不丢批注）
        page.EnsureAnnotationsProjected();

        page.PendingKey = key;
        page.IsRendering = true;
        _inFlight[page.Index] = key;

        _ = Task.Run(async () =>
        {
            BitmapSource? bitmap = null;
            Exception? failure = null;
            try
            {
                bitmap = await service.RenderPageAsync(docId, page.Index, key.Dpi, key.ZoomPct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Marshal(() =>
            {
                if (_inFlight.TryGetValue(page.Index, out var running) && running == key)
                {
                    _inFlight.TryRemove(page.Index, out _);
                }

                // 规格不匹配（期间缩放/滚出视口/文档切换）→ 丢弃结果不绑定（缺口①适配）
                if (page.PendingKey != key || docId != _docId) return;

                page.PendingKey = null;
                page.IsRendering = false;

                if (failure is not null)
                {
                    ErrorMessage ??= ReaderStrings.RenderErrorPrefix + failure.Message;
                    return;
                }

                page.Bitmap = bitmap;
                page.RenderedKey = key;
            });
        });
    }

    private (int Dpi, int ZoomPct) CurrentRenderKey()
        => ((int)PageLayoutCalculator.DpiForZoom(ZoomPercent), (int)Math.Round(ZoomPercent));

    /// <summary>渲染结果回传 UI 线程（构造时捕获同步上下文；测试环境无上下文时内联）。</summary>
    private void Marshal(Action action)
    {
        var ctx = _syncContext;
        if (ctx is null) action();
        else ctx.Post(_ => action(), null);
    }

    // ------------------------------------------------------------------ 缩放

    partial void OnZoomPercentChanged(double value)
    {
        if (!_hasLayout || Math.Abs(value - _appliedZoom) < PageLayoutCalculator.Epsilon) return;

        // 保留视口中心锚点，重建布局后请求滚动到对应位置
        var oldOffsets = _offsets;
        var oldHeights = _heights;
        RebuildLayout(value);

        var target = PageLayoutCalculator.ZoomAnchorScroll(oldOffsets, oldHeights, _scrollTop, _viewportHeight, _offsets, _heights);
        UpdateViewportCore(target); // 立即按新布局发起渲染（不等视图回传）
        ScrollRequested?.Invoke(this, new ReaderScrollRequestEventArgs(target));
    }

    private void RebuildLayout(double zoomPercent)
    {
        _appliedZoom = zoomPercent;
        _offsets = PageLayoutCalculator.ComputePageOffsets(_pageSizes, zoomPercent);
        _heights = PageLayoutCalculator.ComputePageHeights(_pageSizes, zoomPercent);
        var scale = zoomPercent / 100.0 * PageLayoutCalculator.DpiScale;
        for (var i = 0; i < Pages.Count && i < _pageSizes.Count; i++)
        {
            Pages[i].DisplayWidth = _pageSizes[i].Width * scale;
            Pages[i].DisplayHeight = _pageSizes[i].Height * scale;
            // 缩放变更：搜索高亮与批注叠加层按新显示尺寸重算（源矩形为 pt，不随缩放变化）
            if (Pages[i].HasSearchRects) Pages[i].RebuildHighlights();
            if (Pages[i].HasAnnotationRecords) Pages[i].RebuildAnnotations();
        }
    }

    /// <summary>状态栏 ＋ 逐档放大（FR-06），脱离适配档。</summary>
    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn()
    {
        LeaveFitMode();
        ZoomPercent = PageLayoutCalculator.StepLadder(ZoomPercent, 1);
    }

    /// <summary>状态栏 − 逐档缩小（FR-06），脱离适配档。</summary>
    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut()
    {
        LeaveFitMode();
        ZoomPercent = PageLayoutCalculator.StepLadder(ZoomPercent, -1);
    }

    /// <summary>Ctrl+滚轮（视图转发档位步数）：吸附阶梯，落在最近档。</summary>
    public void ZoomByWheelSteps(int steps)
    {
        if (!_hasLayout || steps == 0) return;
        LeaveFitMode();
        ZoomPercent = PageLayoutCalculator.StepLadder(ZoomPercent, steps);
    }

    /// <summary>适配宽度（FR-06 动态档）：按视口宽与最大页宽重算 zoom；窗口 resize 后由视图再次调用本命令。</summary>
    [RelayCommand]
    private void FitWidth()
    {
        if (!_hasLayout || _viewportWidth <= 0) return;
        var maxPageWidth = 0.0;
        foreach (var p in _pageSizes) maxPageWidth = Math.Max(maxPageWidth, p.Width);
        IsFitPageActive = false;
        IsFitWidthActive = true;
        ZoomPercent = PageLayoutCalculator.FitWidthZoomPercent(_viewportWidth, maxPageWidth);
    }

    /// <summary>适配页面（FR-06 动态档）：宽高取更紧约束。</summary>
    [RelayCommand]
    private void FitPage()
    {
        if (!_hasLayout || _viewportWidth <= 0 || _viewportHeight <= 0) return;
        var maxPageWidth = 0.0;
        var maxPageHeight = 0.0;
        foreach (var p in _pageSizes)
        {
            maxPageWidth = Math.Max(maxPageWidth, p.Width);
            maxPageHeight = Math.Max(maxPageHeight, p.Height);
        }
        IsFitWidthActive = false;
        IsFitPageActive = true;
        ZoomPercent = PageLayoutCalculator.FitPageZoomPercent(_viewportWidth, _viewportHeight, maxPageWidth, maxPageHeight);
    }

    private void LeaveFitMode()
    {
        IsFitWidthActive = false;
        IsFitPageActive = false;
    }

    // ------------------------------------------------------------------ 页码跳转（FR-05）

    /// <summary>跳转输入框回车：合法滚动到位；非法标记 IsJumpInvalid（视图红边抖动，拒绝跳转）。</summary>
    [RelayCommand]
    private void JumpToPage()
    {
        if (!_hasLayout) return;
        if (PageLayoutCalculator.TryParsePageNumber(JumpText, PageCount, out var page))
        {
            IsJumpInvalid = false;
            GoToPage(page);
        }
        else
        {
            IsJumpInvalid = true;
        }
    }

    /// <summary>跳转到指定页（1 起）：滚动至该页顶部。供缩略图/目录/书签面板（T4/T8）复用。</summary>
    public void GoToPage(int page)
    {
        if (!_hasLayout || page < 1 || page > PageCount) return;
        var offset = Math.Max(0, _offsets[page - 1]);
        UpdateViewportCore(offset);
        ScrollRequested?.Invoke(this, new ReaderScrollRequestEventArgs(offset));
    }

    partial void OnJumpTextChanged(string value) => IsJumpInvalid = false;

    // ------------------------------------------------------------------ 搜索高亮（T8）

    /// <summary>
    /// 在指定页绘制搜索命中高亮（矩形为 PDF 点、左上原点；源矩形保存后按当前显示比例换 DIP）。
    /// 只保留一页高亮（切换命中先清上一页）：高亮层由 ReaderView 叠加，命中测试穿透。
    /// </summary>
    public void SetSearchHighlight(int pageIndex, IReadOnlyList<PdfRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;

        if (_searchHighlightPage >= 0 && _searchHighlightPage < Pages.Count && _searchHighlightPage != pageIndex)
        {
            Pages[_searchHighlightPage].ClearSearchRects();
        }

        Pages[pageIndex].SetSearchRects(rects);
        _searchHighlightPage = pageIndex;
    }

    /// <summary>清除全部搜索高亮（退出搜索面板 / 切换面板）。</summary>
    public void ClearSearchHighlight()
    {
        if (_searchHighlightPage >= 0 && _searchHighlightPage < Pages.Count)
        {
            Pages[_searchHighlightPage].ClearSearchRects();
        }
        _searchHighlightPage = -1;
    }

    // ------------------------------------------------------------------ 批注（V2 T1：模型 + 叠加层；创建入口由 T2 浮菜单交付）

    /// <summary>
    /// 本页是否允许批注（M-11 决策）：页有效且非 /Rotate 旋转页。
    /// PdfCoordinateMapper 无 derotation 变换，旋转页的选区/高亮矩形会与 PDFium 未旋转页面空间
    /// （PdfiumTextService.GetTextRects）错位；补 derotation 需 X/Y 交换并与 engine derotation_matrix
    /// 对齐，超出 T1 范围。V2 明确【禁止旋转页批注】（T2 据此禁选），derotation 落点留待后续版本。
    /// 证据：.omo/evidence/v2-task1-annotrender.txt。
    /// </summary>
    public bool CanAnnotate(int pageIndex)
        => HasDocument && pageIndex >= 0 && pageIndex < Pages.Count
           && pageIndex < _pageRotations.Length && _pageRotations[pageIndex] % 360 == 0;

    /// <summary>新增高亮批注（T2 浮菜单「高亮」消费）。旋转页/越界返回 null（不创建）。</summary>
    public AnnotationRecord? AddHighlight(int pageIndex, PdfRect rect, string? color = null)
        => AddAnnotation(AnnotationRecord.CreateHighlight(pageIndex, Normalize(rect), color));

    /// <summary>新增笔记批注（T2 浮菜单「笔记」消费）。旋转页/越界返回 null（不创建）。</summary>
    public AnnotationRecord? AddNote(int pageIndex, PdfRect rect, string note, string? color = null)
        => AddAnnotation(AnnotationRecord.CreateNote(pageIndex, Normalize(rect), note, color));

    /// <summary>新增批注记录（幂等：同 Id 不重复）。越界/旋转页返回 null。</summary>
    public AnnotationRecord? AddAnnotation(AnnotationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!CanAnnotate(record.PageIndex)) return null;
        if (!Pages[record.PageIndex].AddAnnotationRecord(record)) return null;
        RaiseAnnotationChanged(AnnotationChangeKind.Added, record);
        return record;
    }

    /// <summary>按 Id 移除批注（T2 悬停删除消费）。返回是否命中。</summary>
    public bool RemoveAnnotation(int pageIndex, string annotationId)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return false;
        var removed = Pages[pageIndex].AnnotationRecords.FirstOrDefault(a => a.Id == annotationId);
        if (removed is null || !Pages[pageIndex].RemoveAnnotationRecord(annotationId)) return false;
        RaiseAnnotationChanged(AnnotationChangeKind.Removed, removed);
        return true;
    }

    /// <summary>按 Id 替换批注（T2 悬停编辑笔记/改色消费，Id 不变）。返回是否命中。</summary>
    public bool UpdateAnnotation(AnnotationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.PageIndex < 0 || record.PageIndex >= Pages.Count) return false;
        if (!Pages[record.PageIndex].UpdateAnnotationRecord(record)) return false;
        RaiseAnnotationChanged(AnnotationChangeKind.Updated, record);
        return true;
    }

    /// <summary>按 Id 查批注记录（悬停操作消费）。未命中返回 null。</summary>
    public AnnotationRecord? FindAnnotation(int pageIndex, string annotationId)
        => GetAnnotations(pageIndex).FirstOrDefault(a => a.Id == annotationId);

    /// <summary>本页批注记录快照（T3/T4 保存链路消费）。</summary>
    public IReadOnlyList<AnnotationRecord> GetAnnotations(int pageIndex)
        => pageIndex >= 0 && pageIndex < Pages.Count ? Pages[pageIndex].AnnotationRecords : [];

    /// <summary>全部页的批注记录快照（T4 保存批次构建消费）。</summary>
    public IReadOnlyList<AnnotationRecord> GetAllRecords()
    {
        var list = new List<AnnotationRecord>();
        foreach (var page in Pages)
        {
            if (page.AnnotationRecords.Count > 0) list.AddRange(page.AnnotationRecords);
        }
        return list;
    }

    /// <summary>
    /// 静默移除指定 Id 的批注记录（T4 保存成功后清理已内嵌记录，不再触发 AnnotationsChanged——
    /// 否则会错误地重启防抖并重复提交）。不存在的 Id 忽略。
    /// </summary>
    internal void RemoveAnnotationsById(IEnumerable<string> annotationIds)
    {
        var set = annotationIds as ISet<string> ?? annotationIds.ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) return;
        foreach (var page in Pages)
        {
            if (page.AnnotationRecords.Count == 0) continue;
            var ids = page.AnnotationRecords.Where(r => set.Contains(r.Id)).Select(r => r.Id).ToList();
            foreach (var id in ids) page.RemoveAnnotationRecord(id);
        }
    }

    /// <summary>当前文档路径（规范化全路径；未加载为 null）。T4 保存会话消费。</summary>
    public string? DocumentPath => _documentPath;

    /// <summary>规范化负宽高矩形（拖拽方向任意），与 PdfCoordinateMapper.Normalize 同口径。</summary>
    private static PdfRect Normalize(PdfRect rect)
        => new(Math.Min(rect.Left, rect.Right), Math.Min(rect.Top, rect.Bottom),
               Math.Max(rect.Left, rect.Right), Math.Max(rect.Top, rect.Bottom));

    // ------------------------------------------------------------------ 选区命中（V2 T2 / H-5）

    /// <summary>
    /// 当页是否有文本层：按当页 <c>CountPageChars(page) &gt; 0</c> 即时判定（不依赖 SearchIndexService
    /// 的惰性索引进度，H-5）；结果按页缓存，文档切换时清空。打不开文本句柄时保守返回 false。
    /// </summary>
    public bool PageHasTextLayer(int pageIndex)
    {
        if (!HasDocument || pageIndex < 0 || pageIndex >= Pages.Count) return false;
        if (_textLayerCache.TryGetValue(pageIndex, out var cached)) return cached;

        var has = false;
        try
        {
            var doc = EnsureTextDocument();
            if (doc is not null && _textService is not null)
            {
                has = _textService.CountPageChars(doc, pageIndex) > 0;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReaderViewModel] 文本层判定失败: 第 {pageIndex} 页 ({ex.Message})");
        }

        _textLayerCache[pageIndex] = has;
        return has;
    }

    /// <summary>
    /// 页容器内 DIP 点 → 字符下标（FPDFText_GetCharIndexAtPos，容差 8pt）。
    /// 仅当页非旋转（<see cref="CanAnnotate"/>）且有文本层时命中；坐标经 <see cref="PdfCoordinateMapper"/>
    /// 单一换算层（PRD 4.2.2），不新建换算。
    /// </summary>
    public bool TryHitCharAtPoint(int pageIndex, double viewXDip, double viewYDip, out int charIndex)
    {
        charIndex = -1;
        if (!HasDocument || pageIndex < 0 || pageIndex >= Pages.Count) return false;
        if (!CanAnnotate(pageIndex)) return false;      // 旋转页禁选（M-11）
        if (!PageHasTextLayer(pageIndex)) return false; // 无文本层不命中（ST-23）
        if (double.IsNaN(viewXDip) || double.IsNaN(viewYDip)) return false;

        var page = Pages[pageIndex];
        if (page.DisplayWidth <= 0 || page.WidthPt <= 0 || page.HeightPt <= 0) return false;

        try
        {
            var doc = EnsureTextDocument();
            if (doc is null || _textService is null) return false;

            var mapper = new PdfCoordinateMapper
            {
                PageWidthPt = page.WidthPt,
                PageHeightPt = page.HeightPt,
                Zoom = ZoomPercent / 100.0,
            };
            var pdfTopLeft = mapper.ViewToPdf(new Point(viewXDip, viewYDip));
            // PDFium 文本坐标为 PDF 左下原点：y = pageHeight - topLeftY
            var yBottomLeft = page.HeightPt - pdfTopLeft.Y;
            var hit = _textService.GetCharIndexAtPoint(doc, pageIndex, pdfTopLeft.X, yBottomLeft);
            if (hit < 0) return false;
            charIndex = hit;
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReaderViewModel] 字符命中失败: 第 {pageIndex} 页 ({ex.Message})");
            return false;
        }
    }

    /// <summary>两个字符下标之间的选区矩形（含端点，按行合并；PDF 点、左上原点）。</summary>
    public IReadOnlyList<PdfRect> GetSelectionRects(int pageIndex, int charIndexA, int charIndexB)
    {
        if (!HasDocument || pageIndex < 0 || pageIndex >= Pages.Count) return [];
        var start = Math.Min(charIndexA, charIndexB);
        var end = Math.Max(charIndexA, charIndexB);
        if (start < 0 || end < start) return [];

        try
        {
            var doc = EnsureTextDocument();
            return doc is null || _textService is null
                ? []
                : _textService.GetTextRects(doc, pageIndex, start, end - start + 1);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReaderViewModel] 选区矩形失败: 第 {pageIndex} 页 ({ex.Message})");
            return [];
        }
    }

    /// <summary>两个字符下标之间的选中文本（含端点）。</summary>
    public string GetSelectionText(int pageIndex, int charIndexA, int charIndexB)
    {
        if (!HasDocument || pageIndex < 0 || pageIndex >= Pages.Count) return string.Empty;
        var start = Math.Min(charIndexA, charIndexB);
        var end = Math.Max(charIndexA, charIndexB);
        if (start < 0 || end < start) return string.Empty;

        try
        {
            var doc = EnsureTextDocument();
            return doc is null || _textService is null
                ? string.Empty
                : _textService.GetTextRange(doc, pageIndex, start, end - start + 1);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReaderViewModel] 选区文本失败: 第 {pageIndex} 页 ({ex.Message})");
            return string.Empty;
        }
    }

    private PdfiumTextService.TextDocument? EnsureTextDocument()
    {
        if (_textDoc is not null) return _textDoc;
        if (_documentPath is null) return null;

        var svc = _injectedTextService ?? _textService;
        if (svc is null)
        {
            svc = new PdfiumTextService();
            _textService = svc;
        }
        _textDoc = svc.OpenDocument(_documentPath);
        return _textDoc;
    }

    private void CloseTextDocument()
    {
        try { _textDoc?.Dispose(); }
        catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 关闭文本句柄失败: {ex.Message}"); }
        _textDoc = null;
    }

    // ------------------------------------------------------------------ 释放

    public void Dispose()
    {
        if (_docId is not null && _service is not null)
        {
            try { _service.CloseDocument(_docId); }
            catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 关闭文档失败: {ex.Message}"); }
            _docId = null;
        }

        // 选区文本句柄（独立于渲染/搜索栈）；PdfiumTextService 无 IDisposable，仅释放句柄
        CloseTextDocument();
        _textService = null;
        _textLayerCache.Clear();
        _documentPath = null;

        if (_ownsService)
        {
            try { _service?.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 释放渲染服务失败: {ex.Message}"); }
        }

        _hasLayout = false;
    }
}
