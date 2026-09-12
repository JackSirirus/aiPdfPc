// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T9（M8）显式 page↔group 映射：
//   对开（spread）模式下一个阅读项 = 一个「对页组」（1–2 页）；单页模式 group == page（恒等映射）。
// 本类型为纯值类型（无状态、无 WPF 依赖、无副作用），供 PageLayoutCalculator / ReaderViewModel /
// ReaderView 的组坐标换算统一消费，xunit 可直测。坐标与几何口径见 PageLayoutCalculator。
namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 页 ↔ 对页组映射（M8）。
/// <para>对开分组规则（<see cref="CoverPageAlone"/> = true）：第 1 页单独成组（封面单列），随后每两页一组；
/// false：从第 1 页起每两页一组（不留单列封面）。</para>
/// <para>单页模式：<see cref="IsIdentity"/> = true，<see cref="PageToGroup"/> 恒等、<see cref="GroupToPages"/> 返回单页。</para>
/// </summary>
public readonly struct PageGroupMap
{
    private PageGroupMap(bool spread, bool coverPageAlone, int pageCount)
    {
        PageCount = Math.Max(0, pageCount);
        SpreadEnabled = spread && PageCount > 0;
        CoverPageAlone = coverPageAlone;
        GroupCount = ComputeGroupCount();
    }

    /// <summary>是否对开模式（页数 > 0 且请求对开）。</summary>
    public bool SpreadEnabled { get; }

    /// <summary>对开模式下第 1 页是否单独成组（封面单列）。</summary>
    public bool CoverPageAlone { get; }

    /// <summary>总页数。</summary>
    public int PageCount { get; }

    /// <summary>总组数。</summary>
    public int GroupCount { get; }

    /// <summary>单页模式：group == page，映射恒等。</summary>
    public bool IsIdentity => !SpreadEnabled;

    /// <summary>构造单页（恒等）映射。</summary>
    public static PageGroupMap Single(int pageCount) => new(spread: false, coverPageAlone: false, pageCount);

    /// <summary>构造对开映射；<paramref name="coverPageAlone"/> 默认 true（封面单列）。</summary>
    public static PageGroupMap Spread(int pageCount, bool coverPageAlone = true) => new(spread: true, coverPageAlone, pageCount);

    /// <summary>
    /// 纯函数：页索引所在组。越界按边界钳制；空文档返回 -1。
    /// 单页模式为恒等映射。
    /// </summary>
    public int PageToGroup(int pageIndex)
    {
        if (PageCount == 0) return -1;
        var page = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (!SpreadEnabled) return page;
        return CoverPageAlone
            ? (page == 0 ? 0 : (page + 1) / 2)
            : page / 2;
    }

    /// <summary>
    /// 纯函数：组内含页范围（含端点；空文档返回 (0, -1)）。组索引越界按边界钳制。
    /// 单页模式返回 (group, group)。
    /// </summary>
    public (int First, int Last) GroupToPages(int groupIndex)
    {
        if (GroupCount == 0) return (0, -1);
        var group = Math.Clamp(groupIndex, 0, GroupCount - 1);
        if (!SpreadEnabled) return (group, group);

        if (CoverPageAlone)
        {
            if (group == 0) return (0, 0);
            return (2 * group - 1, Math.Min(2 * group, PageCount - 1));
        }

        return (2 * group, Math.Min(2 * group + 1, PageCount - 1));
    }

    /// <summary>页在所属组内的序号（0 起）；空文档返回 -1。</summary>
    public int PageIndexInGroup(int pageIndex)
    {
        var group = PageToGroup(pageIndex);
        if (group < 0) return -1;
        var (first, _) = GroupToPages(group);
        return Math.Clamp(pageIndex, 0, PageCount - 1) - first;
    }

    private int ComputeGroupCount()
    {
        if (PageCount == 0) return 0;
        if (!SpreadEnabled) return PageCount;
        return CoverPageAlone ? 1 + PageCount / 2 : (PageCount + 1) / 2;
    }
}
