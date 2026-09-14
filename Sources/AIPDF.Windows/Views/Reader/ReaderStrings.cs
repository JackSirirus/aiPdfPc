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

    // ---- V4 Option 3：加密文档锁定标签（未解锁不打开/不解密/不渲染；打印门禁同步禁用） ----
    public const string LockedTitle = "此文档已锁定";
    public const string LockedHint = "此 PDF 已加密。解锁后才能查看和打印内容；锁定期间不会保留任何解密内容。";
    public const string UnlockButton = "解锁";
    public const string RelockButton = "锁定";
    public const string LockedStatusLabel = "已锁定";
    public const string UnlockedStatusLabel = "已解锁";
    public const string LockedSearchBlocked = "文档已锁定，请先解锁后再使用搜索。";

    // ---- T2 选中浮菜单 / 悬停操作 / 笔记 ----
    public const string MenuCopy = "复制";    public const string MenuHighlight = "高亮";
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

    // ---- v1.1 T14 划词翻译（浮出「译」按钮 + F1b 无文本层态） ----
    public const string MenuTranslate = "翻译选中文字";
    public const string TranslateToolTip = "翻译选中文字（点击才发送，不自动翻译）";
    public const string TranslateTooShort = "请先选中至少 2 个字";
    public const string NoTextLayerTitle = "本页无文本层";
    public const string NoTextLayerHint = "此页是扫描图片，没有可选文字，划词翻译不可用。可改用整篇翻译（先 OCR 该页），或先对本页 OCR。";

    // ---- V3 T11（C3）手写/形状绘制工具栏 ----
    public const string DrawToolbarToolTip = "开启后在页面上拖拽绘制手写或形状；关闭后恢复文字选中";
    public const string DrawModeLabel = "批注";
    public const string DrawInkLabel = "手写";
    public const string DrawSquareLabel = "矩形";
    public const string DrawCircleLabel = "圆";
    public const string DrawLineLabel = "线";
    public const string DrawArrowLabel = "箭头";
    public const string DrawColorTitle = "颜色";
    public const string DrawWidthTitle = "粗细";
    public const string DrawThinLabel = "细";
    public const string DrawMidLabel = "中";
    public const string DrawThickLabel = "粗";
    public const string InkApplied = "已添加手写";
    public const string ShapeApplied = "已添加形状";
}
