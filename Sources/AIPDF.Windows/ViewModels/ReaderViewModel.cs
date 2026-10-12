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
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
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

    /// <summary>页在所属对页组内的水平偏移（DIP）；单页模式恒为 0（V3 T9 / M8）。</summary>
    [ObservableProperty]
    private double _offsetX;

    /// <summary>页在所属对页组内的垂直偏移（DIP）；当前与组顶对齐，恒为 0。</summary>
    [ObservableProperty]
    private double _offsetY;

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

    // ---- V4 T8 朗读高亮 ----

    /// <summary>
    /// 朗读位置高亮（页内 DIP 矩形）。由朗读进度驱动，随缩放重算——复用搜索高亮的换算函数
    /// （<see cref="PageLayoutCalculator.ToSearchHighlightDip"/>），不新增坐标换算层。
    /// </summary>
    public ObservableCollection<SearchHighlight> SpeechRects { get; } = [];

    /// <summary>本页是否持有朗读高亮矩形（缩放重算的触发条件）。</summary>
    internal bool HasSpeechRects => _speechSourceRects.Count > 0;

    private IReadOnlyList<PdfRect> _speechSourceRects = [];

    /// <summary>V4 T8：设置朗读高亮源矩形（PDF 点）并按当前显示比例换算。</summary>
    public void SetSpeechRects(IReadOnlyList<PdfRect> rects)
    {
        _speechSourceRects = rects ?? [];
        RebuildSpeechRects();
    }

    /// <summary>V4 T8：清空朗读高亮（停止朗读 / 关闭文档）。</summary>
    public void ClearSpeechRects()
    {
        _speechSourceRects = [];
        if (SpeechRects.Count > 0) SpeechRects.Clear();
    }

    /// <summary>V4 T8：按当前显示尺寸重算朗读高亮（缩放变更时调用）。</summary>
    internal void RebuildSpeechRects()
    {
        SpeechRects.Clear();
        if (_speechSourceRects.Count == 0 || WidthPt <= 0) return;
        foreach (var rect in _speechSourceRects)
        {
            SpeechRects.Add(PageLayoutCalculator.ToSearchHighlightDip(rect, WidthPt, DisplayWidth));
        }
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

    /// <summary>
    /// V3 T11（C3）绘制中的临时预览几何（页局部 DIP；手写/形状拖拽期）。null = 无预览。
    /// 与持久批注层分离：仅在拖拽期间由 ReaderView 写入，完成后清空。
    /// </summary>
    [ObservableProperty]
    private Geometry? _drawPreview;

    /// <summary>绘制预览的描边宽（DIP，随缩放换算）；无预览为 0。</summary>
    [ObservableProperty]
    private double _drawPreviewThickness;

    /// <summary>绘制预览包围盒原点 X（页局部 DIP；预览几何相对该原点）。</summary>
    [ObservableProperty]
    private double _drawPreviewX;

    /// <summary>绘制预览包围盒原点 Y（页局部 DIP）。</summary>
    [ObservableProperty]
    private double _drawPreviewY;

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
    internal bool HasAnnotationRecords => _annotationRecords.Count > 0 || _embeddedRecords.Count > 0;

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
        if (WidthPt <= 0) return;

        // V4 T9a：脏记录（本次会话新增/修改）与**已内嵌**记录（从文件读回）都要投影。
        // 二者分开存放：读回的批注不得进 _annotationRecords，否则保存时会被当成脏记录重复写回。
        foreach (var record in _annotationRecords)
        {
            Annotations.Add(AnnotationOverlay.FromRecord(record, WidthPt, DisplayWidth));
        }

        foreach (var record in _embeddedRecords)
        {
            Annotations.Add(AnnotationOverlay.FromRecord(record, WidthPt, DisplayWidth));
        }
    }

    // ---- V4 T9a：从文件读回的已内嵌批注 ----

    /// <summary>
    /// 已内嵌批注（打开文档时经引擎 <c>list_annotations</c> 读回）。**只读展示**：
    /// 不进脏记录集合（<see cref="_annotationRecords"/>），故保存时不会被重复写回；
    /// 供属性面板 / 回复线程 / 汇总导出读取。
    /// </summary>
    private readonly List<AnnotationRecord> _embeddedRecords = [];

    /// <summary>已内嵌批注（V4 T9a）。</summary>
    public IReadOnlyList<AnnotationRecord> EmbeddedRecords => _embeddedRecords;

    /// <summary>V4 T9a：设置本页从文件读回的已内嵌批注并重建叠加层（父 VM 按页分发）。</summary>
    public void SetEmbeddedRecords(IReadOnlyList<AnnotationRecord> records)
    {
        _embeddedRecords.Clear();
        if (records is not null)
        {
            _embeddedRecords.AddRange(records);
        }

        RebuildAnnotations();
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

    // ------------------------------------------------------------------ 替换标注（v1.1 T19 划词译文 sidecar）

    /// <summary>
    /// 页内替换标注叠加条目（DIP，相对页左上角）。独立于批注 <see cref="Annotations"/>：
    /// 只读消费 sidecar 译文，不进批注保存管线、绝不写回源 PDF。
    /// </summary>
    public ObservableCollection<TranslationOverlay> TranslationOverlays { get; } = [];

    /// <summary>替换标注真源（PDF 点，左上原点）。叠加层为其 DIP 投影；滚出视口只释放投影不丢记录。</summary>
    private readonly List<SelectionSidecarAnnotation> _translationAnnotations = [];

    /// <summary>本页是否存在替换标注记录（缩放重算触发条件）。</summary>
    internal bool HasTranslationAnnotations => _translationAnnotations.Count > 0;

    /// <summary>整体替换本页替换标注并重建投影（文档打开 / 切换标签时由宿主调用）。</summary>
    public void SetTranslationAnnotations(IReadOnlyList<SelectionSidecarAnnotation> annotations)
    {
        _translationAnnotations.Clear();
        if (annotations is not null) _translationAnnotations.AddRange(annotations);
        RebuildTranslationOverlays();
    }

    /// <summary>追加一条替换标注并重建投影（写入成功后由宿主调用）。</summary>
    public void AddTranslationAnnotation(SelectionSidecarAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        _translationAnnotations.Add(annotation);
        RebuildTranslationOverlays();
    }

    /// <summary>清空本页替换标注（关闭文档 / 清除会话）。</summary>
    internal void ClearTranslationAnnotations()
    {
        _translationAnnotations.Clear();
        if (TranslationOverlays.Count > 0) TranslationOverlays.Clear();
    }

    /// <summary>按当前 <see cref="DisplayWidth"/> 重算全部替换标注叠加（缩放变更/记录变化）。</summary>
    internal void RebuildTranslationOverlays()
    {
        TranslationOverlays.Clear();
        if (_translationAnnotations.Count == 0 || WidthPt <= 0) return;
        foreach (var annotation in _translationAnnotations)
        {
            TranslationOverlays.Add(TranslationOverlay.FromAnnotation(annotation, WidthPt, DisplayWidth));
        }
    }

    /// <summary>只释放 DIP 投影（页滚出视口）；记录保留，滚回视口经
    /// <see cref="EnsureTranslationOverlaysProjected"/> 重建，保证虚拟化不丢替换标注。</summary>
    internal void ReleaseTranslationOverlays()
    {
        if (TranslationOverlays.Count > 0) TranslationOverlays.Clear();
    }

    /// <summary>页重新进入视口时按需重建替换标注投影。</summary>
    internal void EnsureTranslationOverlaysProjected()
    {
        if (HasTranslationAnnotations && TranslationOverlays.Count == 0) RebuildTranslationOverlays();
    }

    partial void OnBitmapChanged(BitmapSource? value) => OnPropertyChanged(nameof(IsRendered));
}

