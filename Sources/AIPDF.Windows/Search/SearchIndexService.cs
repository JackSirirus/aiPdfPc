// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V1 阅读器搜索索引服务（T2）：按页惰性索引 + 全文搜索 + 无文本层判定 + 内存上限 + 乱码率启发式。
//
// 设计要点（对照 PRD 3.3 FR-09/FR-10 与计划 T2 规格）：
//  - 惰性语义：页文本首次被访问（显式 EnsurePageIndexed/GetPageText，或首次 Search）才提取+索引；
//    未索引页不参与无文本层判定（文档级 IsTextLayerAvailable 仅统计已索引页）。
//  - CJK 匹配：索引文本上的直接子串匹配（spike 证实 PDFium CJK 提取正确率 100%、
//    原生 Find 可用；索引内子串匹配与 GetText 同一下标空间，且免去逐次页加载）。
//    flags=0 大小写不敏感语义以 OrdinalIgnoreCase 对应。
//  - 内存上限【R13】：索引字符总预算（默认 ≈20MB UTF-16）。超限停止新页索引（已开始
//    索引的页仍可搜），IsFullyIndexed=false 供 UI 消费「部分索引」状态。
//  - 乱码率启发式【D12】：替换字符（U+FFFD）+ 不可见控制字符（C0/C1，除 \t\n\r 结构性
//    空白）占比 > 阈值（PRD 建议 15%）→ 低质量页标记，命中项携带 IsLowQualityPage。
//  - 线程模型：单个服务内 _gate 串行化（搜索为 ms 级操作，V1 取简单正确）；_gate →
//    PdfiumLock.Global 单向嵌套（本类持有 _gate 时才进入 PDFium 锁，无反向路径，无死锁）。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Search;

/// <summary>单条搜索命中：页索引 + 字符区间 [CharStart, CharEnd) + 命中矩形（左上原点 pt）。</summary>
/// <param name="Rect">命中区间的包围盒（多行命中为各行矩形的并集）。</param>
/// <param name="LineRects">按行拆分的矩形（CountRects/GetRect 原生按行合并），页内高亮消费。</param>
/// <param name="IsLowQualityPage">所在页被乱码率启发式标记为低质量文本层【D12】。</param>
public sealed record SearchHit(
    int PageIndex,
    int CharStart,
    int CharEnd,
    PdfRect Rect,
    IReadOnlyList<PdfRect> LineRects,
    bool IsLowQualityPage);

/// <summary>单页索引信息（UI 可观测）。</summary>
/// <param name="IsIndexed">false = 因内存预算被跳过，尚未索引（「部分索引」状态）。</param>
public sealed record PageTextInfo(
    int PageIndex,
    bool IsIndexed,
    int CharCount,
    bool HasTextLayer,
    bool IsLowQuality,
    double GarbageRatio);

/// <summary>文档级索引状态（UI 消费：部分索引提示、搜索可用性、索引中过渡态【ST-16】）。</summary>
public sealed record SearchIndexStatus(
    int PageCount,
    int IndexedPageCount,
    long IndexedChars,
    long MaxIndexedChars,
    bool IsFullyIndexed,
    bool MemoryBudgetExceeded,
    bool IsTextLayerAvailable,
    int LowQualityPageCount);

/// <summary>
/// 按页惰性全文搜索索引。一个服务实例可挂接多份文档（docId → 索引）。
/// 文档句柄生命周期由调用方管理（Dispose 后须 Detach）。
/// </summary>
public sealed class SearchIndexService
{
    /// <summary>默认索引字符总预算：10,485,760 字符 ≈ 20MB（UTF-16 每字符 2 字节）。</summary>
    public const long DefaultMaxIndexedChars = 10 * 1024 * 1024;

    /// <summary>默认乱码率阈值：15%（PRD 3.3 FR-10 建议初始值，spike 期可校准）。</summary>
    public const double DefaultGarbageRatioThreshold = 0.15;

    private readonly PdfiumTextService _textService;
    private readonly long _maxIndexedChars;
    private readonly double _garbageRatioThreshold;
    private readonly object _gate = new();
    private readonly Dictionary<string, DocIndex> _documents = new(StringComparer.Ordinal);

