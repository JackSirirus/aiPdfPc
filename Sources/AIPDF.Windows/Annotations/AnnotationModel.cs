// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 阅读器批注模型（T1 地基）：高亮 / 文字笔记的类型、状态与记录。
//
// 坐标口径：矩形为 PDF 点、左上原点，与 Controls/PdfCoordinateMapper 及 backend/engine.py 的
// area() 完全一致（可见旋转页、左上原点、pt 单位）。本文件为纯模型，不触碰 PDFium、不写回。
//
// 旋转页决策（M-11，详见 ReaderViewModel.CanAnnotate 与 .omo/evidence/v2-task1-annotrender.txt）：
//   PdfCoordinateMapper 无 /Rotate 变换，PdfiumTextService.GetTextRects 输出未旋转页面空间矩形，
//   新增 derotation 需 X/Y 交换与引擎 derotation_matrix 对齐，超出 T1 范围且有坐标漂移风险。
//   V2 决定【禁止旋转页选区/批注】；本模型不承载旋转信息，旋转判定在 ReaderViewModel（由渲染服务取 /Rotate）。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Annotations;

/// <summary>批注类型。V2 仅高亮与文字笔记；手写/形状为 PRD 排除项。</summary>
public enum AnnotationKind
{
    /// <summary>文字高亮（选区矩形，默认黄）。</summary>
    Highlight,

    /// <summary>文字笔记（高亮 + 文本标记）。</summary>
    Note,
}

/// <summary>批注保存状态。T1 仅建模；T4 防抖保存链路驱动的状态迁移在客户端。</summary>
public enum AnnotationState
{
    /// <summary>仅存在于客户端叠加层，尚未提交保存。</summary>
    Unsaved,

    /// <summary>保存请求在途。</summary>
    Saving,

    /// <summary>已成功写回 PDF。</summary>
    Saved,

    /// <summary>保存失败（重试入口由 T4 提供）。</summary>
    Failed,
}

/// <summary>
/// 客户端批注记录（唯一真相源【FR-20】：先写客户端叠加层，PDF 内嵌注释为渲染备份）。
/// 矩形单位为 PDF 点、左上原点，复用 <see cref="PdfRect"/>，不新增坐标类型。
/// </summary>
public sealed record AnnotationRecord
{
    /// <summary>高亮默认填充色（FR-13：默认黄 #FFFF00）。</summary>
    public const string DefaultHighlightColor = "#FFFF00";

    /// <summary>批注唯一标识（客户端生成，T3/T4 写回时映射到 PDF 注释 xref）。</summary>
    public required string Id { get; init; }

    /// <summary>页索引（0 起）。</summary>
    public required int PageIndex { get; init; }

    /// <summary>页内矩形（PDF 点，左上原点）。</summary>
    public required PdfRect Rect { get; init; }

    /// <summary>批注类型。</summary>
    public AnnotationKind Kind { get; init; } = AnnotationKind.Highlight;

    /// <summary>填充色（#RRGGBB，高亮默认见 <see cref="DefaultHighlightColor"/>）。</summary>
    public string Color { get; init; } = DefaultHighlightColor;

    /// <summary>笔记文本（<see cref="AnnotationKind.Highlight"/> 为 null）。</summary>
    public string? Note { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>保存状态（T1 恒为 Unsaved，T4 驱动迁移）。</summary>
    public AnnotationState State { get; init; } = AnnotationState.Unsaved;

    /// <summary>新建高亮记录（默认黄）。</summary>
    public static AnnotationRecord CreateHighlight(int pageIndex, PdfRect rect, string? color = null)
        => new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = rect,
            Kind = AnnotationKind.Highlight,
            Color = color ?? DefaultHighlightColor,
        };

    /// <summary>新建文字笔记记录。空笔记抛 <see cref="ArgumentException"/>（T2 输入框校验）。</summary>
    public static AnnotationRecord CreateNote(int pageIndex, PdfRect rect, string note, string? color = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = rect,
            Kind = AnnotationKind.Note,
            Color = color ?? DefaultHighlightColor,
            Note = note,
        };
    }

    /// <summary>返回状态迁移后的副本（T4 保存链路用；只读记录不可变）。</summary>
    public AnnotationRecord WithState(AnnotationState state) => this with { State = state };

    /// <summary>返回改色后的副本（FR-13 改色）。</summary>
    public AnnotationRecord WithColor(string color) => this with { Color = color };

    private static string NewId() => Guid.NewGuid().ToString("N");
}
