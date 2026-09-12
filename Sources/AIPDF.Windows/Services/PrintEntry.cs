// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T6 打印入口纯逻辑（不依赖真实打印机 / 桌面会话，可 xunit 直测）：
//   - 门禁入参解析：把阅读器可观测状态映射为 T3 PrintService.EvaluateGate 的入参；
//     【不复制】门禁判定逻辑，仍由 T3 裁决（AC-30.2）。
//   - 范围映射：把系统 PrintDialog 的范围选择映射为 T3 PrintPageRange（全部 / 当前页 / 自定义）。
using System.Windows.Controls;

namespace AIPDF.Windows.Services;

/// <summary>
/// 打印入口纯逻辑：门禁输入解析 + 打印对话框范围映射。无副作用、不触碰 UI/IO，便于单测。
/// T6 只消费 T3（<see cref="PrintService"/> / <see cref="PrintPageRange"/>），不修改其文件。
/// </summary>
public static class PrintEntry
{
    /// <summary>
    /// 解析门禁输入并复用 T3 <see cref="PrintService.EvaluateGate"/>（AC-30.2，不复制判定逻辑）。
    /// <para>
    /// 「已加载」要求存在激活文档路径且阅读实例已完成加载（<c>HasDocument</c>）；加载中 / 无文档 → 未加载。
    /// 「加密」取阅读实例的真实 <c>IsEncrypted</c>（V4 Option 3：加密且已解锁为 true，加密锁定亦为 true，
    /// 普通文档为 false）——不再以「会话是否驻留密码」推断加密，否则锁定的加密标签会被误判为普通文档。
    /// 是否可打印仍由 T3 裁决：加密且 <see cref="SessionCredentialStore"/> 无该路径密码 → Locked。
    /// </para>
    /// </summary>
    /// <param name="service">T3 打印服务（门禁判定唯一来源）。</param>
    /// <param name="documentPath">激活文档标签路径（可为 null）。</param>
    /// <param name="readerHasDocument">激活文档的阅读实例是否已完成加载（锁定标签亦视为「已就绪」，由加密/凭据裁决）。</param>
    /// <param name="isDocumentEncrypted">该文档是否为加密文档（阅读实例 <c>IsEncrypted</c>，真实信号）。</param>
    public static PrintGateDecision EvaluateGate(
        PrintService service,
        string? documentPath,
        bool readerHasDocument,
        bool isDocumentEncrypted)
    {
        ArgumentNullException.ThrowIfNull(service);
        var isLoaded = readerHasDocument && !string.IsNullOrWhiteSpace(documentPath);
        return service.EvaluateGate(documentPath, isLoaded, isDocumentEncrypted);
    }

    /// <summary>
    /// 把系统 <see cref="PrintDialog"/> 的范围选择映射为 T3 <see cref="PrintPageRange"/> 结果。
    /// <paramref name="rangeFrom"/> / <paramref name="rangeTo"/> 为对话框语义的 1 基页码（仅 UserPages 使用）。
    /// </summary>
    /// <param name="selection">对话框范围选择（全部页 / 当前页 / 自定义）。</param>
    /// <param name="rangeFrom">自定义范围起始页（1 基）。</param>
    /// <param name="rangeTo">自定义范围结束页（1 基）。</param>
    /// <param name="pageCount">文档总页数。</param>
    /// <param name="currentPageIndex">当前页（0 基）。</param>
    public static PrintPageRangeResult ResolveRange(
        PageRangeSelection selection,
        int rangeFrom,
        int rangeTo,
        int pageCount,
        int currentPageIndex)
        => selection switch
        {
            PageRangeSelection.UserPages => PrintPageRange.Parse($"{rangeFrom}-{rangeTo}", pageCount),
            PageRangeSelection.CurrentPage => PrintPageRange.CurrentPage(pageCount, currentPageIndex),
            _ => PrintPageRange.All(pageCount),
        };
}
