// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 阅读视图纯逻辑层：页码 ↔ 滚动偏移换算、缩放阶梯、视口缓冲计算。
// 全部为无状态纯函数，供 ReaderViewModel 消费、xunit 直接覆盖（混合页尺寸场景）。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 阅读区布局计算（纯函数，无状态）。
/// 坐标口径：滚动偏移与页高均为 WPF 设备无关像素（DIP）；页尺寸输入为 PDF 点（1pt = 1/72 inch）。
/// </summary>
public static class PageLayoutCalculator
{
    /// <summary>FR-01：页间距 16px。</summary>
    public const double PageGap = 16.0;

    /// <summary>zoom=100% 的渲染基准 DPI。</summary>
    public const double BaseDpi = 96.0;

    /// <summary>zoom ≥ 150% 时的渲染 DPI（PRD 4.1 口径）。</summary>
    public const double HighDpi = 150.0;

    /// <summary>启用高 DPI 渲染的 zoom 阈值（百分比，含）。</summary>
    public const double HighDpiThresholdPercent = 150.0;

    /// <summary>WPF 设备无关像素 / PDF 点（96/72），与 PdfCoordinateMapper.DpiScale 一致。</summary>
    public const double DpiScale = 96.0 / 72.0;

    /// <summary>缩放阶梯（%）：FR-06 九档。</summary>
    public static readonly double[] LadderPercent = [25, 33, 50, 67, 100, 150, 200, 300, 400];

    /// <summary>浮点比较容差。</summary>
    public const double Epsilon = 0.01;

    // ------------------------------------------------------------------ 缩放

    /// <summary>当前 zoom 对应的渲染 DPI：zoom ≥ 150% 用 150dpi，否则 96dpi 基准。</summary>
    public static double DpiForZoom(double zoomPercent)
        => zoomPercent >= HighDpiThresholdPercent ? HighDpi : BaseDpi;

