// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 阅读视图纯逻辑层：页码 ↔ 滚动偏移换算、缩放阶梯、视口缓冲计算。
// 全部为无状态纯函数，供 ReaderViewModel 消费、xunit 直接覆盖（混合页尺寸场景）。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 页内搜索高亮矩形（DIP，左上原点）。由 <see cref="PageLayoutCalculator.ToSearchHighlightDip"/>
/// 从 PDF 点矩形按页显示缩放换算得到；ReaderView 叠加层以 Canvas 绝对定位消费（命中测试穿透）。
/// </summary>
public readonly record struct SearchHighlight(double X, double Y, double Width, double Height);

/// <summary>
/// 对页组内命中结果（V3 T9）：<paramref name="PageOffset"/> 为组内页序号（0 起，-1 = 空组），
/// <paramref name="LocalX"/>/<paramref name="LocalY"/> 为钳制后的页局部坐标（DIP，页左上原点）。
/// <paramref name="Inside"/> 表示点确落在该页显示矩形内；落在组内页间距/页外时为 false，但
/// <paramref name="PageOffset"/> 仍给出按 X 最近的页，供拖选跨页判定消费。
/// </summary>
public readonly record struct GroupHit(bool Inside, int PageOffset, double LocalX, double LocalY);

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

    /// <summary>
    /// 页内矩形（PDF 点，左上原点）→ 页内显示 DIP：scale = 页显示宽 DIP / 页宽 pt（== zoom × DpiScale）。
    /// 搜索高亮与批注叠加层共用此唯一换算（不新增坐标层，PRD 4.2.2）；缩放变更时按新 DisplayWidth 重算。
    /// 输入退化（页宽/显示宽 ≤ 0）返回默认值（零矩形，不绘制）。
    /// </summary>
    public static SearchHighlight ToPageDip(PdfRect rect, double pageWidthPt, double displayWidthDip)
    {
        if (pageWidthPt <= 0 || displayWidthDip <= 0) return default;
        var scale = displayWidthDip / pageWidthPt;
        return new SearchHighlight(rect.Left * scale, rect.Top * scale, rect.Width * scale, rect.Height * scale);
    }

    /// <summary>
    /// 命中矩形（PDF 点，左上原点）→ 页内显示 DIP。语义别名，委托 <see cref="ToPageDip"/> 保持单一换算。
    /// </summary>
    public static SearchHighlight ToSearchHighlightDip(PdfRect rect, double pageWidthPt, double displayWidthDip)
        => ToPageDip(rect, pageWidthPt, displayWidthDip);

    /// <summary>缩放阶梯（%）：FR-06 九档。</summary>
    public static readonly double[] LadderPercent = [25, 33, 50, 67, 100, 150, 200, 300, 400];

    /// <summary>浮点比较容差。</summary>
    public const double Epsilon = 0.01;

    // ------------------------------------------------------------------ 缩放

    /// <summary>
    /// 当前 zoom 对应的渲染 DPI：zoom ≥ 150% 用高 DPI（<see cref="HighDpi"/>，但不低于注入基准），
    /// 否则用渲染基准 DPI。默认 <paramref name="baseDpi"/> = <see cref="BaseDpi"/>（96），
    /// 既有纯函数调用与测试语义不变【T1 SettingsStore 不变量】。
    /// <para>
    /// V3 T5：设置 <c>DefaultRenderDpi</c> 仅覆盖 <paramref name="baseDpi"/>（渲染基准），
    /// 不改「zoom ≥150% → HighDpi=150」阈值阶梯——阈值档返回 <c>Math.Max(baseDpi, HighDpi)</c>，
    /// 保证注入更大基准时高倍率仍更清晰、注入更小基准时不越过 HighDpi。不参与打印（打印用 PrintDpi）。
    /// </para>
    /// </summary>
    public static double DpiForZoom(double zoomPercent, double baseDpi = BaseDpi)
        => zoomPercent >= HighDpiThresholdPercent ? Math.Max(baseDpi, HighDpi) : baseDpi;

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

    /// <summary>
    /// 适配宽度 zoom（%）：视口宽度容纳最大页宽；结果钳制在 [25, 400]。
    /// <paramref name="extraDip"/>（V3 T9）为对开模式下组内页间距等固定占用：可用宽 = 视口宽 − extraDip；
    /// 默认 0 保持既有单页语义与既有调用不变。
    /// </summary>
    public static double FitWidthZoomPercent(double viewportWidth, double pageWidthPt, double extraDip = 0)
    {
        if (viewportWidth <= 0 || pageWidthPt <= 0) return 100;
        var available = Math.Max(Epsilon, viewportWidth - Math.Max(0, extraDip));
        var zoom = available / (pageWidthPt * DpiScale) * 100.0;
        return Math.Clamp(zoom, LadderPercent[0], LadderPercent[^1]);
    }

    /// <summary>
    /// 适配页面 zoom（%）：宽与高取更紧约束；结果钳制在 [25, 400]。
    /// <paramref name="extraWidthDip"/> / <paramref name="extraHeightDip"/>（V3 T9）为对开模式下组尺寸的固定占用；
    /// 默认 0 保持既有单页语义与既有调用不变。
    /// </summary>
    public static double FitPageZoomPercent(double viewportWidth, double viewportHeight, double pageWidthPt, double pageHeightPt,
        double extraWidthDip = 0, double extraHeightDip = 0)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || pageWidthPt <= 0 || pageHeightPt <= 0) return 100;
        var availableWidth = Math.Max(Epsilon, viewportWidth - Math.Max(0, extraWidthDip));
        var availableHeight = Math.Max(Epsilon, viewportHeight - Math.Max(0, extraHeightDip));
        var byWidth = availableWidth / (pageWidthPt * DpiScale) * 100.0;
        var byHeight = availableHeight / (pageHeightPt * DpiScale) * 100.0;
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

    // ------------------------------------------------------------------ 对开（spread）布局（V3 T9 / M8）

    /// <summary>对开拓扑薄封装：页索引所在组（等价 <see cref="PageGroupMap.PageToGroup"/>，保留计划命名）。</summary>
    public static int GroupForPage(int pageIndex, bool spread, bool coverPageAlone, int pageCount)
        => (spread ? PageGroupMap.Spread(pageCount, coverPageAlone) : PageGroupMap.Single(pageCount)).PageToGroup(pageIndex);

    /// <summary>对开拓扑薄封装：组内含页范围（等价 <see cref="PageGroupMap.GroupToPages"/>）。</summary>
    public static (int First, int Last) PagesInGroup(int groupIndex, bool spread, bool coverPageAlone, int pageCount)
        => (spread ? PageGroupMap.Spread(pageCount, coverPageAlone) : PageGroupMap.Single(pageCount)).GroupToPages(groupIndex);

    /// <summary>每组显示高（DIP）：组内页高最大值（单页模式即页高）。空文档返回空数组。</summary>
    public static double[] ComputeGroupHeights(IReadOnlyList<PageSize> sizesPt, double zoomPercent, PageGroupMap map)
    {
        var heights = new double[map.GroupCount];
        if (map.GroupCount == 0) return heights;
        var scale = zoomPercent / 100.0 * DpiScale;
        for (var g = 0; g < map.GroupCount; g++)
        {
            var (first, last) = map.GroupToPages(g);
            var h = 0.0;
            for (var p = first; p <= last && p < sizesPt.Count; p++) h = Math.Max(h, sizesPt[p].Height * scale);
            heights[g] = h;
        }
        return heights;
    }

    /// <summary>每组顶部偏移（DIP，含组间距）；混合页尺寸/对开逐组累加。</summary>
    public static double[] ComputeGroupOffsets(IReadOnlyList<PageSize> sizesPt, double zoomPercent, PageGroupMap map)
    {
        var heights = ComputeGroupHeights(sizesPt, zoomPercent, map);
        var offsets = new double[heights.Length];
        var acc = 0.0;
        for (var g = 0; g < heights.Length; g++)
        {
            offsets[g] = acc;
            acc += heights[g] + PageGap;
        }
        return offsets;
    }

    /// <summary>每组显示宽（DIP）：组内页宽之和 + 组内页间距（页数 > 1 时）。</summary>
    public static double[] ComputeGroupWidths(IReadOnlyList<PageSize> sizesPt, double zoomPercent, PageGroupMap map)
    {
        var widths = new double[map.GroupCount];
        if (map.GroupCount == 0) return widths;
        var scale = zoomPercent / 100.0 * DpiScale;
        for (var g = 0; g < map.GroupCount; g++)
        {
            var (first, last) = map.GroupToPages(g);
            var w = 0.0;
            var count = 0;
            for (var p = first; p <= last && p < sizesPt.Count; p++)
            {
                if (count > 0) w += PageGap;
                w += sizesPt[p].Width * scale;
                count++;
            }
            widths[g] = w;
        }
        return widths;
    }

    /// <summary>对开内容总高（DIP）= 末组底部 + 尾部组间距；空文档为 0。</summary>
    public static double ComputeGroupsContentHeight(IReadOnlyList<PageSize> sizesPt, double zoomPercent, PageGroupMap map)
    {
        if (map.GroupCount == 0) return 0;
        var offsets = ComputeGroupOffsets(sizesPt, zoomPercent, map);
        var heights = ComputeGroupHeights(sizesPt, zoomPercent, map);
        return offsets[^1] + heights[^1] + PageGap;
    }

    /// <summary>页在组内的水平偏移（DIP）：组内左侧页为 0，右侧页为左页显示宽 + 页间距；单页为 0。</summary>
    public static double PageXWithinGroup(int pageIndex, IReadOnlyList<PageSize> sizesPt, double zoomPercent, PageGroupMap map)
    {
        var group = map.PageToGroup(pageIndex);
        if (group < 0) return 0;
        var (first, _) = map.GroupToPages(group);
        var scale = zoomPercent / 100.0 * DpiScale;
        var x = 0.0;
        for (var p = first; p < pageIndex && p < sizesPt.Count; p++) x += sizesPt[p].Width * scale + PageGap;
        return x;
    }

    /// <summary>组内最大内容宽（PDF 点）：组内页宽之和（不含 DIP 间距），供适配宽度换算。</summary>
    public static double MaxGroupContentWidthPt(IReadOnlyList<PageSize> sizesPt, PageGroupMap map)
    {
        var max = 0.0;
        for (var g = 0; g < map.GroupCount; g++)
        {
            var (first, last) = map.GroupToPages(g);
            var w = 0.0;
            for (var p = first; p <= last && p < sizesPt.Count; p++) w += sizesPt[p].Width;
            max = Math.Max(max, w);
        }
        return max;
    }

    /// <summary>组内最大页高（PDF 点），供适配页面换算。</summary>
    public static double MaxGroupContentHeightPt(IReadOnlyList<PageSize> sizesPt, PageGroupMap map)
    {
        var max = 0.0;
        for (var g = 0; g < map.GroupCount; g++)
        {
            var (first, last) = map.GroupToPages(g);
            for (var p = first; p <= last && p < sizesPt.Count; p++) max = Math.Max(max, sizesPt[p].Height);
        }
        return max;
    }

    /// <summary>是否存在含 2 页的组（决定适配宽度/页面是否扣减一个页间距）。</summary>
    public static bool HasMultiPageGroup(PageGroupMap map)
    {
        for (var g = 0; g < map.GroupCount; g++)
        {
            var (first, last) = map.GroupToPages(g);
            if (last > first) return true;
        }
        return false;
    }

    /// <summary>按 y（DIP）取对页组索引：语义同 <see cref="PageAtPoint"/>，偏移数组改为组偏移。</summary>
    public static int GroupAtPoint(double[] groupOffsets, double y) => PageAtPoint(groupOffsets, y);

    /// <summary>可视组范围（含缓冲组）；语义同 <see cref="VisibleRange"/>，输入改为组偏移/组高。</summary>
    public static (int First, int Last) VisibleGroupRange(double[] groupOffsets, double[] groupHeights,
        double scrollTop, double viewportHeight, int bufferGroups)
        => VisibleRange(groupOffsets, groupHeights, scrollTop, viewportHeight, bufferGroups);

    /// <summary>当前对页组判定（交叠最大，并列取靠前）；语义同 <see cref="CurrentPageByMaxOverlap"/>。</summary>
    public static int CurrentGroupByMaxOverlap(double[] groupOffsets, double[] groupHeights, double scrollTop, double viewportHeight)
        => CurrentPageByMaxOverlap(groupOffsets, groupHeights, scrollTop, viewportHeight);

    /// <summary>
    /// 组内点命中（纯函数）：给定组内各页显示宽/高（DIP）与组内坐标，返回页序号与钳制后的页局部坐标。
    /// 点在页间距/页外时 <see cref="GroupHit.Inside"/> 为 false，但按 X 距离最近的页仍返回，供拖选跨页判定。
    /// </summary>
    public static GroupHit HitPageInGroup(IReadOnlyList<double> pageWidthsDip, IReadOnlyList<double> pageHeightsDip,
        double groupX, double groupY, double gap = PageGap)
    {
        if (pageWidthsDip is null || pageWidthsDip.Count == 0) return new GroupHit(false, -1, 0, 0);

        // 1) 精确包含：按 X 落在某页水平区间
        var cursor = 0.0;
        var contained = -1;
        for (var i = 0; i < pageWidthsDip.Count; i++)
        {
            var w = pageWidthsDip[i];
            if (groupX >= cursor - Epsilon && groupX <= cursor + w + Epsilon) { contained = i; break; }
            cursor += w + gap;
        }

        // 2) 未精确包含（页间距/页外）：取 X 距离最近的页，保证拖选仍能给出页序号
        var index = contained;
        var start = 0.0;
        if (index < 0)
        {
            var bestDist = double.MaxValue;
            cursor = 0.0;
            for (var i = 0; i < pageWidthsDip.Count; i++)
            {
                var w = pageWidthsDip[i];
                var dist = groupX < cursor ? cursor - groupX : (groupX > cursor + w ? groupX - (cursor + w) : 0);
                if (dist < bestDist - Epsilon) { bestDist = dist; index = i; start = cursor; }
                cursor += w + gap;
            }
        }
        else
        {
            cursor = 0.0;
            for (var i = 0; i < index; i++) cursor += pageWidthsDip[i] + gap;
            start = cursor;
        }

        if (index < 0) index = 0;
        if (index >= pageWidthsDip.Count) index = pageWidthsDip.Count - 1;

        var pageW = pageWidthsDip[index];
        var pageH = index < pageHeightsDip.Count ? pageHeightsDip[index] : 0;
        var localX = Math.Clamp(groupX - start, 0, Math.Max(0, pageW));
        var localY = Math.Clamp(groupY, 0, Math.Max(0, pageH));
        var inside = groupX >= start - Epsilon && groupX <= start + pageW + Epsilon
                     && groupY >= -Epsilon && groupY <= pageH + Epsilon;
        return new GroupHit(inside, index, localX, localY);
    }

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
