// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T2 文本层与搜索索引服务测试。语料全部为合成文件（tmp/t2-corpus，不提交）。
using System.Diagnostics;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Search;
using Xunit;
using Xunit.Abstractions;

namespace AIPDF.Windows.Tests.Search;

/// <summary>
/// 验收对照（计划 T2）：
///   惰性索引 / 中英文搜索命中 / 无文本层判定 / 内存上限触发 / 低质量页标记 / 索引会话内复用
///   + B5 预演（500 页搜索 P95，目标 <500ms）+ 原生 Find 与子串匹配交叉验证。
/// </summary>
public sealed class SearchIndexServiceTests
{
    private readonly ITestOutputHelper _output;

    public SearchIndexServiceTests(ITestOutputHelper output)
    {
        _output = output;
        T2Corpus.Ensure();
    }

    private static (PdfiumTextService TextService, SearchIndexService Index, string DocId, PdfiumTextService.TextDocument Doc)
        OpenWithIndex(string path, long maxChars = SearchIndexService.DefaultMaxIndexedChars)
    {
        var textService = new PdfiumTextService();
        var index = new SearchIndexService(textService, maxChars);
        var doc = textService.OpenDocument(path);
        var docId = path;
        index.Attach(docId, doc);
        return (textService, index, docId, doc);
    }

    // ---------------------------------------------------------------- 提取