/// <summary>
/// 对页组视图模型（V3 T9 / M8）：对开模式下一个阅读项（虚拟化单元）含 1–2 页；单页模式恒含 1 页。
/// 组显示宽/高由 <see cref="ReaderViewModel.RebuildLayout"/> 按组内页尺寸重算；组内各页经
/// <see cref="ReaderPageItemViewModel.OffsetX"/> 水平排布（左侧/右侧或单页）。
/// </summary>
public partial class ReaderSpreadGroupViewModel : ObservableObject
{
    public ReaderSpreadGroupViewModel(int index, IReadOnlyList<ReaderPageItemViewModel> pages)
    {
        Index = index;
        Pages = pages;
    }

    /// <summary>组索引（0 起）。</summary>
    public int Index { get; }

    /// <summary>组内页（1–2 页，阅读顺序左→右）。</summary>
    public IReadOnlyList<ReaderPageItemViewModel> Pages { get; }

    /// <summary>组显示宽（DIP）：组内页宽之和 + 组内页间距。</summary>
    [ObservableProperty]
    private double _displayWidth;

    /// <summary>组显示高（DIP）：组内页高最大值（ST-10 骨架占位不跳动）。</summary>
    [ObservableProperty]
    private double _displayHeight;
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

    /// <summary>
    /// V4 Option 3：<see cref="LoadDocument"/> 的输入路径（规范化）。锁定态下渲染/文本句柄与页项
    /// 均已释放，仅保留该路径用于「解锁」重试。路径本身不是解密内容，不是安全红线。
    /// </summary>
    private string? _sourcePath;

    /// <summary>
    /// 本次会话驻留的解密密码（T9）：仅内存，用于惰性文本层句柄重开（选区/搜索）。
    /// 只在打开成功后写入；绝不落盘 / 写日志 / 进异常消息（PRD 4.3.3）。
    /// 渲染/保存的逐请求密码由宿主的 <c>SessionCredentialStore</c> 提供。
    /// </summary>
    private string? _documentPassword;
    private readonly Dictionary<int, bool> _textLayerCache = new();

    private string? _docId;
    private List<PageSize> _pageSizes = [];
    private int[] _pageRotations = [];

    // ---- V3 T9（M8）group-index 布局层：渲染窗口/当前页/命中/选区一律以「对页组」为作用域，
    //      组坐标经 PageGroupMap + 组内偏移换算到页局部；单页模式映射恒等（group == page）。 ----
    private PageGroupMap _groups = PageGroupMap.Single(0);
    private double[] _groupOffsets = [];
    private double[] _groupHeights = [];
    private bool _spreadEnabled;

    /// <summary>阅读状态存储（V3 T9：对开开关随文件持久化）。宿主/测试注入；未注入时惰性自建（生产默认目录）。</summary>
    private ReadingStateStore? _readingStates;

    private double _scrollTop;
    private double _viewportHeight;
    private double _viewportWidth;
    private double _appliedZoom = 100;
    private bool _hasLayout;

    /// <summary>当前持有高亮的页索引（-1 = 无）；切换命中时只清上一页，避免遍历全部页。</summary>
    private int _searchHighlightPage = -1;

    /// <summary>构造。renderService 为空时首次打开文档自建并持有（随 Dispose 释放）；测试/宿主可注入。
    /// textService 同样为空时惰性自建（仅首次选区命中）；测试可注入独立实例。
    /// readingStates 为空时惰性自建（V3 T9 对开开关持久化，生产默认目录）；测试注入临时目录。</summary>
    public ReaderViewModel(PdfiumRenderService? renderService = null, int bufferPages = DefaultBufferPages,
        PdfiumTextService? textService = null, ReadingStateStore? readingStates = null)
    {
        _injectedService = renderService;
        _ownsService = renderService is null;
        _injectedTextService = textService;
        _readingStates = readingStates;
        BufferPages = Math.Max(0, bufferPages);
    }

