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
    public const string SpreadSinglePageLabel = "单页";
    public const string SpreadTwoPageLabel = "双页";
    public const string SpreadToggleToolTip = "切换单页 / 双页对开阅读（对开按封面单列分组）";
    public const string LoadErrorPrefix = "无法打开文档：";
    public const string RenderErrorPrefix = "页面渲染失败：";

    // ---- T9 加密文档密码解锁（【ST-04】）：错误行内红字、不清空输入、不限次数 ----
    public const string PasswordDialogTitle = "输入密码";
    public const string PasswordDialogHint = "此 PDF 已加密，请输入打开密码。";
    public const string PasswordDialogEmptyError = "请输入密码";
    public const string PasswordDialogWrongError = "密码错误，请重试";
    public const string PasswordDialogUnlock = "解锁";

    // ---- T2 选中浮菜单 / 悬停操作 / 笔记 ----
    public const string MenuCopy = "复制";
    public const string MenuHighlight = "高亮";
    public const string MenuNote = "笔记";
    public const string MenuTools = "用工具处理 ▾";
    public const string MenuMoreTools = "更多工具…";
    public const string MenuBack = "返回";
    public const string HoverEdit = "编辑";
    public const string HoverDelete = "删除";
    public const string HoverRecolor = "改色";
    public const string ColorPanelTitle = "选择高亮颜色";
    public const string NoteEditorTitle = "输入笔记内容";
    public const string Confirm = "确定";
    public const string Cancel = "取消";
    public const string Copied = "已复制";
    public const string NoteApplied = "已添加笔记";
    public const string HighlightApplied = "已高亮";
    public const string AnnotationDeleted = "已删除批注";
    public const string ToolUnavailable = "不可用";
}
