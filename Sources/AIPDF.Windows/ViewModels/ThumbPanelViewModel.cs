// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 缩略图面板视图模型：后台队列批量渲染（消费 T1 RenderPageAsync，120px 固定宽），
// 可见区优先、单项结果经 UI 线程回填；纯逻辑可脱离 UI 测试（渲染器经接口注入）。
using System.Collections.ObjectModel;
using System.Windows.Media;
using AIPDF.Windows.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>缩略图渲染抽象：生产实现为 <see cref="PdfiumRenderService"/>，测试可桩替。</summary>
public interface IThumbnailRenderer
{
    /// <summary>后台渲染一页（返回冻结位图）。与 T1 RenderPageAsync 同语义。</summary>
    Task<ImageSource> RenderPageAsync(string docId, int pageIndex, double dpi, int zoomPct);

    /// <summary>页数；未打开返回 -1。</summary>
    int GetPageCount(string docId);

    /// <summary>页尺寸（点）；未打开/越界返回 null。</summary>
    PageSize? GetPageSize(string docId, int pageIndex);
}

/// <summary>把 T1 渲染服务适配为缩略图渲染接口（隐藏 RenderPageAsync 的缓存实现细节）。</summary>
public sealed class PdfiumThumbnailRendererAdapter(PdfiumRenderService renderer) : IThumbnailRenderer
{
    public async Task<ImageSource> RenderPageAsync(string docId, int pageIndex, double dpi, int zoomPct)
        => await renderer.RenderPageAsync(docId, pageIndex, dpi, zoomPct).ConfigureAwait(true);

    public int GetPageCount(string docId) => renderer.GetPageCount(docId);

    public PageSize? GetPageSize(string docId, int pageIndex) => renderer.GetPageSize(docId, pageIndex);
}

/// <summary>单个缩略图条目：页码 + 占位状态 + 渲染结果（渐进填充）。</summary>
public partial class ThumbItemViewModel : ObservableObject
{
    public int PageIndex { get; }

    /// <summary>显示名（1 基页码，与阅读器页码显示一致）。</summary>
    public string Label => (PageIndex + 1).ToString();

    /// <summary>条目高度（按页宽高比换算，宽固定 <see cref="ThumbPanelViewModel.ThumbWidth"/>）。</summary>
    public double Height { get; }

    /// <summary>渲染完成位图；未完成为 null（XAML 显示占位框）。</summary>
    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>是否已在渲染队列中/渲染完成（防重复入队）。</summary>
    internal bool IsQueuedOrLoaded { get; set; }

    public ThumbItemViewModel(int pageIndex, double height)
    {
        PageIndex = pageIndex;
        Height = height;
    }
}

/// <summary>
/// 缩略图面板视图模型：
/// - 条目集合一次性建立（500 页集合本身轻量），位图由后台工作线程渐进渲染；
/// - 可见区优先生成（EnsureRange 提升优先级），其余页按距可见锚点近先渲染；
/// - 结果在 UI 线程回填（位图本身已冻结，仅做属性赋值）。
/// </summary>
public partial class ThumbPanelViewModel : ObservableObject
{
    /// <summary>缩略图目标宽度（CSS 像素，PRD 3.1 FR-02）。</summary>
    public const int ThumbWidth = 120;

    /// <summary>后台渲染并发：PDFium 全局锁已串行化，1 个工作线程避免排队竞争。</summary>
    private const int WorkerCount = 1;

    private readonly IThumbnailRenderer _renderer;
    private readonly string _docId;
    private readonly object _queueLock = new();
    private readonly List<ThumbItemViewModel> _pending = [];
    private int _visibleAnchor; // 可见区中心页（近者优先）
    private int _workerGeneration; // 换文档作废旧队列

    public ObservableCollection<ThumbItemViewModel> Items { get; } = [];

    /// <summary>点击缩略图请求跳页（宿主接线 T3 ReaderView 滚动 API；T7 前为事件桩）。</summary>
    public event EventHandler<int>? NavigateRequested;

    [ObservableProperty]
    private int _renderedCount;

    public int TotalCount => Items.Count;

    public ThumbPanelViewModel(IThumbnailRenderer renderer, string docId, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);

        _renderer = renderer;
        _docId = docId;