    /// <summary>可视区两侧缓冲页数。</summary>
    public int BufferPages { get; }

    /// <summary>
    /// 渲染基准 DPI（V3 T5）：由宿主在打开文档前用设置 <c>DefaultRenderDpi</c> 注入；
    /// 默认 <see cref="PageLayoutCalculator.BaseDpi"/>（96，等价既有行为）。仅覆盖渲染基准，
    /// 不改变「zoom ≥150% → HighDpi=150」阈值阶梯（见 <see cref="PageLayoutCalculator.DpiForZoom"/>），
    /// 也不参与打印（打印 DPI 由 <c>PrintService</c> 读 <c>PrintDpi</c>）。
    /// </summary>
    public double RenderBaseDpi { get; set; } = PageLayoutCalculator.BaseDpi;

    /// <summary>待应用的初始适配档（V3 T5）：设置默认缩放为 fit-width/fit-page 时排队，视口测量后套用一次。</summary>
    private bool _pendingFitWidth;
    private bool _pendingFitPage;

    /// <summary>
    /// 排队初始适配模式（设置 DefaultZoom 为适配档时由宿主注入）。打开瞬间视口宽高常为 0，
    /// <see cref="FitWidthCommand"/>/<see cref="FitPageCommand"/> 会空转；此处于首个有效视口到达时补套用一次。
    /// 数值档不经此处（宿主直接设置 <see cref="ZoomPercent"/>）。
    /// </summary>
    public void QueueInitialFit(bool fitWidth, bool fitPage)
    {
        _pendingFitWidth = fitWidth;
        _pendingFitPage = fitPage;
        TryApplyPendingFit();
    }

    /// <summary>页面集合（虚拟化 ItemsControl 的 ItemsSource；仅持有尺寸元数据，位图按需）。</summary>
    public ObservableCollection<ReaderPageItemViewModel> Pages { get; } = [];

    /// <summary>对页组集合（虚拟化 ItemsControl 的实际 ItemsSource）：对开模式 1–2 页/组；单页模式 group=page。</summary>
    public ObservableCollection<ReaderSpreadGroupViewModel> Groups { get; } = [];

    /// <summary>对开分组：第 1 页是否单独成组（封面单列）。</summary>
    public const bool CoverPageAlone = true;

    /// <summary>当前 page↔group 映射（M8）：单页恒等；对开按封面单列分组。</summary>
    public PageGroupMap GroupMap => _groups;

    /// <summary>
    /// 双页对开开关（V3 T9）：setter 即切换布局（重建组 + 重算偏移 + 保持当前页），并持久化到阅读状态。
    /// 单页（false，默认）/双页（true）切换不改变 PageGap / 缩放阶梯 / ZoomAnchorScroll 语义。
    /// </summary>
    public bool SpreadEnabled
    {
        get => _spreadEnabled;
        set => SetSpread(value, persist: true);
    }

    /// <summary>状态栏切换按钮文案：显示当前模式（单页/双页）。</summary>
    public string SpreadToggleText => _spreadEnabled ? ReaderStrings.SpreadTwoPageLabel : ReaderStrings.SpreadSinglePageLabel;

    /// <summary>切换对开（供状态栏按钮命令与测试调用）。</summary>
    [RelayCommand]
    private void ToggleSpread() => SetSpread(!_spreadEnabled, persist: true);

    // ---- V4 T8 朗读状态（供朗读条绑定；执行体在宿主 MainWindow）----

    /// <summary>朗读是否可用（无可用语音时为 false，宿主据此禁用入口并提示，不得静默无声）。</summary>
    [ObservableProperty]
    private bool _speechAvailable;

    /// <summary>是否正在朗读（含暂停态）。</summary>
    [ObservableProperty]
    private bool _speechActive;

    /// <summary>是否处于暂停。</summary>
    [ObservableProperty]
    private bool _speechPaused;

    /// <summary>朗读按钮文案（朗读 / 暂停 / 继续）。</summary>
    public string SpeechToggleLabel
        => !SpeechActive
            ? ReaderStrings.SpeechPlay
            : SpeechPaused
                ? ReaderStrings.SpeechResume
                : ReaderStrings.SpeechPause;

    /// <summary>停止按钮可用性（仅在朗读/暂停时可用）。</summary>
    public bool SpeechStopEnabled => SpeechActive;

