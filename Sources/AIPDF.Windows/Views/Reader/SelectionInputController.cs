// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 选中交互纯逻辑：拖选生命周期 + 禁用判定（无文本层【ST-23】/旋转页【M-11】/跨页）+
// 拖选矩形归一。无 WPF 依赖，xunit 直接覆盖；坐标均为「页容器内 DIP」（页左上原点）。
namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 选区禁用原因。判定优先级与 T2 规格一致：
/// 无文档 → 旋转页（T1 CanAnnotate=false，禁选）→ 无文本层（可拉矩形但不弹菜单【ST-23】）→ 启用。
/// </summary>
public enum SelectionGate
{
    /// <summary>选区可用：有文本层且非旋转页，释放后弹浮菜单。</summary>
    Enabled,

    /// <summary>未打开文档，输入被忽略。</summary>
    DisabledNoDocument,

    /// <summary>旋转页（/Rotate ≠ 0）：T1 CanAnnotate=false，V2 禁选（M-11/BA-8）。</summary>
    DisabledRotated,

    /// <summary>无文本层页：可拖出矩形，但释放不弹菜单（ST-23）。</summary>
    DisabledNoTextLayer,
}

/// <summary>一次拖选的完成结果。坐标为页容器内 DIP（页左上原点）。</summary>
public readonly record struct SelectionResult(
    bool HasSelection,
    bool CanShowMenu,
    bool IsCrossPage,
    SelectionGate Gate,
    int PageIndex,
    double StartX,
    double StartY,
    double EndX,
    double EndY)
{
    /// <summary>选区归一矩形（左上原点，宽高非负）。无选区时为零矩形。</summary>
    public (double Left, double Top, double Width, double Height) Rect
    {
        get
        {
            if (!HasSelection) return (0, 0, 0, 0);
            var left = Math.Min(StartX, EndX);
            var top = Math.Min(StartY, EndY);
            return (left, top, Math.Abs(EndX - StartX), Math.Abs(EndY - StartY));
        }
    }
}

/// <summary>
/// 拖选输入状态机。视图在鼠标按下/移动/抬起时调用 Begin/Update/Complete；
/// 全部判定（禁用、跨页、最小拖拽距离）均为纯逻辑，便于单测。
/// </summary>
public sealed class SelectionInputController
{
    /// <summary>最小有效拖拽距离（DIP）：小于此值视为点击，不产生选区。</summary>
    public const double MinDrag = 2.0;

    /// <summary>拖拽进行中。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>锚点页（0 起）。</summary>
    public int AnchorPageIndex { get; private set; } = -1;

    /// <summary>当前指针页（0 起）。</summary>
    public int CurrentPageIndex { get; private set; } = -1;

    /// <summary>是否已跨页（锚点页 ≠ 当前页）。</summary>
    public bool IsCrossPage { get; private set; }

    /// <summary>本次拖拽的禁用判定（Begin 时确定）。</summary>
    public SelectionGate Gate { get; private set; } = SelectionGate.DisabledNoDocument;

    /// <summary>锚点与当前点（页容器内 DIP）。</summary>
    public double AnchorX { get; private set; }
    public double AnchorY { get; private set; }
    public double CurrentX { get; private set; }
    public double CurrentY { get; private set; }

    /// <summary>
    /// 开始拖选。返回 false 表示该页不应产生任何选区（无文档 / 旋转页禁选）。
    /// <paramref name="hasTextLayer"/> 应由「当页 CountPageChars(page) &gt; 0」即时判定（H-5，不依赖惰性索引）。
    /// </summary>
    public bool Begin(int pageIndex, double x, double y, bool hasDocument, bool canAnnotate, bool hasTextLayer)
    {
        AnchorPageIndex = -1;
        CurrentPageIndex = -1;
        IsCrossPage = false;
        IsDragging = false;

        if (!hasDocument || pageIndex < 0)
        {
            Gate = SelectionGate.DisabledNoDocument;
            return false;
        }

        if (!canAnnotate)
        {
            Gate = SelectionGate.DisabledRotated;
            return false;
        }

        Gate = hasTextLayer ? SelectionGate.Enabled : SelectionGate.DisabledNoTextLayer;
        IsDragging = true;
        AnchorPageIndex = pageIndex;
        CurrentPageIndex = pageIndex;
        AnchorX = x;
        AnchorY = y;
        CurrentX = x;
        CurrentY = y;
        return true;
    }

    /// <summary>拖拽移动。指针进入别的页时标记跨页（释放不弹菜单）。</summary>
    public void Update(int pageIndex, double x, double y)
    {
        if (!IsDragging) return;
        if (pageIndex != AnchorPageIndex) IsCrossPage = true;
        CurrentPageIndex = pageIndex;
        CurrentX = x;
        CurrentY = y;
    }

    /// <summary>
    /// 由视图显式标记跨页（鼠标捕获下所有 MouseMove 都路由到锚点元素、pageIndex 不变，
    /// 故视图按指针是否越出锚点页纵向边界判定，见 ReaderView.OnPageMouseMove）。
    /// </summary>
    public void MarkCrossPage()
    {
        if (IsDragging) IsCrossPage = true;
    }

    /// <summary>结束拖选并产出结果（不弹菜单的场景：旋转/无文本层/跨页/拖拽距离过小）。</summary>
    public SelectionResult Complete()
    {
        if (!IsDragging)
        {
            return new SelectionResult(false, false, IsCrossPage, Gate, AnchorPageIndex, AnchorX, AnchorY, CurrentX, CurrentY);
        }

        IsDragging = false;

        var samePage = !IsCrossPage && CurrentPageIndex == AnchorPageIndex;
        var dx = Math.Abs(CurrentX - AnchorX);
        var dy = Math.Abs(CurrentY - AnchorY);
        var hasSelection = samePage
            && Gate is not (SelectionGate.DisabledNoDocument or SelectionGate.DisabledRotated)
            && (dx >= MinDrag || dy >= MinDrag);
        var canShowMenu = hasSelection && Gate == SelectionGate.Enabled;

        return new SelectionResult(
            hasSelection, canShowMenu, IsCrossPage, Gate,
            AnchorPageIndex, AnchorX, AnchorY, CurrentX, CurrentY);
    }

    /// <summary>取消拖拽（Esc / 点击外部 / 滚动），不产出选区。</summary>
    public void Cancel()
    {
        IsDragging = false;
        IsCrossPage = false;
        AnchorPageIndex = -1;
        CurrentPageIndex = -1;
    }
}
