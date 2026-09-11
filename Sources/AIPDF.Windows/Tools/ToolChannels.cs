// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using AIPDF.Windows.Models;

namespace AIPDF.Windows.Tools;

/// <summary>
/// V1 工具通道。V1 只有两个实际宿主：<see cref="RightDrawer"/> 与 <see cref="TempTab"/>；
/// 起始页工具在 V1 也统一落到临时标签承载（起始页只是入口，不是独立宿主）。
/// </summary>
public enum ToolChannel
{
    /// <summary>无 PDF 输入的工具：从起始页发起，V1 以临时标签承载。</summary>
    StartPage,

    /// <summary>单文件工具：主窗口右侧抽屉内联参数面板【D7】。</summary>
    RightDrawer,

    /// <summary>其余工具：打开现有工具页作为临时标签承载【D16】。</summary>
    TempTab,
}

/// <summary>
/// V1 工具通道判定（权威数据源：设计/v1.0-阅读器/prd/01-工具通道映射表.md，四路评审）。
/// <para>
/// id 一律显式编码，刻意不从 <see cref="Tool.Multiple"/> 推导：映射表与 Multiple 并不一致，
/// 例如 <c>pdfa</c> 的 Multiple=true 却属右抽屉，<c>forms</c> 的 Multiple=false 却属临时标签。
/// </para>
/// <para>
/// 分布（01 表）：右抽屉 2（organize / pdfa）+ 起始页 6 + 其余 24 = 32。
/// V1 不实现浮菜单（选中浮菜单不在 V1 范围）。
/// </para>
/// </summary>
public static class ToolChannels
{
    /// <summary>右抽屉工具 id（01-工具通道映射表.md「右抽屉」行：organize / pdfa，判据 :58 单文件）。</summary>
    private static readonly HashSet<string> RightDrawerIds = new(StringComparer.Ordinal)
    {
        "organize",
        "pdfa",
    };

    /// <summary>起始页工具 id（01-工具通道映射表.md「起始页」行：extensions 不含 pdf，判据 :39 无 PDF 输入）。</summary>
    private static readonly HashSet<string> StartPageIds = new(StringComparer.Ordinal)
    {
        "scan",
        "images_to_pdf",
        "word_to_pdf",
        "ppt_to_pdf",
        "excel_to_pdf",
        "html_to_pdf",
    };

    /// <summary>判定工具在 V1 的承载通道；未显式列入右抽屉/起始页的一律临时标签。</summary>
    public static ToolChannel ChannelFor(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (RightDrawerIds.Contains(tool.Id))
        {
            return ToolChannel.RightDrawer;
        }

        return StartPageIds.Contains(tool.Id) ? ToolChannel.StartPage : ToolChannel.TempTab;
    }

    /// <summary>该工具是否走右抽屉宿主（organize / pdfa）。</summary>
    public static bool RoutesToRightDrawer(Tool tool) => ChannelFor(tool) == ToolChannel.RightDrawer;
}

/// <summary>
/// 工具页 ViewModel 的文档上下文接收口：打开工具页时，若当前有激活的文档标签，
/// 宿主把该文档路径交接给工具页视图模型（各工具组基类的既有 <c>AddFiles(IEnumerable&lt;string&gt;)</c> 即隐式实现）。
/// </summary>
public interface IToolDocumentReceiver
{
    /// <summary>接收宿主交接的文档路径；语义与各工具页既有的 AddFiles（过滤/去重/单文件替换）一致。</summary>
    void AddFiles(IEnumerable<string> paths);
}

/// <summary>
/// V2 阅读器浮菜单「用工具处理 ▾」的统一入口：把当前文档置顶并标记「当前文档」，
/// 并在工具确有页范围字段时预填「当前页-当前页」（H-6）。
/// <para>
/// 与 <see cref="IToolDocumentReceiver"/> 的区别：后者只把路径加入文件列表；
/// 本接口额外要求（a）当前文档置顶 + 可观察标记，（b）页范围预填契约。
/// 各工具组基类实现置顶与标记；页范围预填由确有该字段的工具（split/rotate）覆写。
/// </para>
/// </summary>
public interface ICurrentDocumentPinnable
{
    /// <summary>把当前文档加入并置顶，同时标记为「当前文档」（不改变既有 AddFiles 语义）。</summary>
    void PinCurrentDocument(string path);

    /// <summary>该工具是否有页范围字段可预填（H-6：compress/pdf_to_word 为 false）。</summary>
    bool SupportsPageRangePrefill { get; }

    /// <summary>预填页范围（1 基，含端点）。仅在 <see cref="SupportsPageRangePrefill"/> 为真时由宿主调用。</summary>
    void PrefillPageRange(int startPage, int endPage);
}