        // 高度换算：宽固定 120px，按页宽高比缩放；页尺寸未知（异常防御）时取 A4 比例兜底
        const double fallbackRatio = 842.0 / 595.0;
        for (var i = 0; i < pageCount; i++)
        {
            var size = renderer.GetPageSize(docId, i);
            var ratio = size is { Width: > 0, Height: > 0 } ? size.Value.Height / size.Value.Width : fallbackRatio;
            ratio = Math.Clamp(ratio, 0.2, 5.0); // 病态长条页截断，防占位框失控
            Items.Add(new ThumbItemViewModel(i, Math.Round(ThumbWidth * ratio)));
        }

        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>点击跳页（XAML 绑定命令或代码后置直调）。</summary>
    public void RequestNavigate(int pageIndex) => NavigateRequested?.Invoke(this, pageIndex);

    /// <summary>
    /// 确保可见区间 [firstVisible, lastVisible] 的缩略图进入渲染队列（可见区优先），
    /// 并把可见锚点更新为区间中心，影响后续后台整册渲染顺序。
    /// </summary>
    public void EnsureRange(int firstVisible, int lastVisible)
    {
        firstVisible = Math.Max(0, firstVisible);
        lastVisible = Math.Min(Items.Count - 1, lastVisible);
        if (firstVisible > lastVisible)
        {
            return;
        }

        lock (_queueLock)
        {
            _visibleAnchor = (firstVisible + lastVisible) / 2;
            for (var i = firstVisible; i <= lastVisible; i++)
            {
                EnqueueLocked(Items[i]);
            }
        }
        EnsureWorkerStarted();
    }

    /// <summary>启动整册渐进渲染（滚动结束/面板打开时调用；已渲染项自动跳过）。</summary>
    public void FillAll() => EnsureWorkerStarted();

    /// <summary>换文档时清空队列并作废在途渲染（面板实例重建时调用，防结果回填错册）。</summary>
    public void ResetQueue()
    {
        lock (_queueLock)
        {
            _workerGeneration++;
            _pending.Clear();
        }
    }

    private void EnqueueLocked(ThumbItemViewModel item)
    {
        if (item.IsQueuedOrLoaded)
        {
            return;
        }
        item.IsQueuedOrLoaded = true;
        _pending.Add(item);
    }

    private void EnsureWorkerStarted()
    {
        // 幂等启动：代际号防多 worker 重复消费（单 worker 常态）
        if (Interlocked.CompareExchange(ref _workerStarted, 1, 0) == 0)
        {
            _ = Task.Run(WorkerLoopAsync);
        }
    }

    private int _workerStarted;

    /// <summary>后台渲染主循环：每次取距可见锚点最近的待渲染项，渲染后 UI 线程回填。</summary>
    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            ThumbItemViewModel? next;
            int generation;
            lock (_queueLock)
            {
                generation = _workerGeneration;
                if (_pending.Count == 0)
                {
                    _workerStarted = 0; // 队列空 → 休眠，下次 EnsureRange 重新拉起
                    return;
                }

                // 可见区优先：距锚点最近者先渲染（稳定排序，等距取小页码）
                var anchor = _visibleAnchor;
                var bestIndex = 0;
                var bestDistance = int.MaxValue;
                for (var i = 0; i < _pending.Count; i++)
                {
                    var distance = Math.Abs(_pending[i].PageIndex - anchor);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = i;
                    }
                }
                next = _pending[bestIndex];
                _pending.RemoveAt(bestIndex);
            }

            try
            {
                // 目标像素宽 120：px = widthPt × dpi / 72 → dpi = 120 × 72 / widthPt
                var dpi = ThumbWidth * 72.0 / PageWidthForDpi(next);
                var bitmap = await _renderer.RenderPageAsync(_docId, next.PageIndex, dpi, 100)
                    .ConfigureAwait(true);

                bool stale;
                lock (_queueLock)
                {
                    stale = generation != _workerGeneration;
                }
                if (!stale)
                {
                    next.Thumbnail = bitmap;
                    RenderedCount++;
                }
            }
            catch (Exception)
            {
                // 单页渲染失败：标记已处理但保持占位（滚动重试挂点：T9 性能通道统一处理）
                RenderedCount++;
            }
        }
    }

    /// <summary>按页宽把目标 120px 换算为渲染 DPI（px = widthPt * dpi / 72）。</summary>
    private double PageWidthForDpi(ThumbItemViewModel item)
    {
        var size = _renderer.GetPageSize(_docId, item.PageIndex);
        var widthPt = size is { Width: > 0 } ? size.Value.Width : 595.0;
        return Math.Max(1, widthPt);
    }
}
