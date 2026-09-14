// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T19 替换标注叠加层投影：sidecar 标注（PDF 点）→ 页内 DIP 叠加条目（供 ReaderView 独立 Canvas 层消费）。
// 换算复用 Views/Reader/PageLayoutCalculator.ToPageDip（与搜索高亮/批注同一唯一函数），不新增坐标换算层。
// 独立于批注 <see cref="AnnotationOverlay"/>：只读叠加，绝不进入批注保存管线、绝不写回源 PDF。
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using AIPDF.Windows.Views.Reader;

namespace AIPDF.Windows.Annotations;

/// <summary>
/// 单条替换标注的页内显示叠加（DIP，相对页左上角）。XAML 以 Canvas 绝对定位消费，
/// 独立 <c>IsHitTestVisible=False</c> 层；译文文本以气泡形式绘制在原文矩形上。
/// </summary>
public readonly record struct TranslationOverlay(
    double X,
    double Y,
    double Width,
    double Height,
    string Text,
    string LangPair)
{
    /// <summary>
    /// sidecar 标注（PDF 点）→ 叠加条目（DIP）。缩放变更时按新显示宽重算（调用方在
    /// <c>ReaderPageItemViewModel.RebuildTranslationOverlays</c>）。退化输入返回零矩形但仍带文本。
    /// </summary>
    public static TranslationOverlay FromAnnotation(
        SelectionSidecarAnnotation annotation, double pageWidthPt, double displayWidthDip)
    {
        var dip = PageLayoutCalculator.ToPageDip(annotation.Rect, pageWidthPt, displayWidthDip);
        return new TranslationOverlay(
            dip.X, dip.Y, dip.Width, dip.Height, annotation.Translation ?? string.Empty, annotation.LangPair ?? string.Empty);
    }
}
