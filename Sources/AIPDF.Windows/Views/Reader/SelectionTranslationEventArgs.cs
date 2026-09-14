// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译事件载荷：视图只把「要翻译的选中文本 + 所在页」交给宿主，
// 请求编排 / 配置 / sidecar 均留在宿主层（视图不接触翻译服务与网络）。
namespace AIPDF.Windows.Views.Reader;

/// <summary>点击浮出「译」按钮时抛出的选中文本。</summary>
public sealed class SelectionTranslationEventArgs : EventArgs
{
    public SelectionTranslationEventArgs(string text, int pageIndex)
    {
        Text = text;
        PageIndex = pageIndex;
    }

    /// <summary>已去除首尾空白的选中文本（≥2 字）。</summary>
    public string Text { get; }

    /// <summary>选区所在页（0 起；未知为 -1）。</summary>
    public int PageIndex { get; }
}
