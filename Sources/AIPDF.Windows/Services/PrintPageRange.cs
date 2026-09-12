// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T3 打印范围解析与校验（纯逻辑：无 UI、无文件 I/O、无 PDFium 调用）。
//
// 支持三种输入：
//   "all"                        → 全部页
//   "current" / "current-page"   → 当前页
//   "1-3,5"                      → 自定义（1 基，逗号分隔，单页 N 或区间 A-B）
//
// 合法性（任一不满足 → 整体非法，供 UI 禁用打印，AC-30.2）：
//   - 空串 / 空 token；
//   - 页码 < 1 或 > pageCount（越界）；
//   - 区间起点 > 终点（逆序）；
//   - 非数字 / 多段破折号 / 小数 / 空区间端点。
// 合法时输出升序、去重的 0 基页索引序列（页码范围解析绝不抛异常）。
using System.Globalization;

namespace AIPDF.Windows.Services;

/// <summary>
/// 打印范围解析结果：合法时携带 0 基页索引序列；非法时携带中文原因。
/// 解析失败是常态输入（用户在范围框中打字），故用结果对象而非异常表达。
/// </summary>
public sealed class PrintPageRangeResult
{
    private static readonly int[] EmptyPages = [];

    private PrintPageRangeResult(bool isValid, IReadOnlyList<int> pages, string? error)
    {
        IsValid = isValid;
        Pages = pages;
        Error = error;
    }

    /// <summary>是否合法；false 时 UI 应禁用「打印」。</summary>
    public bool IsValid { get; }

    /// <summary>0 基页索引序列（升序、去重）；非法时为空序列。</summary>
    public IReadOnlyList<int> Pages { get; }

    /// <summary>非法原因（中文，可直接展示）；合法时为 null。</summary>
    public string? Error { get; }

    public static PrintPageRangeResult Valid(IReadOnlyList<int> pages) => new(true, pages, null);

    public static PrintPageRangeResult Invalid(string error) => new(false, EmptyPages, error);
}

/// <summary>打印页码范围解析器（无状态纯函数，可直测）。</summary>
public static class PrintPageRange
{
    /// <summary>「全部页」标记。</summary>
    public const string AllToken = "all";

    /// <summary>「当前页」标记。</summary>
    public const string CurrentToken = "current";

    /// <summary>「当前页」备用标记（含连字符写法）。</summary>
    public const string CurrentPageToken = "current-page";

    // 非法原因文案集中在此，供 UI 直接展示（内部常量，避免散落魔法串）。
    internal const string ErrorEmptyDocument = "文档没有可打印的页面";
    internal const string ErrorEmptyRange = "请输入打印页码范围";
    internal const string ErrorEmptyToken = "打印范围包含空项，请检查逗号分隔";
    internal const string ErrorNonNumeric = "打印范围格式无效，请使用如 1-3,5 的形式";
    internal const string ErrorOutOfRange = "打印范围超出文档页数";
    internal const string ErrorReversed = "打印范围的起止页码颠倒";
    internal const string ErrorInvalidCurrentPage = "当前页无效";

    /// <summary>「全部页」；<paramref name="pageCount"/> ≤ 0 视为非法（无文档）。</summary>
    public static PrintPageRangeResult All(int pageCount)
    {
        if (pageCount <= 0) return PrintPageRangeResult.Invalid(ErrorEmptyDocument);

        var pages = new int[pageCount];
        for (var i = 0; i < pageCount; i++) pages[i] = i;
        return PrintPageRangeResult.Valid(pages);
    }

    /// <summary>「当前页」；<paramref name="currentPageIndex"/> 为 0 基，越界即非法。</summary>
    public static PrintPageRangeResult CurrentPage(int pageCount, int currentPageIndex)
    {
        if (pageCount <= 0) return PrintPageRangeResult.Invalid(ErrorEmptyDocument);
        if (currentPageIndex < 0 || currentPageIndex >= pageCount)
            return PrintPageRangeResult.Invalid(ErrorInvalidCurrentPage);
        return PrintPageRangeResult.Valid([currentPageIndex]);
    }

    /// <summary>
    /// 解析范围说明。<paramref name="spec"/> 支持 <c>all</c> / <c>current</c> / <c>current-page</c>
    /// 或自定义 1 基范围（如 <c>1-3,5</c>）；<paramref name="currentPageIndex"/> 仅 "current" 分支使用
    /// （0 基，默认 -1 表示未提供）。大小写不敏感、容忍首尾空白。
    /// </summary>
    public static PrintPageRangeResult Parse(string? spec, int pageCount, int currentPageIndex = -1)
    {
        if (pageCount <= 0) return PrintPageRangeResult.Invalid(ErrorEmptyDocument);

        var value = spec?.Trim();
        if (string.IsNullOrEmpty(value)) return PrintPageRangeResult.Invalid(ErrorEmptyRange);

        if (value.Equals(AllToken, StringComparison.OrdinalIgnoreCase)) return All(pageCount);
        if (value.Equals(CurrentToken, StringComparison.OrdinalIgnoreCase)
            || value.Equals(CurrentPageToken, StringComparison.OrdinalIgnoreCase))
        {
            return CurrentPage(pageCount, currentPageIndex);
        }

        return ParseCustom(value, pageCount);
    }

    private static PrintPageRangeResult ParseCustom(string value, int pageCount)
    {
        var pages = new SortedSet<int>(); // 升序 + 去重

        foreach (var raw in value.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) return PrintPageRangeResult.Invalid(ErrorEmptyToken);

            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!TryParsePage(token, out var single))
                    return PrintPageRangeResult.Invalid(ErrorNonNumeric);
                if (single < 1 || single > pageCount)
                    return PrintPageRangeResult.Invalid(ErrorOutOfRange);
                pages.Add(single - 1);
                continue;
            }

            var left = token[..dash].Trim();
            var right = token[(dash + 1)..].Trim();
            if (left.Length == 0 || right.Length == 0 || right.IndexOf('-') >= 0)
                return PrintPageRangeResult.Invalid(ErrorNonNumeric);
            if (!TryParsePage(left, out var start) || !TryParsePage(right, out var end))
                return PrintPageRangeResult.Invalid(ErrorNonNumeric);
            if (start > end) return PrintPageRangeResult.Invalid(ErrorReversed);
            if (start < 1 || end > pageCount)
                return PrintPageRangeResult.Invalid(ErrorOutOfRange);

            for (var page = start; page <= end; page++) pages.Add(page - 1);
        }

        return pages.Count == 0
            ? PrintPageRangeResult.Invalid(ErrorEmptyRange)
            : PrintPageRangeResult.Valid(pages.ToArray());
    }

    /// <summary>仅接受非负十进制整数（拒绝符号/小数/前导空白；已由调用方 trim）。</summary>
    private static bool TryParsePage(string token, out int value)
        => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