    public SearchIndexService(
        PdfiumTextService textService,
        long maxIndexedChars = DefaultMaxIndexedChars,
        double garbageRatioThreshold = DefaultGarbageRatioThreshold)
    {
        ArgumentNullException.ThrowIfNull(textService);
        if (maxIndexedChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxIndexedChars));
        if (garbageRatioThreshold is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(garbageRatioThreshold));
        _textService = textService;
        _maxIndexedChars = maxIndexedChars;
        _garbageRatioThreshold = garbageRatioThreshold;
    }

    // ------------------------------------------------------------------ 挂接 / 解除

    /// <summary>挂接文档索引。docId 任意稳定键（V1 用规范化路径）。重复挂接同 docId 以新文档为准。</summary>
    public void Attach(string docId, PdfiumTextService.TextDocument doc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentNullException.ThrowIfNull(doc);
        lock (_gate)
        {
            _documents[docId] = new DocIndex(doc, doc.PageCount);
        }
    }

    /// <summary>解除挂接并释放索引内存（不 Dispose 文档句柄，调用方管理）。</summary>
    public void Detach(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        lock (_gate)
        {
            _documents.Remove(docId);
        }
    }

    public bool IsAttached(string docId)
    {
        lock (_gate) return _documents.ContainsKey(docId);
    }

    // ------------------------------------------------------------------ 惰性索引

    /// <summary>
    /// 确保指定页已索引（首次访问才提取；惰性语义：只建这一页，其余页不动）。
    /// 页超出内存预算时该页被跳过（IsIndexed=false），已索引页不受影响。
    /// </summary>
    public void EnsurePageIndexed(string docId, int pageIndex)
    {
        lock (_gate)
        {
            var idx = GetDocOrThrow(docId);
            EnsurePageIndexedCore(idx, pageIndex);
        }
    }

    /// <summary>取页文本（惰性触发该页索引）。页被预算跳过或无文本层时返回空串。</summary>
    public string GetPageText(string docId, int pageIndex)
    {
        lock (_gate)
        {
            var idx = GetDocOrThrow(docId);
            EnsurePageIndexedCore(idx, pageIndex);
            return idx.Pages.TryGetValue(pageIndex, out var entry) ? entry.Text : string.Empty;
        }
    }

    /// <summary>
    /// V4 T8：取页内字符区间的矩形（PDF 点，左上原点）——朗读按 <c>SpeakProgress</c> 的字符区间调用，
    /// 用于页内高亮跟随。未挂接 / 区间无效返回空列表（不抛：朗读进度可能晚于文档关闭）。
    /// </summary>
    public IReadOnlyList<PdfRect> GetTextRects(string docId, int pageIndex, int charStart, int charCount)
    {
        if (string.IsNullOrWhiteSpace(docId) || charCount <= 0 || charStart < 0)
        {
            return [];
        }

        lock (_gate)
        {
            if (!_documents.TryGetValue(docId, out var index))
            {
                return [];
            }

            try
            {
                return _textService.GetTextRects(index.Doc, pageIndex, charStart, charCount);
            }
            catch (Exception)
            {
                return [];   // 句柄已关闭 / 区间越界：静默退化为不高亮（不打断朗读）
            }
        }
    }

    /// <summary>单页索引信息（含低质量标记与乱码率，UI 可观测）。</summary>
    public PageTextInfo GetPageInfo(string docId, int pageIndex)
    {
        lock (_gate)
        {
            var idx = GetDocOrThrow(docId);
            EnsurePageIndexedCore(idx, pageIndex);
            if (idx.Pages.TryGetValue(pageIndex, out var entry))
            {
                return new PageTextInfo(pageIndex, true, entry.Text.Length, entry.HasTextLayer,
                    entry.IsLowQuality, entry.GarbageRatio);
            }
            return new PageTextInfo(pageIndex, false, 0, false, false, 0);
        }
    }

    // ------------------------------------------------------------------ 搜索

    /// <summary>
    /// 全文搜索：先把全部未索引页纳入索引（受内存预算约束，超限页跳过），
    /// 再在已索引页文本上做大小写不敏感子串匹配（CJK 直接子串，spike 证实可用）。
    /// 首次搜索承担索引构建（一次性、会话内复用【ST-16】）。
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string docId, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(query)) return hits;

        lock (_gate)
        {
            var idx = GetDocOrThrow(docId);
            for (var i = 0; i < idx.PageCount; i++) EnsurePageIndexedCore(idx, i);

            for (var pageIndex = 0; pageIndex < idx.PageCount; pageIndex++)
            {
                if (!idx.Pages.TryGetValue(pageIndex, out var entry)) continue;
                if (entry.Text.Length == 0) continue;

                var start = 0;
                while (true)
                {
                    var pos = entry.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0) break;
                    var charEnd = pos + query.Length;
                    var lineRects = _textService.GetTextRects(idx.Doc, pageIndex, pos, query.Length);
                    hits.Add(new SearchHit(
                        pageIndex, pos, charEnd, UnionRects(lineRects), lineRects, entry.IsLowQuality));
                    start = pos + 1;
                }
            }
        }
        return hits;
    }

    // ------------------------------------------------------------------ 状态查询

    /// <summary>
    /// 文档级文本层可用性。惰性语义：尚未索引任何页 → true（未知，不预设不可用）；
    /// 「全部已索引页均无文本」才判定 false（无文本层降级【ST-15】由 UI 消费）。
    /// </summary>
    public bool IsTextLayerAvailable(string docId)
    {
        lock (_gate)
        {
            var idx = GetDocOrThrow(docId);
            if (idx.Pages.Count == 0) return true;
            foreach (var entry in idx.Pages.Values)
            {
                if (entry.HasTextLayer) return true;
            }
            return false;
        }
    }

    /// <summary>文档级索引状态（索引页数/字符数/预算/低质量页计数）。</summary>
    public SearchIndexStatus GetStatus(string docId)
    {
        lock (_gate)
        {
            var idx = GetDocOrThrow(docId);
            var lowQuality = 0;
            foreach (var entry in idx.Pages.Values)
            {
                if (entry.IsLowQuality) lowQuality++;
            }
            return new SearchIndexStatus(
                idx.PageCount, idx.Pages.Count, idx.IndexedChars, _maxIndexedChars,
                idx.Pages.Count >= idx.PageCount && !idx.BudgetExceeded,
                idx.BudgetExceeded,
                IsTextLayerAvailableCore(idx),
                lowQuality);
        }
    }

    public bool IsPageLowQuality(string docId, int pageIndex)
    {
        return GetPageInfo(docId, pageIndex).IsLowQuality;
    }

    // ------------------------------------------------------------------ 乱码率启发式【D12】

    /// <summary>乱码率 =（替换字符 + 不可见控制字符）/ 总字符（结构性空白 \t\n\r 不计）。</summary>
    public static double ComputeGarbageRatio(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var bad = 0;
        foreach (var c in text)
        {
            if (IsGarbageChar(c)) bad++;
        }
        return (double)bad / text.Length;
    }

    internal static bool IsGarbageChar(char c)
    {
        if (c == '\ufffd') return true;                       // 替换字符（映射失败的典型输出）
        if (c == '\t' || c == '\n' || c == '\r') return false; // 结构性空白：PDFium 行分隔，正常
        if (c < ' ') return true;                              // 其余 C0 控制字符
        if (c == '\x7f') return true;                          // DEL
        if (c is >= '\u0080' and <= '\u009f') return true;     // C1 控制字符
        return false;
    }

    // ------------------------------------------------------------------ 内部

    private sealed class DocIndex
    {
        public DocIndex(PdfiumTextService.TextDocument doc, int pageCount)
        {
            Doc = doc;
            PageCount = pageCount;
        }

        public PdfiumTextService.TextDocument Doc { get; }
        public int PageCount { get; }
        public Dictionary<int, PageEntry> Pages { get; } = new();
        public HashSet<int> SkippedPages { get; } = new();
        public long IndexedChars { get; set; }
        public bool BudgetExceeded { get; set; }
    }

    private sealed class PageEntry
    {
        public required string Text { get; init; }
        public required bool HasTextLayer { get; init; }
        public required bool IsLowQuality { get; init; }
        public required double GarbageRatio { get; init; }
    }

    /// <summary>调用方持有 _gate。惰性建页；预算不足时跳过该页并置 BudgetExceeded。</summary>
    private void EnsurePageIndexedCore(DocIndex idx, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= idx.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"页码超出范围：0–{idx.PageCount - 1}。");
        if (idx.Pages.ContainsKey(pageIndex) || idx.SkippedPages.Contains(pageIndex)) return;

        // 预算已耗尽：不再提取，直接跳过（「超限停止新页索引」）
        if (idx.BudgetExceeded)
        {
            idx.SkippedPages.Add(pageIndex);
            return;
        }

        var text = _textService.ExtractPageText(idx.Doc, pageIndex);

        // 提取后预算校验：放不下的页跳过（不索引、不记账），并停止后续新页索引
        if (idx.IndexedChars + text.Length > _maxIndexedChars)
        {
            idx.BudgetExceeded = true;
            idx.SkippedPages.Add(pageIndex);
            return;
        }

        var garbageRatio = ComputeGarbageRatio(text);
        idx.Pages[pageIndex] = new PageEntry
        {
            Text = text,
            HasTextLayer = text.Length > 0,
            IsLowQuality = garbageRatio > _garbageRatioThreshold,
            GarbageRatio = garbageRatio,
        };
        idx.IndexedChars += text.Length;
    }

    private bool IsTextLayerAvailableCore(DocIndex idx)
    {
        // 调用方持有 _gate
        if (idx.Pages.Count == 0) return true; // 惰性语义：未索引页不算
        foreach (var entry in idx.Pages.Values)
        {
            if (entry.HasTextLayer) return true;
        }
        return false;
    }

    private DocIndex GetDocOrThrow(string docId)
    {
        // 调用方持有 _gate
        if (!_documents.TryGetValue(docId, out var idx))
            throw new KeyNotFoundException($"文档未挂接索引: {docId}");
        return idx;
    }

    private static PdfRect UnionRects(IReadOnlyList<PdfRect> rects)
    {
        if (rects.Count == 0) return default;
        var union = rects[0];
        for (var i = 1; i < rects.Count; i++)
        {
            var r = rects[i];
            union = new PdfRect(
                Math.Min(union.Left, r.Left),
                Math.Min(union.Top, r.Top),
                Math.Max(union.Right, r.Right),
                Math.Max(union.Bottom, r.Bottom));
        }
        return union;
    }
}
