// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 搜索 UI 对 T2 SearchIndexService 的消费接口 + 适配器。
//
// 为什么不直接让 SearchIndexService 实现接口：T2 服务是已提交的 T1/T2 交付物，
// T8 不改动其声明（MUST NOT），故用薄适配器把稳定的服务方法暴露为可桩替接口，
// 让 SearchPanelViewModel 的防抖/命中投影/状态机可在 xunit 中用假实现直测（不依赖 PDFium 与 UI）。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Search;

/// <summary>搜索 UI 消费的最小索引能力面（T2 <see cref="SearchIndexService"/> 的方法子集）。</summary>
public interface ISearchIndexProvider
{
    /// <summary>全文搜索（阻塞；调用方须在后台线程执行——首次搜索会构建整册索引）。</summary>
    IReadOnlyList<SearchHit> Search(string docId, string query);

    /// <summary>文档级索引状态（部分索引提示 / 预算超限 / 低质量页计数）。</summary>
    SearchIndexStatus GetStatus(string docId);

    /// <summary>文档级文本层可用性（无文本层降级【ST-15】）。惰性：未索引页不算。</summary>
    bool IsTextLayerAvailable(string docId);

    /// <summary>取页文本（惰性触发该页索引）；命中摘录取自此文本。</summary>
    string GetPageText(string docId, int pageIndex);
}

/// <summary>把 T2 <see cref="SearchIndexService"/> 适配为 <see cref="ISearchIndexProvider"/>。</summary>
public sealed class SearchIndexProviderAdapter(SearchIndexService service) : ISearchIndexProvider
{
    private readonly SearchIndexService _service = service ?? throw new ArgumentNullException(nameof(service));

    public IReadOnlyList<SearchHit> Search(string docId, string query) => _service.Search(docId, query);

    public SearchIndexStatus GetStatus(string docId) => _service.GetStatus(docId);

    public bool IsTextLayerAvailable(string docId) => _service.IsTextLayerAvailable(docId);

    public string GetPageText(string docId, int pageIndex) => _service.GetPageText(docId, pageIndex);
}
