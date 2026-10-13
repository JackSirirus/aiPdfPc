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
using System.Collections.Generic;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Annotations;

/// <summary>批注类型。V2 为高亮/文字笔记；V3 T11（C3）新增手写与几何形状。</summary>
public enum AnnotationKind
{
    /// <summary>文字高亮（选区矩形，默认黄）。</summary>
    Highlight,

    /// <summary>文字笔记（高亮 + 文本标记）。</summary>
    Note,

    /// <summary>手写笔迹（多点折线，可多段；几何承载见 <see cref="AnnotationRecord.Strokes"/>）。</summary>
    Ink,

    /// <summary>几何形状（矩形/圆/线/箭头；具体见 <see cref="ShapeKind"/>）。</summary>
    Shape,

    /// <summary>文字下划线（文本标记；逐行几何见 <see cref="AnnotationRecord.Quads"/>）。V4 T3。</summary>
    Underline,

    /// <summary>文字删除线（文本标记；逐行几何见 <see cref="AnnotationRecord.Quads"/>）。V4 T3。</summary>
    Strikeout,

    /// <summary>图章（预置文字图章或图片图章）。V4 T4。</summary>
    Stamp,
}

/// <summary>单个预置图章（token 下发引擎，Label 供 UI/叠加层显示）。</summary>
public sealed record StampPreset(string Token, string Label);

/// <summary>
/// 预置图章表（V4 T4）：token 与引擎侧 <c>STAMP_PRESETS</c> 一一对应（共 14 种）。
/// 图片图章不走此表。
/// </summary>
public static class StampPresets
{
    /// <summary>预置图章默认颜色（红），与引擎 <c>add_stamp_annot</c> 默认一致。</summary>
    public const string DefaultColor = "#E11D48";

    /// <summary>预置图章有序表；顺序与引擎 STAMP_PRESETS 一致。</summary>
    public static IReadOnlyList<StampPreset> All { get; } =
    [
        new("approved", "APPROVED"),
        new("asis", "AS IS"),
        new("confidential", "CONFIDENTIAL"),
        new("departmental", "DEPARTMENTAL"),
        new("draft", "DRAFT"),
        new("experimental", "EXPERIMENTAL"),
        new("expired", "EXPIRED"),
        new("final", "FINAL"),
        new("forcomment", "FOR COMMENT"),
        new("forpublicrelease", "FOR PUBLIC RELEASE"),
        new("notapproved", "NOT APPROVED"),
        new("notforpublicrelease", "NOT FOR PUBLIC RELEASE"),
        new("sold", "SOLD"),
        new("topsecret", "TOP SECRET"),
    ];

    /// <summary>token → 显示名；未知 token 原样大写返回（不抛错，仅用于预览）。</summary>
    public static string LabelFor(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "STAMP";
        }

        foreach (var preset in All)
        {
            if (string.Equals(preset.Token, token, StringComparison.OrdinalIgnoreCase))
            {
                return preset.Label;
            }
        }

        return token.ToUpperInvariant();
    }
}

/// <summary>
/// 几何形状类型（PRD 6.3 「形状」）。几何承载模型（显式）：
/// <list type="bullet">
///   <item><see cref="Square"/>/<see cref="Circle"/>：一个 <see cref="PdfRect"/>（外接矩形）。</item>
///   <item><see cref="Line"/>/<see cref="Arrow"/>：恰好两点（起点/终点，<see cref="AnnotationRecord.LinePoints"/>），
///         方向恒为起点→终点，无二义（尖刺 #4 定稿：PyMuPDF Line 注释 + OPEN_ARROW 线端）。</item>
/// </list>
/// 坐标口径与 <see cref="AnnotationRecord.Rect"/> 一致：可见旋转页、左上原点、PDF 点。
/// </summary>
public enum ShapeKind
{
    /// <summary>矩形（外接矩形描边）。</summary>
    Square,

    /// <summary>圆/椭圆（外接矩形内切）。</summary>
    Circle,

    /// <summary>线段（起点→终点）。</summary>
    Line,

    /// <summary>箭头（起点→终点，终点带 OPEN_ARROW 箭头）。</summary>
    Arrow,
}

