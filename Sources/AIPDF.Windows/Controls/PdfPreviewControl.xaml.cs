// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AIPDF.Windows.Controls;

/// <summary>框选完成事件参数。</summary>
public sealed class PdfRegionSelectedEventArgs : EventArgs
{
    /// <summary>页码（0 基，与引擎一致）。</summary>
    public required int PageIndex { get; init; }

    /// <summary>选择区域：可见页左上原点、pt 单位（backend/engine.py area() 约定），已裁剪到页内。</summary>
    public required Rect RegionPt { get; init; }
}

/// <summary>
/// PDF 预览控件：pdfium 渲染当前页为位图，Ctrl+滚轮缩放（25%–400%），
/// PageUp/PageDown 翻页，框选 overlay 输出 PDF 坐标区域。
/// 每次只渲染当前页，不缓存多页位图。
/// </summary>
public partial class PdfPreviewControl : UserControl
{
    private static readonly double[] ZoomPresets = [0.25, 0.33, 0.5, 0.67, 1.0, 1.5, 2.0, 3.0, 4.0];

    private PdfiumPreviewDocument? _document;
    private PdfCoordinateMapper? _mapper;
    private int _currentPage;
    private double _zoom = 1.0;
    private Point? _selectionAnchor;

    /// <summary>渲染代际号：结果到达时若已翻页/换文档/再缩放则丢弃，避免旧位图覆盖新位图。</summary>
    private int _renderGeneration;

    /// <summary>构造时捕获的 UI Dispatcher：后台渲染完成后封送回 UI 线程绑定（F3-BLK-1）。</summary>
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public PdfPreviewControl()
    {
        InitializeComponent();
    }

    /// <summary>框选完成（仅 IsRegionSelectionEnabled 时触发）。</summary>
    public event EventHandler<PdfRegionSelectedEventArgs>? RegionSelected;

    /// <summary>是否启用框选（对应上游 crop/edit/sign/redact/forms 工具场景）。</summary>
    public bool IsRegionSelectionEnabled { get; set; }

    /// <summary>当前缩放倍数（0.25–4.0）。</summary>
    public double Zoom => _zoom;

    /// <summary>当前页（0 基；未打开时为 -1）。</summary>
    public int CurrentPageIndex => _document is null ? -1 : _currentPage;

    /// <summary>总页数（未打开时为 0）。</summary>
    public int PageCount => _document?.PageCount ?? 0;

    /// <summary>打开 PDF 并渲染第 1 页。文件以 FileShare.ReadWrite 共享读；
    /// 格式非法或密码错误时抛异常，由调用方处理。渲染为后台异步，位图完成后封送 UI 线程。</summary>
    public void OpenPdf(string filePath, string? password = null)
    {
        ClosePdf();
        _document = PdfiumPreviewDocument.OpenFile(filePath, password);
        _currentPage = 0;
        ClearSelection();
        RenderCurrentPageAsync();
    }

    /// <summary>关闭当前文档，回到空态（释放本预览独占的 PDFium 句柄）。</summary>
    public void ClosePdf()
    {
        var old = _document;
        _document = null;
        _mapper = null;
        _currentPage = 0;
        ClearSelection();
        PageImage.Source = null;
        old?.Dispose();
        UpdateState();
    }

    /// <summary>清除当前框选框。</summary>
    public void ClearSelection()
    {
        _selectionAnchor = null;
        SelectionRect.Visibility = Visibility.Collapsed;
        if (SelectionCanvas.IsMouseCaptured)
        {
            SelectionCanvas.ReleaseMouseCapture();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var document = _document;
        if (document is not null && e.Key == Key.PageUp)
        {
            e.Handled = true;
            GoToPage(_currentPage - 1);
        }
        else if (document is not null && e.Key == Key.PageDown)
        {
            e.Handled = true;
            GoToPage(_currentPage + 1);
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        var document = _document;
        if (document is not null && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            SetZoom(NextZoomPreset(_zoom, e.Delta > 0));
        }

        base.OnPreviewMouseWheel(e);
    }

    /// <summary>缩放档位推进（向上取更大的档，向下取更小的档；已到边界时保持不变）。</summary>
    internal static double NextZoomPreset(double current, bool zoomIn)
    {
        if (zoomIn)
        {
            foreach (var preset in ZoomPresets)
            {
                if (preset > current + 0.001)
                {
                    return preset;
                }
            }

            return PdfCoordinateMapper.MaxZoom;
        }

        for (var i = ZoomPresets.Length - 1; i >= 0; i--)
        {
            if (ZoomPresets[i] < current - 0.001)
            {
                return ZoomPresets[i];
            }
        }

        return PdfCoordinateMapper.MinZoom;
    }

    private void OnPrevPageClick(object sender, RoutedEventArgs e) => GoToPage(_currentPage - 1);

    private void OnNextPageClick(object sender, RoutedEventArgs e) => GoToPage(_currentPage + 1);

    private void GoToPage(int pageIndex)
    {
        var document = _document;
        if (document is null || pageIndex < 0 || pageIndex >= document.PageCount || pageIndex == _currentPage)
        {
            return;
        }

        _currentPage = pageIndex;
        ClearSelection();
        RenderCurrentPageAsync();
    }

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, PdfCoordinateMapper.MinZoom, PdfCoordinateMapper.MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.0001 || _document is null)
        {
            return;
        }

        _zoom = zoom;
        ClearSelection();
        RenderCurrentPageAsync();
    }

