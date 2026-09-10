// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 阅读视图 UI 资源字符串集中放置：文案统一从这里取，禁止散落在代码各处。
namespace AIPDF.Windows.Views.Reader;

/// <summary>阅读视图 UI 文案（简体中文）。集中管理，便于审阅与后续本地化。</summary>
public static class ReaderStrings
{
    public const string EmptyStateTitle = "打开 PDF 文件开始阅读";
    public const string EmptyStateHint = "可从起始页选择文档，或使用标签栏的 ＋ 按钮。";
    public const string PagePrefix = "第";
    public const string PagePlaceholderFormat = "第 {0} 页";
    public const string RenderingHint = "渲染中…";
    public const string JumpBoxToolTip = "输入页码后按回车跳转（Ctrl+G 定位输入框）";
    public const string JumpInvalid = "请输入有效页码";
    public const string ZoomInToolTip = "放大";
    public const string ZoomOutToolTip = "缩小";
    public const string FitWidthLabel = "适配宽度";
    public const string FitPageLabel = "适配页面";
    public const string FitWidthToolTip = "按窗口宽度重算缩放";
    public const string FitPageToolTip = "按窗口大小完整显示一页";
    public const string LoadErrorPrefix = "无法打开文档：";
    public const string RenderErrorPrefix = "页面渲染失败：";
}