    /// <summary>距 zoom 最近的阶梯档索引；距离并列取更低档。</summary>
    public static int NearestLadderIndex(double zoomPercent)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < LadderPercent.Length; i++)
        {
            var dist = Math.Abs(LadderPercent[i] - zoomPercent);
            // 严格更小才替换：并列（如 83.5 介于 67/100）取更低档
            if (dist < bestDist - Epsilon)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }

    /// <summary>吸附到最近的阶梯档（用于 Ctrl+滚轮落档、脱离适配档）。</summary>
    public static double SnapToLadder(double zoomPercent) => LadderPercent[NearestLadderIndex(zoomPercent)];

    /// <summary>
    /// 沿阶梯逐档移动 <paramref name="steps"/> 步（正=放大，负=缩小），端点处钳制【ST-11】。
    /// 当前值不在阶梯上（适配动态档）时：正步取首个严格更大的档，负步取末个严格更小的档。
    /// </summary>
    public static double StepLadder(double zoomPercent, int steps)
    {
        if (steps == 0) return zoomPercent;
        if (steps > 0)
        {
            var idx = Array.FindIndex(LadderPercent, v => v > zoomPercent + Epsilon);
            if (idx < 0) return LadderPercent[^1]; // 已在 400% 端点
            return LadderPercent[Math.Min(idx + steps - 1, LadderPercent.Length - 1)];
        }
        var lower = Array.FindLastIndex(LadderPercent, v => v < zoomPercent - Epsilon);
        if (lower < 0) return LadderPercent[0]; // 已在 25% 端点
        return LadderPercent[Math.Max(lower + steps + 1, 0)];
    }

    /// <summary>适配宽度 zoom（%）：视口宽度容纳最大页宽；结果钳制在 [25, 400]。</summary>
    public static double FitWidthZoomPercent(double viewportWidth, double pageWidthPt)
    {
        if (viewportWidth <= 0 || pageWidthPt <= 0) return 100;
        var zoom = viewportWidth / (pageWidthPt * DpiScale) * 100.0;
        return Math.Clamp(zoom, LadderPercent[0], LadderPercent[^1]);
    }

    /// <summary>适配页面 zoom（%）：宽与高取更紧约束；结果钳制在 [25, 400]。</summary>
    public static double FitPageZoomPercent(double viewportWidth, double viewportHeight, double pageWidthPt, double pageHeightPt)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || pageWidthPt <= 0 || pageHeightPt <= 0) return 100;
        var byWidth = viewportWidth / (pageWidthPt * DpiScale) * 100.0;
        var byHeight = viewportHeight / (pageHeightPt * DpiScale) * 100.0;
        return Math.Clamp(Math.Min(byWidth, byHeight), LadderPercent[0], LadderPercent[^1]);
    }

    // ------------------------------------------------------------------ 布局（DIP）

    /// <summary>每页显示高（DIP）：pt × zoom × DpiScale。</summary>
    public static double[] ComputePageHeights(IReadOnlyList<PageSize> sizesPt, double zoomPercent)
    {
        var scale = zoomPercent / 100.0 * DpiScale;
        var heights = new double[sizesPt.Count];
        for (var i = 0; i < sizesPt.Count; i++) heights[i] = sizesPt[i].Height * scale;
        return heights;
    }

    /// <summary>
    /// 每页顶部偏移（DIP，含页间距）。混合页尺寸逐页累加，滚动换算以此为准。
    /// </summary>
    public static double[] ComputePageOffsets(IReadOnlyList<PageSize> sizesPt, double zoomPercent)
    {
        var scale = zoomPercent / 100.0 * DpiScale;
        var offsets = new double[sizesPt.Count];
        var acc = 0.0;
        for (var i = 0; i < sizesPt.Count; i++)
        {
            offsets[i] = acc;
            acc += sizesPt[i].Height * scale + PageGap;
        }
        return offsets;
    }

    /// <summary>内容总高（DIP）= 末页底部 + 尾部页间距；空文档为 0。</summary>
    public static double ComputeContentHeight(IReadOnlyList<PageSize> sizesPt, double zoomPercent)
    {
        if (sizesPt.Count == 0) return 0;
        var offsets = ComputePageOffsets(sizesPt, zoomPercent);
        var heights = ComputePageHeights(sizesPt, zoomPercent);
        return offsets[^1] + heights[^1] + PageGap;
    }

    // ------------------------------------------------------------------ 视口换算

    /// <summary>
    /// y（DIP）所在页索引：最大的 i 使 offsets[i] ≤ y（落在页间隙时返回上一页）；y 越界钳到两端。空文档返回 -1。
    /// </summary>
    public static int PageAtPoint(double[] offsets, double y)
    {
        if (offsets.Length == 0) return -1;
        if (y <= offsets[0]) return 0;
        int lo = 0, hi = offsets.Length - 1, ans = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (offsets[mid] <= y) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>
    /// 可视页范围（0 起，含端点），向两侧各扩展 <paramref name="bufferPages"/> 缓冲页并钳制边界。
    /// 空文档或视口未测量（viewportHeight ≤ 0）返回 (0, -1) 表示空区间。
    /// </summary>
    public static (int First, int Last) VisibleRange(double[] offsets, double[] heights, double scrollTop, double viewportHeight, int bufferPages)
    {
        if (offsets.Length == 0 || viewportHeight <= 0) return (0, -1);
        if (scrollTop < 0) scrollTop = 0;
        var scrollBottom = scrollTop + viewportHeight;

        var first = PageAtPoint(offsets, scrollTop);
        // last：最大的 i 使 offsets[i] < scrollBottom（页顶进入视口即算可见）
        int lo = first, hi = offsets.Length - 1, last = first;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (offsets[mid] < scrollBottom) { last = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        var buffer = Math.Max(0, bufferPages);
        return (Math.Max(0, first - buffer), Math.Min(offsets.Length - 1, last + buffer));
    }

    /// <summary>
    /// 当前页判定（FR-01）：视口中与页面交叠面积最大的页；并列取靠前页。无交叠返回 -1。
    /// </summary>
    public static int CurrentPageByMaxOverlap(double[] offsets, double[] heights, double scrollTop, double viewportHeight)
    {
        if (offsets.Length == 0 || viewportHeight <= 0) return -1;
        var scrollBottom = scrollTop + viewportHeight;
        var best = -1;
        var bestOverlap = 0.0;
        for (var i = PageAtPoint(offsets, Math.Max(0, scrollTop)); i < offsets.Length && offsets[i] < scrollBottom; i++)
        {
            var overlap = Math.Min(offsets[i] + heights[i], scrollBottom) - Math.Max(offsets[i], scrollTop);
            // 严格更大才替换：并列时保留靠前页
            if (overlap > bestOverlap + Epsilon)
            {
                bestOverlap = overlap;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// 缩放后保持视口中心锚点：换算缩放前视口中心所在页与页内比例，映射到新布局后取对应滚动位置。
    /// 结果钳制在 [0, 新内容高 - 视口高]。
    /// </summary>
    public static double ZoomAnchorScroll(double[] oldOffsets, double[] oldHeights, double oldScrollTop, double viewportHeight,
        double[] newOffsets, double[] newHeights)
    {
        if (newOffsets.Length == 0) return 0;
        var vh = Math.Max(0, viewportHeight);
        var center = oldScrollTop + vh / 2.0;
        var page = PageAtPoint(oldOffsets, center);
        if (page < 0) page = 0;
        var frac = oldHeights[page] <= 0 ? 0.0 : (center - oldOffsets[page]) / oldHeights[page];
        frac = Math.Clamp(frac, 0.0, 1.0);
        var newCenter = newOffsets[page] + frac * newHeights[page];
        var contentHeight = newOffsets[^1] + newHeights[^1] + PageGap;
        var maxScroll = Math.Max(0, contentHeight - vh);
        return Math.Clamp(newCenter - vh / 2.0, 0, maxScroll);
    }

    /// <summary>页码输入解析（FR-05）：1..pageCount 之外（含 0、越界、非数字）一律拒绝。</summary>
    public static bool TryParsePageNumber(string? text, int pageCount, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(text) || pageCount <= 0) return false;
        if (!int.TryParse(text.Trim(), out var value)) return false;
        if (value < 1 || value > pageCount) return false;
        page = value;
        return true;
    }
}