    [Fact]
    public void ExtractPageText_CjkAndEnglish_Correct()
    {
        var (textService, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            var page0 = textService.ExtractPageText(doc, 0);
            Assert.Contains("全文搜索测试语料 第1页", page0);
            Assert.Contains("AIPDF corpus page 1", page0);
            Assert.Contains("梅花1号", page0);
            Assert.True(textService.CountPageChars(doc, 0) > 0);

            // 索引内文本与提取一致
            Assert.Equal(page0, index.GetPageText(docId, 0));
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 惰性索引

    [Fact]
    public void LazyIndexing_OnlyVisitedPageIndexed()
    {
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            // 访问第 3 页（index 2）才建第 3 页
            var text = index.GetPageText(docId, 2);
            Assert.Contains("梅花3号", text);
            Assert.Contains("AIPDF corpus page 3", text);

            var status = index.GetStatus(docId);
            Assert.Equal(1, status.IndexedPageCount);
            Assert.Equal(500, status.PageCount);
            Assert.False(status.IsFullyIndexed);      // 只索引了 1/500
            Assert.False(status.MemoryBudgetExceeded);
            Assert.True(status.IsTextLayerAvailable);
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- CJK 搜索

    [Fact]
    public void Search_CjkFixedMarker_CorrectPagesAndRects()
    {
        var (textService, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            var hits = index.Search(docId, T2Corpus.CjkFixedMarker);
            Assert.Equal(T2Corpus.CjkFixturePages.Length, hits.Count);
            Assert.Equal(T2Corpus.CjkFixturePages, hits.Select(h => h.PageIndex));

            foreach (var hit in hits)
            {
                // 字符区间与页文本一致
                var pageText = index.GetPageText(docId, hit.PageIndex);
                Assert.Equal(T2Corpus.CjkFixedMarker, pageText[hit.CharStart..hit.CharEnd]);
                // 矩形有效：非退化、页内（595x842 pt，左上原点）
                Assert.True(hit.Rect.Width > 0);
                Assert.True(hit.Rect.Height > 0);
                Assert.InRange(hit.Rect.Left, 0, 595);
                Assert.InRange(hit.Rect.Right, 0, 595);
                Assert.InRange(hit.Rect.Top, 0, 842);
                Assert.InRange(hit.Rect.Bottom, 0, 842);
                Assert.True(hit.Rect.Top < hit.Rect.Bottom);
                // 按行矩形非空且与包围盒一致
                Assert.NotEmpty(hit.LineRects);
                Assert.False(hit.IsLowQualityPage);
            }

            // 矩形与字符物理位置对照：语料在 (50, ~150) 附近绘制标记 → 左上原点下 Top 应在页面上半部
            var first = hits[0];
            Assert.True(first.Rect.Top < 842 / 2.0, $"标记应位于页面上半部，实际 Top={first.Rect.Top}");
            Assert.True(first.Rect.Left >= 40, $"标记起点应在左侧，实际 Left={first.Rect.Left}");
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 英文搜索

    [Fact]
    public void Search_English_CaseInsensitive()
    {
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            var lower = index.Search(docId, "zebra-quartz");
            var upper = index.Search(docId, T2Corpus.EnglishMarker);
            Assert.Equal(T2Corpus.EnglishFixturePages, lower.Select(h => h.PageIndex));
            Assert.Equal(T2Corpus.EnglishFixturePages, upper.Select(h => h.PageIndex));
            Assert.All(lower, h => Assert.Equal("zebra-quartz".Length, h.CharEnd - h.CharStart));
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 无文本层

    [Fact]
    public void ScannedDocument_NoTextLayer_IsUnavailable()
    {
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Scanned);
        using var _ = doc;
        try
        {
            var hits = index.Search(docId, "任何词");
            Assert.Empty(hits);
            Assert.False(index.IsTextLayerAvailable(docId));
            var status = index.GetStatus(docId);
            Assert.False(status.IsTextLayerAvailable);
            Assert.Equal(8, status.IndexedPageCount); // 全部已索引，均无文本
            var info = index.GetPageInfo(docId, 0);
            Assert.True(info.IsIndexed);
            Assert.False(info.HasTextLayer);
            Assert.Equal(0, info.CharCount);
        }
        finally
        {
            index.Detach(docId);
        }
    }

    [Fact]
    public void MixedDocument_PartialTextLayer_AvailableWithPartialHits()
    {
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Mixed);
        using var _ = doc;
        try
        {
            var hits = index.Search(docId, "mixed corpus");
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.InRange(h.PageIndex, 0, 4)); // 仅 0-4 页有文本层
            Assert.True(index.IsTextLayerAvailable(docId));
            Assert.False(index.GetPageInfo(docId, 7).HasTextLayer);
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 内存上限

    [Fact]
    public void MemoryBudget_StopsNewPages_IndexedPagesStillSearchable()
    {
        // 预算 200 字符：语料每页约 90-110 字符 → 前两页可容纳，第三页超限停止
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500, maxChars: 200);
        using var _ = doc;
        try
        {
            var hits = index.Search(docId, "AIPDF corpus page");
            Assert.NotEmpty(hits); // 已索引页仍可搜
            var status = index.GetStatus(docId);
            Assert.True(status.MemoryBudgetExceeded);
            Assert.False(status.IsFullyIndexed);
            Assert.True(status.IndexedPageCount > 0);
            Assert.True(status.IndexedPageCount < status.PageCount);
            Assert.True(status.IndexedChars <= 200);
            // 命中只可能来自已索引页
            Assert.All(hits, h => Assert.True(h.PageIndex < status.PageCount));
            Assert.All(hits, h => Assert.True(h.PageIndex < status.IndexedPageCount || status.IndexedPageCount == status.PageCount));

            // 未索引页的显式访问被跳过（不提取、不索引）
            var lastInfo = index.GetPageInfo(docId, 499);
            Assert.False(lastInfo.IsIndexed);
            Assert.Equal(string.Empty, index.GetPageText(docId, 499));
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 低质量页标记

    [Fact]
    public void LowQualityPage_HeuristicMarksGarbagePages()
    {
        // 纯函数：替换字符/控制字符占比
        Assert.Equal(0, SearchIndexService.ComputeGarbageRatio("normal text"));
        Assert.Equal(0, SearchIndexService.ComputeGarbageRatio("a\tb\nc\rd")); // 结构性空白不计
        Assert.Equal(0.5, SearchIndexService.ComputeGarbageRatio("ab\ufffd\ufffd"));
        Assert.Equal(0.5, SearchIndexService.ComputeGarbageRatio("ab\u0001\u0002"));
        Assert.Equal(0, SearchIndexService.ComputeGarbageRatio(""));

        // 集成：语料页 0 为控制字节页（生成器实测乱码率 0.57），页 1 为对照页
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Garbage);
        using var _ = doc;
        try
        {
            var garbageInfo = index.GetPageInfo(docId, 0);
            Assert.True(garbageInfo.IsIndexed);
            Assert.True(garbageInfo.GarbageRatio > 0.15,
                $"乱码页应超阈值，实测 {garbageInfo.GarbageRatio:F2}");
            Assert.True(garbageInfo.IsLowQuality);
            Assert.True(index.IsPageLowQuality(docId, 0));

            var cleanInfo = index.GetPageInfo(docId, 1);
            Assert.False(cleanInfo.IsLowQuality);

            // 命中项携带低质量标注
            var hits = index.Search(docId, "normal text anchor");
            Assert.NotEmpty(hits);
            Assert.All(hits.Where(h => h.PageIndex == 0), h => Assert.True(h.IsLowQualityPage));
            Assert.All(hits.Where(h => h.PageIndex == 1), h => Assert.False(h.IsLowQualityPage));
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 会话内复用

    [Fact]
    public void IndexReusedWithinSession_SecondSearchDoesNotReextract()
    {
        var (textService, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            index.Search(docId, "梅花"); // 首搜：建全量索引
            var extractionAfterFirst = textService.ExtractionCount;
            Assert.Equal(500, extractionAfterFirst);

            index.Search(docId, "AIPDF corpus page"); // 二搜：零提取
            index.Search(docId, "Filler line");
            Assert.Equal(extractionAfterFirst, textService.ExtractionCount);

            // 命中复用一致
            var again = index.Search(docId, "梅花");
            Assert.Equal(500, again.Count);
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 原生 Find 交叉验证

    [Fact]
    public void NativeFindInPage_AgreesWithSubstringMatch()
    {
        var (textService, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            var pageText = textService.ExtractPageText(doc, 0);

            foreach (var term in new[] { "AIPDF", "corpus", "梅花1号", "alpha beta" })
            {
                var native = textService.FindInPage(doc, 0, term);
                var expected = new List<int>();
                var start = 0;
                while (true)
                {
                    var pos = pageText.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0) break;
                    expected.Add(pos);
                    start = pos + 1;
                }
                Assert.Equal(expected, native);
            }

            // 无命中词
            Assert.Empty(textService.FindInPage(doc, 0, "不存在的词"));
            Assert.Empty(textService.FindInPage(doc, 0, ""));
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- B5 预演

    [Fact]
    public void B5Rehearsal_500PageSearch_P95Under500ms()
    {
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            // 首搜 = 索引构建（B5 口径排除）
            var buildSw = Stopwatch.StartNew();
            var warm = index.Search(docId, "AIPDF corpus page");
            buildSw.Stop();
            Assert.Equal(500, warm.Count);

            var timings = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                var sw = Stopwatch.StartNew();
                var hits = index.Search(docId, "AIPDF corpus page");
                sw.Stop();
                Assert.Equal(500, hits.Count);
                timings.Add(sw.Elapsed.TotalMilliseconds);
            }
            timings.Sort();
            var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];

            _output.WriteLine($"[B5 预演] 索引构建(500页) = {buildSw.Elapsed.TotalMilliseconds:F1} ms");
            _output.WriteLine($"[B5 预演] 搜索 P95 = {p95:F1} ms（目标 <500ms）");
            _output.WriteLine($"[B5 预演] min/median/max = {timings[0]:F1}/{timings[timings.Count / 2]:F1}/{timings[^1]:F1} ms");
            Assert.True(p95 < 500, $"500 页搜索 P95={p95:F1}ms 超出 500ms 目标");
        }
        finally
        {
            index.Detach(docId);
        }
    }

    // ---------------------------------------------------------------- 并发冒烟（锁纪律）

    [Fact]
    public async Task ConcurrentSearch_NoDeadlock()
    {
        var (_, index, docId, doc) = OpenWithIndex(T2Corpus.Cjk500);
        using var _ = doc;
        try
        {
            var tasks = Enumerable.Range(0, 2).Select(async t =>
            {
                var total = 0;
                for (var i = 0; i < 5; i++)
                {
                    var hits = index.Search(docId, t == 0 ? "AIPDF corpus page" : "梅花");
                    total += hits.Count;
                    await Task.Yield();
                }
                return total;
            });
            var results = await Task.WhenAll(tasks);
            // "梅花" 每页 1 处（500）+ "AIPDF corpus page" 每页 1 处（500）× 各 5 轮
            Assert.Equal(500 * 5, results[0]);
            Assert.Equal(500 * 5, results[1]);
        }
        finally
        {
            index.Detach(docId);
        }
    }
}
