// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译事件载荷：视图只把「要翻译的选中文本 + 所在页 + 选区矩形」交给宿主，
// 请求编排 / 配置 / sidecar 均留在宿主层（视图不接触翻译服务与网络）。
// T19：附带 PDF 点坐标（左上原点）供「替换标注」写入 sidecar 后按页叠加显示。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Views.Reader;

/// <summary>点击浮出「译」按钮时抛出的选中文本。</summary>
public sealed class SelectionTranslationEventArgs : EventArgs
{
    public SelectionTranslationEventArgs(string text, int pageIndex, PdfRect rect = default)
    {
        Text = text;
        PageIndex = pageIndex;
        Rect = rect;
    }

    /// <summary>已去除首尾空白的选中文本（≥2 字）。</summary>
    public string Text { get; }

    /// <summary>选区所在页（0 起；未知为 -1）。</summary>
    public int PageIndex { get; }

    /// <summary>选区包围盒（PDF 点、左上原点；未知为 default）。「替换标注」据此定位 sidecar 叠加。</summary>
    public PdfRect Rect { get; }
}
