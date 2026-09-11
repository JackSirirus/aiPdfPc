// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 目录面板数据源：PDFium 大纲（bookmark 树）只读解析。
//
// 纪律【R12】：所有 FPDF_* 调用持有 PdfiumLock.Global（与 T1 渲染 / T2 文本层共用同一把锁）。
// 152 原生库 + PDFiumCore CppSharp 封送要点（经 spike 程序逐一验证，勿凭记忆改写）：
//   - FPDFBookmark_GetFirstChild(doc, NULL)：C# 传 null 引用即可，包装器按 IntPtr.Zero 封送；
//   - FPDF_BOOKMARK 本质是 CPDF_Dictionary 裸指针，文档打开期间稳定；
//   - 【关键】行走与数据读取必须两阶段分离：单遍「边走边读标题/目标页」在
//     CppSharp 封送下出现伪随机树损坏（子链丢失/兄弟链成环，五轮复现）；
//     纯行走收集句柄 → 事后统一读数据 则五轮全对。改写前先跑 tmp/t4-corpus 验证；
//   - 标题读取两段式：先 FPDFBookmark_GetTitle(bk, NULL, 0) 取字节数（UTF-16LE，含结尾 NUL），
//     再分配非托管缓冲二次调用，PtrToStringUni 解码；
//   - 目标页：FPDFBookmark_GetDest 为空时回退 FPDFBookmark_GetAction → FPDFAction_GetDest，
//     FPDFDest_GetDestPageIndex(doc, dest) 返回 0 基页码（无效为 -1）。
using System.Runtime.InteropServices;
using PDFiumCore;

namespace AIPDF.Windows.Rendering;

/// <summary>大纲条目（只读快照）：标题、目标页（0 基；无效为 -1）、子条目。</summary>
public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);

/// <summary>
/// PDF 大纲解析服务：从 <see cref="PdfiumRenderService"/> 的持久句柄读取书签树。
/// 无大纲文档返回空列表（【ST-17】空态由面板层渲染）。
/// </summary>
public static class PdfiumOutlineService
{
    /// <summary>递归深度上限：防御畸形大纲环引用，超过按截断处理。</summary>
    private const int MaxDepth = 32;

    /// <summary>单文档大纲条目上限：防御病态超大大纲拖垮 UI（V1 保守阈值）。</summary>
    private const int MaxItems = 4096;

    /// <summary>书签句柄节点（解析中间态）。</summary>
    private sealed class Node
    {
        public required FpdfBookmarkT Handle;
        public int ParentIndex = -1;
        public int Depth;
    }

    /// <summary>
    /// 解析文档大纲（阻塞调用；条目多时应在后台线程执行，如 VM 的 LoadAsync）。
    /// 文档未打开返回 null（与空大纲区分，便于调用方提示）。
    /// </summary>
    public static IReadOnlyList<OutlineItem>? ReadOutline(PdfiumRenderService renderer, string docId)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        var handle = renderer.TryGetDocumentHandle(docId);
        if (handle is null || handle.__Instance == IntPtr.Zero)
        {
            return null;
        }

        var budget = MaxItems;
        var nodes = new List<Node>();

        lock (PdfiumLock.Global)
        {
            // 阶段 1：纯行走收集句柄与树结构（不读任何数据——封送兼容性关键，见文件头注释）
            CollectLevel(handle, -1, 0, nodes, ref budget);

            // 阶段 2：逐句柄读取标题与目标页（顺序读，无行走交错）
            var titles = new string[nodes.Count];
            var pages = new int[nodes.Count];
            var children = new List<int>[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                titles[i] = ReadTitle(nodes[i].Handle);
                pages[i] = ReadDestPageIndex(handle, nodes[i].Handle);
                if (nodes[i].ParentIndex >= 0)
                {
                    children[nodes[i].ParentIndex] ??= [];
                    children[nodes[i].ParentIndex].Add(i);
                }
            }

            // 组树：从根（ParentIndex == -1）递归装配
            OutlineItem Build(int index)
            {
                var kids = children[index] is null
                    ? []
                    : children[index].Select(Build).ToList();
                return new OutlineItem(titles[index], pages[index], kids);
            }

            var roots = new List<OutlineItem>();
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].ParentIndex == -1)
                {
                    roots.Add(Build(i));
                }
            }
            return roots;
        }
    }

    /// <summary>阶段 1：收集 parent 的子书签链（parentIndex 为 -1 时读顶层）。调用方持有 PdfiumLock.Global。</summary>
    private static void CollectLevel(FpdfDocumentT doc, int parentIndex, int depth, List<Node> nodes, ref int budget)
    {
        if (depth >= MaxDepth || budget <= 0)
        {
            return;
        }

        var start = parentIndex < 0 ? null : nodes[parentIndex].Handle;
        // parent 为 null → 顶层首条（封装器对 null 引用按 NULL 封送，spike 已验证）
        var bookmark = fpdf_doc.FPDFBookmarkGetFirstChild(doc, start);

        var level = new List<int>();
        while (bookmark is not null && bookmark.__Instance != IntPtr.Zero && budget > 0)
        {
            budget--;
            nodes.Add(new Node { Handle = bookmark, ParentIndex = parentIndex, Depth = depth });
            level.Add(nodes.Count - 1);
            bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(doc, bookmark);
        }

        // 本层收齐后再下钻（保持行走与下钻分离的纯性）
        foreach (var index in level)
        {
            CollectLevel(doc, index, depth + 1, nodes, ref budget);
            if (budget <= 0)
            {
                break;
            }
        }
    }

    /// <summary>读取书签标题（UTF-16LE 两段式读取）。空标题回退为占位名。调用方已持全局锁。</summary>
    private static string ReadTitle(FpdfBookmarkT bookmark)
    {
        var byteLen = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (byteLen <= 2)
        {
            // 0/1 字节异常，2 字节 = 仅结尾 NUL（空标题）
            return "(无标题)";
        }

        var buffer = Marshal.AllocHGlobal((int)byteLen);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, byteLen);
            return Marshal.PtrToStringUni(buffer)?.Trim() ?? "(无标题)";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读取书签目标页码（0 基）。无 dest/action 目标返回 -1（面板显示为不可跳转）。</summary>
    private static int ReadDestPageIndex(FpdfDocumentT doc, FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(doc, bookmark);
        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (action is not null && action.__Instance != IntPtr.Zero)
            {
                dest = fpdf_doc.FPDFActionGetDest(doc, action);
            }
        }

        return dest is not null && dest.__Instance != IntPtr.Zero
            ? fpdf_doc.FPDFDestGetDestPageIndex(doc, dest)
            : -1;
    }
}