    /// <summary>
    /// 后台渲染当前页，完成后封送回 UI 线程绑定（F3-BLK-1：绝不在后台线程改 UI 绑定状态）。
    /// PDFium 后端返回冻结 BitmapSource；<see cref="_renderGeneration"/> 丢弃过期结果
    /// （快速翻页/缩放时旧渲染晚到不得覆盖新位图）。
    /// </summary>
    private async void RenderCurrentPageAsync()
    {
        var document = _document;
        if (document is null)
        {
            _mapper = null;
            PageImage.Source = null;
            PageImage.Visibility = Visibility.Collapsed;
            SelectionCanvas.Visibility = Visibility.Collapsed;
            UpdateState();
            return;
        }

        var pageIndex = _currentPage;
        var mapper = document.CreateMapper(pageIndex, _zoom);
        _mapper = mapper;
        var generation = ++_renderGeneration;

        BitmapSource bitmap;
        try
        {
            bitmap = await document.RenderPageAsync(pageIndex, _zoom).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // 文档已关闭/切换，静默丢弃
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[PdfPreviewControl] 渲染失败: {ex.Message}");
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            if (generation != _renderGeneration || !ReferenceEquals(_document, document))
            {
                return; // 过期结果：已翻页/缩放/换文档
            }
            ApplyBitmap(bitmap);
        });
    }

    /// <summary>UI 线程应用已渲染位图（尺寸沿用像素尺寸，1px = 1DIP，与旧栈一致）。</summary>
    private void ApplyBitmap(BitmapSource bitmap)
    {
        PageImage.Source = bitmap;
        PageImage.Width = bitmap.PixelWidth;
        PageImage.Height = bitmap.PixelHeight;
        PageImage.Visibility = Visibility.Visible;
        SelectionCanvas.Visibility = IsRegionSelectionEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateState();
    }

    private void UpdateState()
    {
        var document = _document;
        var hasDocument = document is not null;

        EmptyState.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        PageIndicator.Text = hasDocument ? $"{_currentPage + 1} / {document!.PageCount}" : "– / –";
        ZoomIndicator.Text = hasDocument ? $"{Math.Round(_zoom * 100)}%" : "–";
        PrevPageButton.IsEnabled = hasDocument && _currentPage > 0;
        NextPageButton.IsEnabled = hasDocument && _currentPage < document!.PageCount - 1;
    }

    private void OnSelectionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsRegionSelectionEnabled || _document is null)
        {
            return;
        }

        _selectionAnchor = e.GetPosition(SelectionCanvas);
        DrawSelectionRect(_selectionAnchor.Value, _selectionAnchor.Value);
        SelectionRect.Visibility = Visibility.Visible;
        SelectionCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnSelectionMouseMove(object sender, MouseEventArgs e)
    {
        if (_selectionAnchor is not { } anchor)
        {
            return;
        }

        DrawSelectionRect(anchor, e.GetPosition(SelectionCanvas));
        e.Handled = true;
    }

    private void OnSelectionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionAnchor is not { } anchor)
        {
            return;
        }

        _selectionAnchor = null;
        if (SelectionCanvas.IsMouseCaptured)
        {
            SelectionCanvas.ReleaseMouseCapture();
        }

        e.Handled = true;

        var mapper = _mapper;
        if (mapper is null)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        var viewRect = new Rect(anchor, e.GetPosition(SelectionCanvas));
        var pdfRect = mapper.ClampToPage(mapper.ViewToPdfRect(viewRect));
        if (pdfRect.IsEmpty || pdfRect.Width <= PdfCoordinateMapper.MinRegionPt || pdfRect.Height <= PdfCoordinateMapper.MinRegionPt)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        // 保留选框直到翻页 / 换文档 / 清除（与上游 SelectingPDFView 一致）
        RegionSelected?.Invoke(this, new PdfRegionSelectedEventArgs
        {
            PageIndex = _currentPage,
            RegionPt = pdfRect,
        });
    }

    private void OnSelectionLostCapture(object sender, MouseEventArgs e)
    {
        // 拖拽中失去捕获（如切窗口）：中止本次框选
        if (_selectionAnchor is not null)
        {
            _selectionAnchor = null;
            SelectionRect.Visibility = Visibility.Collapsed;
        }
    }

    private void DrawSelectionRect(Point anchor, Point current)
    {
        var rect = new Rect(anchor, current);
        Canvas.SetLeft(SelectionRect, rect.Left);
        Canvas.SetTop(SelectionRect, rect.Top);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
    }
}