/// <summary>PDF 点（可见旋转页、左上原点、pt 单位）。与 <see cref="PdfRect"/> 同坐标口径，供手写/线几何承载。</summary>
public readonly record struct PdfPoint(double X, double Y);

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

    /// <summary>手写/形状默认描边色（黑色；UI 会按所选色板显式传入）。</summary>
    public const string DefaultInkColor = "#000000";

    /// <summary>默认线宽（pt，坐标口径与形状一致）。</summary>
    public const double DefaultLineWidth = 2.0;

    /// <summary>允许的最小线宽（避免 0 宽导致注释不可见）。</summary>
    public const double MinLineWidth = 0.5;

    /// <summary>允许的最大线宽。</summary>
    public const double MaxLineWidth = 24.0;

    /// <summary>批注唯一标识（客户端生成，T3/T4 写回时映射到 PDF 注释 xref）。</summary>
    public required string Id { get; init; }

    /// <summary>页索引（0 起）。</summary>
    public required int PageIndex { get; init; }

    /// <summary>页内矩形（PDF 点，左上原点）。手写/线/箭头为其几何的包围盒；矩形/圆即本身。</summary>
    public required PdfRect Rect { get; init; }

    /// <summary>批注类型。</summary>
    public AnnotationKind Kind { get; init; } = AnnotationKind.Highlight;

    /// <summary>填充色/描边色（#RRGGBB，高亮默认见 <see cref="DefaultHighlightColor"/>）。</summary>
    public string Color { get; init; } = DefaultHighlightColor;

    /// <summary>笔记文本（<see cref="AnnotationKind.Highlight"/> 为 null）。</summary>
    public string? Note { get; init; }

    /// <summary>形状类型（仅 <see cref="AnnotationKind.Shape"/> 有值）。</summary>
    public ShapeKind? Shape { get; init; }

    /// <summary>线/箭头几何：恰好两点（起点、终点）。其他类型为 null。</summary>
    public IReadOnlyList<PdfPoint>? LinePoints { get; init; }

    /// <summary>手写几何：多段折线（每段为点序列）。仅 <see cref="AnnotationKind.Ink"/> 有值。</summary>
    public IReadOnlyList<IReadOnlyList<PdfPoint>>? Strokes { get; init; }

    /// <summary>
    /// 文本标记的逐行矩形（<see cref="AnnotationKind.Highlight"/> /
    /// <see cref="AnnotationKind.Underline"/> / <see cref="AnnotationKind.Strikeout"/>）。
    /// 多行选区每行一个矩形；单行/历史记录可为 null（此时退化为 <see cref="Rect"/> 单矩形）。
    /// <see cref="Rect"/> 恒为这些矩形的并集（命中测试与叠加层外接框）。V4 T3。
    /// </summary>
    public IReadOnlyList<PdfRect>? Quads { get; init; }

    /// <summary>图章 token（预置图章；图片图章为 null）。V4 T4。</summary>
    public string? StampToken { get; init; }

    /// <summary>图章图片字节（图片图章；预置图章为 null）。V4 T4。</summary>
    public byte[]? StampImage { get; init; }

    /// <summary>
    /// 父批注 Id（V4 T10：仅回复有值）。保存时下发为 <c>kind:"reply"</c> + <c>parent</c>，
    /// 引擎据此写 PDF 标准 <c>/IRT</c> 关联键（其它阅读器可读，不需要 sidecar）。
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>描边线宽（pt；手写与形状使用）。</summary>
    public double LineWidth { get; init; } = DefaultLineWidth;

    /// <summary>
    /// 不透明度（0.05 ~ 1.0）。V4 T9 属性面板可调：文本标记默认 0.5（与引擎
    /// <c>add_text_markup_annot</c> 一致），手写/形状默认 1.0。
    /// </summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>保存状态（T1 恒为 Unsaved，T4 驱动迁移）。</summary>
    public AnnotationState State { get; init; } = AnnotationState.Unsaved;

    /// <summary>新建高亮记录（默认黄）。单矩形不落 <see cref="Quads"/>，保持 V2 载荷与渲染契约。</summary>
    public static AnnotationRecord CreateHighlight(int pageIndex, PdfRect rect, string? color = null)
        => new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = rect,
            Kind = AnnotationKind.Highlight,
            Color = color ?? DefaultHighlightColor,
            // 高亮默认半透明：与引擎 add_text_markup_annot 的 0.5 及叠加层的 0x66 填充一致
            Opacity = 0.5,
        };

    /// <summary>
    /// 新建文本标记记录（Highlight / Underline / Strikeout）。V4 T3。
    /// <para>
    /// <paramref name="quads"/> 为**逐行**矩形（每行一个）：多行选区必须逐行传入。把多行压成
    /// 单个包围盒会让渲染退化成一条跨行实心带（引擎侧实测见
    /// <c>.omo/evidence/v4-spike-underline.md</c>），故 <see cref="Rect"/> 取并集仅供命中测试，
    /// 真正下发给引擎的几何是 <see cref="Quads"/>。
    /// </para>
    /// </summary>
    public static AnnotationRecord CreateTextMarkup(
        AnnotationKind kind, int pageIndex, IReadOnlyList<PdfRect> quads, string? color = null)
    {
        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count == 0)
        {
            throw new ArgumentException("文本标记至少需要一个矩形。", nameof(quads));
        }

        if (kind is not (AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.Strikeout))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "不是文本标记类型。");
        }

        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = AnnotationGeometry.Union(quads),
            // 仅多行才落 Quads：单矩形与 V2 记录等价，保持既有载荷/渲染契约不变
            // （叠加层的单矩形路径继续走 BoxLayer）。
            Quads = quads.Count > 1 ? quads : null,
            Kind = kind,
            Color = color ?? DefaultHighlightColor,
            // 文本标记默认半透明（与引擎 add_text_markup_annot 的 0.5 一致）
            Opacity = 0.5,
        };
    }

    /// <summary>
    /// 新建图章记录（V4 T4）。预置图章给 <paramref name="token"/>，图片图章给
    /// <paramref name="image"/>（两者都给时以图片为准）。二者皆空抛 <see cref="ArgumentException"/>。
    /// </summary>
    public static AnnotationRecord CreateStamp(
        int pageIndex, PdfRect rect, string? token = null, byte[]? image = null, string? color = null)
    {
        if (image is null && string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("图章需要预置 token 或图片。", nameof(token));
        }

        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = rect,
            Kind = AnnotationKind.Stamp,
            Color = color ?? StampPresets.DefaultColor,
            StampToken = image is null ? token : null,
            StampImage = image,
        };
    }

    /// <summary>
    /// 新建回复记录（V4 T10）：在 PDF 里是锚定到父批注的 FreeText + 标准 <c>/IRT</c> 关联键。
    /// 复用 <see cref="AnnotationKind.Note"/> 的渲染（回复就是一条带父链接的笔记）。
    /// </summary>
    public static AnnotationRecord CreateReply(
        int pageIndex, PdfRect rect, string parentId, string text, string? color = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = rect,
            Kind = AnnotationKind.Note,
            Color = color ?? DefaultHighlightColor,
            Note = text,
            ParentId = parentId,
        };
    }

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

    /// <summary>新建手写批注记录（V3 T11）。空/退化笔迹抛 <see cref="ArgumentException"/>。</summary>
    public static AnnotationRecord CreateInk(
        int pageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, string? color = null,
        double lineWidth = DefaultLineWidth)
    {
        var normalized = AnnotationGeometry.NormalizeStrokes(strokes);
        if (normalized.Count == 0)
        {
            throw new ArgumentException("手写批注至少需要一个笔迹点。", nameof(strokes));
        }

        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = AnnotationGeometry.BoundingBox(normalized),
            Kind = AnnotationKind.Ink,
            Color = color ?? DefaultInkColor,
            Strokes = normalized,
            LineWidth = ClampLineWidth(lineWidth),
        };
    }

    /// <summary>新建矩形/圆形状记录（V3 T11）。线/箭头请用两点重载。</summary>
    public static AnnotationRecord CreateShape(
        int pageIndex, ShapeKind shape, PdfRect rect, string? color = null,
        double lineWidth = DefaultLineWidth)
    {
        if (shape is ShapeKind.Line or ShapeKind.Arrow)
        {
            throw new ArgumentException("线/箭头形状需使用两点重载。", nameof(shape));
        }

        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = AnnotationGeometry.NormalizeRect(rect),
            Kind = AnnotationKind.Shape,
            Shape = shape,
            Color = color ?? DefaultInkColor,
            LineWidth = ClampLineWidth(lineWidth),
        };
    }

    /// <summary>新建线/箭头形状记录（V3 T11）：恰好两点，方向 = 起点→终点（无二义）。</summary>
    public static AnnotationRecord CreateShape(
        int pageIndex, ShapeKind shape, PdfPoint start, PdfPoint end, string? color = null,
        double lineWidth = DefaultLineWidth)
    {
        if (shape is not (ShapeKind.Line or ShapeKind.Arrow))
        {
            throw new ArgumentException("矩形/圆形状需使用 PdfRect 重载。", nameof(shape));
        }

        var points = new[] { start, end };
        return new()
        {
            Id = NewId(),
            PageIndex = pageIndex,
            Rect = AnnotationGeometry.BoundingBox(points),
            Kind = AnnotationKind.Shape,
            Shape = shape,
            Color = color ?? DefaultInkColor,
            LinePoints = points,
            LineWidth = ClampLineWidth(lineWidth),
        };
    }

    /// <summary>返回状态迁移后的副本（T4 保存链路用；只读记录不可变）。</summary>
    public AnnotationRecord WithState(AnnotationState state) => this with { State = state };

    /// <summary>
    /// 内容等价（T4 H-4 粒度判定）：比较 Id/页/矩形/类型/颜色/文本，**忽略 State/CreatedAt**。
    /// 用于「保存期间该记录是否被修改」——State 会被保存链路置为 Saving/Failed，不能参与判定。
    /// </summary>
    public bool ContentEquals(AnnotationRecord? other)
        => other is not null
           && Id == other.Id
           && PageIndex == other.PageIndex
           && Rect == other.Rect
           && Kind == other.Kind
           && string.Equals(Color, other.Color, StringComparison.Ordinal)
           && string.Equals(Note, other.Note, StringComparison.Ordinal)
           && Shape == other.Shape
           && LineWidth.Equals(other.LineWidth)
           && AnnotationGeometry.PointsEqual(LinePoints, other.LinePoints)
           && AnnotationGeometry.StrokesEqual(Strokes, other.Strokes)
           && AnnotationGeometry.RectsEqual(Quads, other.Quads)
           && Opacity.Equals(other.Opacity)
           && string.Equals(StampToken, other.StampToken, StringComparison.Ordinal)
           && string.Equals(ParentId, other.ParentId, StringComparison.Ordinal)
           && AnnotationGeometry.BytesEqual(StampImage, other.StampImage);

    /// <summary>返回改色后的副本（FR-13 改色）。</summary>
    public AnnotationRecord WithColor(string color) => this with { Color = color };

    /// <summary>返回改线宽后的副本（V4 T9 属性面板；钳制到既有上下限）。</summary>
    public AnnotationRecord WithLineWidth(double width)
        => this with { LineWidth = Math.Clamp(width, MinLineWidth, MaxLineWidth) };

    /// <summary>返回改不透明度后的副本（V4 T9 属性面板；钳制到 0.05~1.0）。</summary>
    public AnnotationRecord WithOpacity(double opacity)
        => this with { Opacity = Math.Clamp(opacity, 0.05, 1.0) };

    /// <summary>返回笔记文本更新后的副本（FR-13 编辑笔记，Id 不变）。</summary>
    public AnnotationRecord WithNote(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        return this with { Note = note };
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>线宽归一化：越界钳制到 [<see cref="MinLineWidth"/>, <see cref="MaxLineWidth"/>]，NaN → 默认。</summary>
    private static double ClampLineWidth(double lineWidth)
        => double.IsNaN(lineWidth)
            ? DefaultLineWidth
            : Math.Clamp(lineWidth, MinLineWidth, MaxLineWidth);
}