    partial void OnSpeechActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(SpeechToggleLabel));
        OnPropertyChanged(nameof(SpeechStopEnabled));
    }

    partial void OnSpeechPausedChanged(bool value) => OnPropertyChanged(nameof(SpeechToggleLabel));

    /// <summary>
    /// V4 T7：演示模式临时切换双页对开——**不落盘**（演示不应改写用户的阅读偏好，
    /// 退出演示后由宿主还原原值）。
    /// </summary>
    public void SetSpreadForPresentation(bool enabled) => SetSpread(enabled, persist: false);

    private void SetSpread(bool enabled, bool persist)
    {
        if (enabled == _spreadEnabled)
        {
            if (persist) PersistSpread(enabled);
            return;
        }

        var currentPage = CurrentPageNumber > 0 ? CurrentPageNumber : 1;
        SetProperty(ref _spreadEnabled, enabled, nameof(SpreadEnabled));
        OnPropertyChanged(nameof(SpreadToggleText));

        if (_hasLayout)
        {
            BuildGroups();
            RebuildLayout(ZoomPercent);
            var group = _groups.PageToGroup(currentPage - 1);
            var offset = group >= 0 ? Math.Max(0, _groupOffsets[group]) : 0;
            UpdateViewportCore(offset);
            ScrollRequested?.Invoke(this, new ReaderScrollRequestEventArgs(offset));
        }

        if (persist) PersistSpread(enabled);
    }

    /// <summary>恢复/初始化对开开关：只置位与通知，不重建布局、不写盘（调用方随后 BuildGroups/RebuildLayout）。</summary>
    private void SetSpreadBacking(bool enabled)
    {
        if (_spreadEnabled == enabled) return;
        _spreadEnabled = enabled;
        OnPropertyChanged(nameof(SpreadEnabled));
        OnPropertyChanged(nameof(SpreadToggleText));
    }

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

    /// <summary>
    /// V4 Option 3：当前文档是否为加密文档（打开时需密码）。由「无密码打开触发 FPDF_ERR_PASSWORD」
    /// 或「带密码成功打开」判定；供锁定 UI 与打印门禁（<c>PrintEntry</c>）使用。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked))]
    [NotifyPropertyChangedFor(nameof(LockStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowRelockAction))]
    private bool _isEncrypted;

    /// <summary>
    /// V4 Option 3：加密文档当前是否处于锁定态。锁定态下：未打开任何渲染/文本句柄、不含页项与位图、
    /// 无搜索/文本缓存；UI 显示锁定遮罩与「解锁」入口；打印门禁禁用。锁定是「本会话不可查看/不可打印，
    /// 且解密内容已从内存释放」的明确边界（安全红线，见 <see cref="Lock"/>）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked))]
    [NotifyPropertyChangedFor(nameof(LockStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowRelockAction))]
    private bool _isLocked;

    /// <summary>加密文档是否已解锁（加密且非锁定）。</summary>
    public bool IsUnlocked => IsEncrypted && !IsLocked;

    /// <summary>
    /// V1.1 T15：当前文档是否为「权限加密（受限）」——可打开（空用户密码）但 owner 权限禁止抽取/复制等。
    /// 由 PDFium 权限位判定（<see cref="PdfiumRenderService.IsPermissionRestricted"/>），与引擎译后
    /// <c>source_protection.restricted</c> 同源。整篇翻译开始前据此弹「确认你有权处理该文档」。
    /// 未打开/锁定态为 false。
    /// </summary>
    [ObservableProperty]
    private bool _isSourceRestricted;

    /// <summary>状态栏锁状态文案（仅当 <see cref="IsEncrypted"/> 时有意义）。</summary>
    public string LockStatusText => IsLocked ? ReaderStrings.LockedStatusLabel : ReaderStrings.UnlockedStatusLabel;

    /// <summary>是否显示「锁定」入口（仅已解锁的加密文档提供重新锁定）。</summary>
    public bool ShowRelockAction => IsUnlocked;

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
    /// 最近一次 <see cref="LoadDocument"/> 是否因「需要密码」失败（PDFium <c>FPDF_ERR_PASSWORD</c>）。
    /// V4 Option 3 起不再驱动自动弹框（宿主已改为锁定标签 + 手动「解锁」）；保留为诊断/兼容信号。
    /// 非密码类失败（格式/损坏）保持 false，仍走既有错误条。
    /// </summary>
    public bool LoadRequiresPassword { get; private set; }

    /// <summary>
    /// 打开文档并构建虚拟化布局（页尺寸一次取自 T1 缓存）。失败写入 ErrorMessage（ST-06，模态对话框由宿主 T5 负责）。
    /// 密码只在打开成功时驻留本 VM 内存（T9），供惰性文本层重开；不落盘、不写日志。
    /// <para>
    /// V4 Option 3：无可用密码打开加密文档时（<c>FPDF_ERR_PASSWORD</c>）不打开、不解密、不渲染——
    /// 置 <see cref="IsEncrypted"/>=<see cref="IsLocked"/>=true，标签以锁定态呈现；解锁走
    /// <see cref="Unlock"/>（宿主弹出既有【ST-04】密码框）。锁定不是错误，不写 ErrorMessage。
    /// </para>
    /// </summary>
    public void LoadDocument(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ErrorMessage = null;
        LoadRequiresPassword = false;
        IsSourceRestricted = false;
        _sourcePath = Path.GetFullPath(path);

        try
        {
            var svc = _injectedService ?? _service;
            if (svc is null) svc = CreateService();
            _service = svc;
            var docId = svc.OpenDocument(path, password);
            _documentPassword = password; // 打开成功才驻留；失败密码不保留

            // V4 Option 3：带密码成功打开 = 加密文档；解除锁定（重开/解锁路径）。
            if (!string.IsNullOrEmpty(password)) IsEncrypted = true;
            IsLocked = false;

            // V1.1 T15：权限加密（可打开但禁止抽取/复制）在打开成功后一次判定，供整篇翻译前置确认。
            IsSourceRestricted = svc.IsPermissionRestricted(docId);

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

            // V3 T9：新打开文档按 reading-state 恢复对开开关；无状态默认单页（单页恒等映射）。
            SetSpreadBacking(ReadPersistedSpread());
            BuildGroups();
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
            Groups.Clear();
            _groups = PageGroupMap.Single(0);
            _groupOffsets = [];
            _groupHeights = [];
            SetSpreadBacking(false);
            _documentPassword = null;

            if (ex.ErrorCode == PdfiumRenderService.PdfiumErrorPassword)
            {
                // V4 Option 3：加密文档无可用会话密码 → 保持锁定（未打开任何句柄、未解密、未渲染）。
                // 失败打开不会保留 document 句柄（T1 失败路径已释放 pin），故无解密内容残留。
                IsEncrypted = true;
                IsLocked = true;
                LoadRequiresPassword = true;
                ErrorMessage = null; // 锁定不是错误：由锁定遮罩呈现，密码行内提示归解锁对话框
            }
            else
            {
                ErrorMessage = ReaderStrings.LoadErrorPrefix + ex.Message;
            }
        }
    }

    private static PdfiumRenderService CreateService() => new();

    // ------------------------------------------------------------------ V4 Option 3：锁定 / 解锁状态机

    /// <summary>
    /// 用密码解锁当前锁定文档。仅当「加密且锁定」时有效；成功则打开文档并解除锁定
    /// （<see cref="IsEncrypted"/> 保持 true），失败保持锁定并返回 false。密码仅驻留本 VM 与宿主
    /// <c>SessionCredentialStore</c> 内存，不落盘 / 不写日志 / 不进异常消息。
    /// </summary>
    public bool Unlock(string password)
    {
        if (!IsEncrypted || !IsLocked || string.IsNullOrEmpty(password)) return false;
        var path = _sourcePath ?? _documentPath;
        if (string.IsNullOrWhiteSpace(path)) return false;

        LoadDocument(path, password);
        return !IsLocked && HasDocument;
    }

    /// <summary>
    /// 重新锁定（V4 Option 3，安全关键）：释放全部解密内容并回到锁定态。
    /// 宿主须同时清除 <c>SessionCredentialStore</c> 中该路径的密码（本 VM 不持有该存储）。
    /// 保证：锁定后本 VM 不再持有任何文档句柄 / 页项 / 位图 / 文本或搜索缓存。
    /// </summary>
    public void Lock()
    {
        if (!IsEncrypted || IsLocked) return;
        ReleaseDecryptedContent();
        IsLocked = true;
    }

    /// <summary>
    /// 释放一切可解密内容（安全红线）：关闭渲染与文本句柄、清在途渲染、清页项与位图、
    /// 清搜索高亮 / 批注投影 / 选区 / 绘制预览、清文本层与页尺寸缓存、清本 VM 驻留密码。
    /// 仅保留 <see cref="_sourcePath"/> 供再次解锁（路径非解密内容）。
    /// </summary>
    private void ReleaseDecryptedContent()
    {
        // ① 关闭渲染文档句柄（T1 CloseDocument 会 drain 在途原生调用；此后无法再渲染/提取）。
        if (_docId is not null && _service is not null)
        {
            try { _service.CloseDocument(_docId); }
            catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 锁定关闭文档失败: {ex.Message}"); }
        }
        _docId = null;

        // ② 关闭独立文本层句柄（选区/搜索文本来源）并清缓存。
        CloseTextDocument();
        _textLayerCache.Clear();
        _inFlight.Clear();

        // ③ 释放每一页持有的位图与各类叠加投影；页 VM 本身也不再保留（清空 Pages）。
        foreach (var page in Pages)
        {
            page.Bitmap = null;
            page.RenderedKey = null;
            page.PendingKey = null;
            page.IsRendering = false;
            page.ClearSearchRects();
            page.ReleaseAnnotationOverlays();
            page.SelectionRects.Clear();
            page.DrawPreview = null;
            page.DrawPreviewThickness = 0;
        }
        Pages.Clear();
        Groups.Clear();

        // ④ 清布局/尺寸/高亮等一切可推导出文档内容的缓存。
        _groups = PageGroupMap.Single(0);
        _groupOffsets = [];
        _groupHeights = [];
        _pageSizes = [];
        _pageRotations = [];
        _searchHighlightPage = -1;
        _hasLayout = false;
        _scrollTop = 0;
        PageCount = 0;
        CurrentPageNumber = 0;
        HasDocument = false;
        ErrorMessage = null;

        // ⑤ 清除句柄路径与本 VM 驻留密码（真实密码由宿主 SessionCredentialStore 清除）。
        _documentPath = null;
        _documentPassword = null;
    }

    // ------------------------------------------------------------------ 对开开关持久化（V3 T9：reading-state，按文件键 size/mtime）

    /// <summary>读取本文件持久化的对开开关（只读，不刷新 lastAccess——那是宿主的职责）；无状态默认 false。</summary>
    private bool ReadPersistedSpread()
    {
        if (_documentPath is null) return false;
        try
        {
            var store = _readingStates ??= new ReadingStateStore();
            var stamp = ReadingStateKey.TryCapture(_documentPath);
            return stamp is { } s && store.Load(s.Key)?.SpreadEnabled == true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReaderViewModel] 读取对开状态失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>把对开开关写回本文件 reading-state（读改写，保留页码/缩放/书签等既有字段）。</summary>
    private void PersistSpread(bool enabled)
    {
        if (_documentPath is null || !_hasLayout) return;
        try
        {
            var store = _readingStates ??= new ReadingStateStore();
            var stamp = ReadingStateKey.TryCapture(_documentPath);
            if (stamp is not { } s) return;

            var state = store.Load(s.Key) ?? new ReadingState { Path = _documentPath };
            state.Path = _documentPath;
            state.SpreadEnabled = enabled;
            state.LastAccessUtc = DateTime.UtcNow;
            store.Save(s.Key, state);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReaderViewModel] 保存对开状态失败: {ex.Message}");
        }
    }

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

        // T9：就地保存后重载用驻留密码重开句柄；显式传入优先（保存链路带回），否则用本 VM 驻留值。
        var effectivePassword = password ?? _documentPassword;
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
            _docId = _service.OpenDocument(path, effectivePassword);
            _documentPassword = effectivePassword;
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
            BuildGroups();
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
        TryApplyPendingFit(); // V3 T5：初始适配档等首个有效视口到达后套用一次
        UpdateViewportCore(scrollTop);
    }

    private void UpdateViewportCore(double scrollTop)
    {
        _scrollTop = scrollTop;
        if (!_hasLayout) return;

        // 对页组作用域渲染窗口（M8）：仅可视 ± 缓冲「组」内的页持位图；单页模式 group == page，行为不变。
        var (firstGroup, lastGroup) = PageLayoutCalculator.VisibleGroupRange(
            _groupOffsets, _groupHeights, scrollTop, _viewportHeight, BufferPages);
        var hasWindow = lastGroup >= firstGroup;
        var firstPage = hasWindow ? _groups.GroupToPages(firstGroup).First : 0;
        var lastPage = hasWindow ? _groups.GroupToPages(lastGroup).Last : -1;

        if (hasWindow)
        {
            for (var i = firstPage; i <= lastPage; i++) StartRender(Pages[i]);
        }

        // 视口外页释放：丢本侧位图引用 + 取消在途标记（缺口②适配，LRU 挂点见 PageReleased）
        if (lastPage - firstPage + 1 < Pages.Count)
        {
            foreach (var page in Pages)
            {
                if (page.Index >= firstPage && page.Index <= lastPage) continue;
                ReleasePage(page);
            }
        }

        // 当前页 = 当前对页组首屏页（M8：经 group→page 映射；并列取靠前组）。
        var currentGroup = PageLayoutCalculator.CurrentGroupByMaxOverlap(_groupOffsets, _groupHeights, scrollTop, _viewportHeight);
        if (currentGroup >= 0)
        {
            var currentPage = _groups.GroupToPages(currentGroup).First;
            if (currentPage + 1 != CurrentPageNumber) CurrentPageNumber = currentPage + 1;
        }
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
        page.ReleaseTranslationOverlays();

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
        page.EnsureTranslationOverlaysProjected();

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
        => ((int)PageLayoutCalculator.DpiForZoom(ZoomPercent, RenderBaseDpi), (int)Math.Round(ZoomPercent));

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
        var oldOffsets = _groupOffsets;
        var oldHeights = _groupHeights;
        RebuildLayout(value);

        var target = PageLayoutCalculator.ZoomAnchorScroll(oldOffsets, oldHeights, _scrollTop, _viewportHeight, _groupOffsets, _groupHeights);
        UpdateViewportCore(target); // 立即按新布局发起渲染（不等视图回传）
        ScrollRequested?.Invoke(this, new ReaderScrollRequestEventArgs(target));
    }

    private void RebuildLayout(double zoomPercent)
    {
        _appliedZoom = zoomPercent;
        _groupOffsets = PageLayoutCalculator.ComputeGroupOffsets(_pageSizes, zoomPercent, _groups);
        _groupHeights = PageLayoutCalculator.ComputeGroupHeights(_pageSizes, zoomPercent, _groups);
        var scale = zoomPercent / 100.0 * PageLayoutCalculator.DpiScale;
        for (var i = 0; i < Pages.Count && i < _pageSizes.Count; i++)
        {
            Pages[i].DisplayWidth = _pageSizes[i].Width * scale;
            Pages[i].DisplayHeight = _pageSizes[i].Height * scale;
            // M8：页在组内水平偏移（单页模式恒 0；对开右侧页 = 左页显示宽 + 页间距）。
            Pages[i].OffsetX = PageLayoutCalculator.PageXWithinGroup(i, _pageSizes, zoomPercent, _groups);
            Pages[i].OffsetY = 0;
            // 缩放变更：搜索高亮与批注叠加层按新显示尺寸重算（源矩形为 pt，不随缩放变化）
            if (Pages[i].HasSearchRects) Pages[i].RebuildHighlights();
            if (Pages[i].HasSpeechRects) Pages[i].RebuildSpeechRects();
            if (Pages[i].HasAnnotationRecords) Pages[i].RebuildAnnotations();
            if (Pages[i].HasTranslationAnnotations) Pages[i].RebuildTranslationOverlays();
        }

        if (_groups.GroupCount > 0)
        {
            var widths = PageLayoutCalculator.ComputeGroupWidths(_pageSizes, zoomPercent, _groups);
            for (var g = 0; g < Groups.Count && g < widths.Length; g++)
            {
                Groups[g].DisplayWidth = widths[g];
                Groups[g].DisplayHeight = _groupHeights[g];
            }
        }
    }

    /// <summary>
    /// 整体替换本阅读实例的替换标注叠加（文档打开 / 切换标签时宿主用 sidecar 会话内容填充）。
    /// 只填充视图叠加，不改动 source PDF，也不进入批注保存管线。
    /// </summary>
    public void SetTranslationAnnotations(IReadOnlyList<SelectionSidecarAnnotation> annotations)
    {
        var byPage = new Dictionary<int, List<SelectionSidecarAnnotation>>();
        if (annotations is not null)
        {
            foreach (var annotation in annotations)
            {
                if (!byPage.TryGetValue(annotation.PageIndex, out var list))
                {
                    list = [];
                    byPage[annotation.PageIndex] = list;
                }

                list.Add(annotation);
            }
        }

        for (var i = 0; i < Pages.Count; i++)
        {
            Pages[i].SetTranslationAnnotations(
                byPage.TryGetValue(i, out var list) ? list : Array.Empty<SelectionSidecarAnnotation>());
        }
    }

    /// <summary>清空全部替换标注叠加（会话结束 / 文档关闭）。</summary>
    public void ClearTranslationAnnotations()
    {
        foreach (var page in Pages)
        {
            page.ClearTranslationAnnotations();
        }
    }

    /// <summary>
    /// 按当前对开开关构建对页组（文档打开 / 切换对开时调用）：单页模式 group == page（恒等映射）；
    /// 对开模式按 <see cref="CoverPageAlone"/> 分组（第 1 页单列，其余每两页一组）。
    /// </summary>
    private void BuildGroups()
    {
        _groups = _pageSizes.Count > 0 && _spreadEnabled
            ? PageGroupMap.Spread(_pageSizes.Count, CoverPageAlone)
            : PageGroupMap.Single(_pageSizes.Count);
        Groups.Clear();
        for (var g = 0; g < _groups.GroupCount; g++)
        {
            var (first, last) = _groups.GroupToPages(g);
            var pages = new List<ReaderPageItemViewModel>(Math.Max(0, last - first + 1));
            for (var p = first; p <= last && p < Pages.Count; p++) pages.Add(Pages[p]);
            Groups.Add(new ReaderSpreadGroupViewModel(g, pages));
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
        // M8：对开按「对页组宽」适配——扣减组内页间距；单页模式与既有语义一致（extra = 0）。
        var maxGroupWidthPt = PageLayoutCalculator.MaxGroupContentWidthPt(_pageSizes, _groups);
        var extra = _spreadEnabled && PageLayoutCalculator.HasMultiPageGroup(_groups) ? PageLayoutCalculator.PageGap : 0;
        IsFitPageActive = false;
        IsFitWidthActive = true;
        ZoomPercent = PageLayoutCalculator.FitWidthZoomPercent(_viewportWidth, maxGroupWidthPt, extra);
    }

    /// <summary>适配页面（FR-06 动态档）：宽高取更紧约束；对开按对页组宽高（M8）。</summary>
    [RelayCommand]
    private void FitPage()
    {
        if (!_hasLayout || _viewportWidth <= 0 || _viewportHeight <= 0) return;
        var maxGroupWidthPt = PageLayoutCalculator.MaxGroupContentWidthPt(_pageSizes, _groups);
        var maxGroupHeightPt = PageLayoutCalculator.MaxGroupContentHeightPt(_pageSizes, _groups);
        var extra = _spreadEnabled && PageLayoutCalculator.HasMultiPageGroup(_groups) ? PageLayoutCalculator.PageGap : 0;
        IsFitWidthActive = false;
        IsFitPageActive = true;
        ZoomPercent = PageLayoutCalculator.FitPageZoomPercent(_viewportWidth, _viewportHeight, maxGroupWidthPt, maxGroupHeightPt, extra, 0);
    }

    private void LeaveFitMode()
    {
        IsFitWidthActive = false;
        IsFitPageActive = false;
    }

    /// <summary>
    /// 套用排队的初始适配档（若存在）：在首个有效视口到达后执行一次，随即清除排队标记（只影响本次打开的初始态）。
    /// 适配宽度仅需视口宽；适配页面需宽高。套用后走既有 <see cref="FitWidthCommand"/>/<see cref="FitPageCommand"/> 语义。
    /// </summary>
    private void TryApplyPendingFit()
    {
        if (!_pendingFitWidth && !_pendingFitPage) return;
        if (!_hasLayout || _viewportWidth <= 0) return;

        var fitWidth = _pendingFitWidth;
        var fitPage = _pendingFitPage;
        _pendingFitWidth = false;
        _pendingFitPage = false;

        if (fitWidth)
        {
            FitWidth();
        }
        else if (fitPage && _viewportHeight > 0)
        {
            FitPage();
        }
        else if (fitPage)
        {
            _pendingFitPage = true; // 视口高尚未测量：保留排队，下一拍再试
        }
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
        // M8：经 page→group 映射取目标组顶部偏移（单页恒等）。
        var group = _groups.PageToGroup(page - 1);
        if (group < 0) return;
        var offset = Math.Max(0, _groupOffsets[group]);
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

    /// <summary>
    /// 新增多行高亮批注（V4 T3：逐行选区）。单框会把行间空隙也涂满，故逐行传入。
    /// </summary>
    public AnnotationRecord? AddHighlight(int pageIndex, IReadOnlyList<PdfRect> rects, string? color = null)
        => AddTextMarkup(AnnotationKind.Highlight, pageIndex, rects, color);

    /// <summary>
    /// 新增文本标记批注（下划线/删除线；V4 T3 浮菜单消费）。
    /// <paramref name="rects"/> 必须是**逐行**选区矩形：把多行压成单个包围盒会让渲染退化成
    /// 跨行实心带（引擎侧实测见 .omo/evidence/v4-spike-underline.md）。
    /// 空选区/旋转页/越界返回 null（不创建）。
    /// </summary>
    public AnnotationRecord? AddTextMarkup(
        AnnotationKind kind, int pageIndex, IReadOnlyList<PdfRect> rects, string? color = null)
    {
        if (rects is null || rects.Count == 0)
        {
            return null;
        }

        var normalized = new PdfRect[rects.Count];
        for (var index = 0; index < rects.Count; index++)
        {
            normalized[index] = Normalize(rects[index]);
        }

        return AddAnnotation(AnnotationRecord.CreateTextMarkup(kind, pageIndex, normalized, color));
    }

    /// <summary>新增笔记批注（T2 浮菜单「笔记」消费）。旋转页/越界返回 null（不创建）。</summary>
    public AnnotationRecord? AddNote(int pageIndex, PdfRect rect, string note, string? color = null)
        => AddAnnotation(AnnotationRecord.CreateNote(pageIndex, Normalize(rect), note, color));

    /// <summary>新增手写批注（V3 T11 工具栏消费）。旋转页/越界返回 null（不创建）。</summary>
    public AnnotationRecord? AddInk(
        int pageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes,
        string? color = null, double lineWidth = AnnotationRecord.DefaultLineWidth)
        => AddAnnotation(AnnotationRecord.CreateInk(pageIndex, strokes, color, lineWidth));

    /// <summary>
    /// 新增图章批注（V4 T4 绘制工具栏「图章」消费）。预置图章给 token，图片图章给 image。
    /// 旋转页/越界/位置无效返回 null（不创建）。
    /// </summary>
    public AnnotationRecord? AddStamp(
        int pageIndex, PdfRect rect, string? token = null, byte[]? image = null, string? color = null)
    {
        var normalized = Normalize(rect);
        if (normalized.Width < 1 || normalized.Height < 1)
        {
            return null;
        }

        return AddAnnotation(AnnotationRecord.CreateStamp(pageIndex, normalized, token, image, color));
    }

    /// <summary>新增矩形/圆形状批注（V3 T11 工具栏消费）。旋转页/越界返回 null（不创建）。</summary>
    public AnnotationRecord? AddShape(
        int pageIndex, ShapeKind shape, PdfRect rect,
        string? color = null, double lineWidth = AnnotationRecord.DefaultLineWidth)
        => AddAnnotation(AnnotationRecord.CreateShape(pageIndex, shape, Normalize(rect), color, lineWidth));

    /// <summary>新增线/箭头形状批注（V3 T11）：方向 = 起点→终点。旋转页/越界返回 null（不创建）。</summary>
    public AnnotationRecord? AddShape(
        int pageIndex, ShapeKind shape, PdfPoint start, PdfPoint end,
        string? color = null, double lineWidth = AnnotationRecord.DefaultLineWidth)
        => AddAnnotation(AnnotationRecord.CreateShape(pageIndex, shape, start, end, color, lineWidth));

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

    // ---- V4 T9a：从文件读回的已内嵌批注（只读展示）----

    /// <summary>
    /// 已内嵌批注（打开文档时经引擎 <c>list_annotations</c> 读回）。**只读**：
    /// 不进 <see cref="GetAllRecords"/> 的脏记录集合，故保存时不会被重复写回；
    /// 供属性面板 / 回复线程 / 汇总导出读取。
    /// </summary>
    public IReadOnlyList<AnnotationRecord> EmbeddedRecords
    {
        get
        {
            var list = new List<AnnotationRecord>();
            foreach (var page in Pages)
            {
                if (page.EmbeddedRecords.Count > 0) list.AddRange(page.EmbeddedRecords);
            }
            return list;
        }
    }

    /// <summary>V4 T9a：把读回的批注按页分发到各页（只读展示；不脏化文档）。</summary>
    public void SetEmbeddedRecords(IReadOnlyList<AnnotationRecord> records)
    {
        var byPage = new Dictionary<int, List<AnnotationRecord>>();
        foreach (var record in records ?? [])
        {
            if (!byPage.TryGetValue(record.PageIndex, out var bucket))
            {
                bucket = [];
                byPage[record.PageIndex] = bucket;
            }

            bucket.Add(record);
        }

        for (var index = 0; index < Pages.Count; index++)
        {
            Pages[index].SetEmbeddedRecords(byPage.TryGetValue(index, out var bucket) ? bucket : []);
        }
    }

    /// <summary>V4 T9a：清空已内嵌批注（关闭文档 / 重载）。</summary>
    public void ClearEmbeddedRecords()
    {
        foreach (var page in Pages)
        {
            page.SetEmbeddedRecords([]);
        }
    }

    /// <summary>
    /// V2 T10 FR-22：文档内已内嵌注释总数（已保存批注，不含 Widget 表单字段）。
    /// 与叠加层未保存记录数相加即「文档存在批注（含未保存）」的判定来源；未打开返回 0。
    /// </summary>
    public int CountFileAnnotations()
        => _service is not null && _docId is not null ? _service.CountAnnotations(_docId) : 0;

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
        // T9：加密文档的文本层句柄重开需同一驻留密码（每个文档句柄独立，渲染/文本各持一份）。
        _textDoc = svc.OpenDocument(_documentPath, _documentPassword);
        return _textDoc;
    }

    private void CloseTextDocument()
    {
        try { _textDoc?.Dispose(); }
        catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 关闭文本句柄失败: {ex.Message}"); }
        _textDoc = null;
    }

    // ------------------------------------------------------------------ group 坐标 ↔ 页局部坐标（V3 T9 / M8）

    /// <summary>
    /// 组内坐标 → 页索引 + 页局部坐标（纯映射，无副作用）：视图的鼠标/命中/选区在「组元素」上取点后，
    /// 一律经此换算到页局部（单页模式恒等）。点落在组内页间距/页外返回 false（但 out 仍给出最近页）。
    /// </summary>
    public bool TryMapGroupPoint(int groupIndex, double groupX, double groupY,
        out int pageIndex, out double localX, out double localY)
    {
        pageIndex = -1;
        localX = 0;
        localY = 0;
        if (groupIndex < 0 || groupIndex >= Groups.Count) return false;

        var pages = Groups[groupIndex].Pages;
        if (pages.Count == 0) return false;

        var widths = new double[pages.Count];
        var heights = new double[pages.Count];
        for (var i = 0; i < pages.Count; i++)
        {
            widths[i] = pages[i].DisplayWidth;
            heights[i] = pages[i].DisplayHeight;
        }

        var hit = PageLayoutCalculator.HitPageInGroup(widths, heights, groupX, groupY);
        if (hit.PageOffset < 0 || hit.PageOffset >= pages.Count) return false;

        pageIndex = pages[hit.PageOffset].Index;
        localX = hit.LocalX;
        localY = hit.LocalY;
        return hit.Inside;
    }

    /// <summary>
    /// 批注命中（M8）：在组坐标上做命中测试，内部经 <see cref="TryMapGroupPoint"/> 换算到页局部后
    /// 走 <see cref="AnnotationHitTester"/>（页内 DIP）。命中返回批注 Id 并 out 页索引；未命中返回 null。
    /// </summary>
    public string? HitTestAnnotationAt(int groupIndex, double groupX, double groupY, out int pageIndex)
    {
        pageIndex = -1;
        if (!TryMapGroupPoint(groupIndex, groupX, groupY, out pageIndex, out var localX, out var localY)) return null;
        return AnnotationHitTester.HitTest(Pages[pageIndex].Annotations, localX, localY);
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
        _sourcePath = null;
        _documentPassword = null; // T9：随文档关闭清除驻留密码（进程退出即整体清除）

        if (_ownsService)
        {
            try { _service?.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[ReaderViewModel] 释放渲染服务失败: {ex.Message}"); }
        }

        _hasLayout = false;
    }
}
