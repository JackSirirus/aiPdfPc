// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T11：批注导出服务（XFDF / 摘要 PDF）。
//
// 导出源是**文件内全部注释**（引擎侧经 _page_annotation_rows 读回），不是客户端内存集合——
// 否则导出「其它阅读器标注过的 PDF」会得到空结果。导出**只读源文件**，产物进 outputDir 下的
// 新任务目录（与其它工具一致）。引擎侧走独立 dispatch 分支，不进 BY_ID / ANNOTATION_OPERATIONS。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIPDF.Windows.Bridge;

namespace AIPDF.Windows.Tools;

/// <summary>批注导出（只读源文件）。</summary>
public sealed class ExportAnnotationsService
{
    /// <summary>同时导出 XFDF 与摘要 PDF。</summary>
    public const string FormatBoth = "both";

    /// <summary>仅导出 XFDF。</summary>
    public const string FormatXfdf = "xfdf";

    /// <summary>仅导出摘要 PDF。</summary>
    public const string FormatSummary = "summary";

    private readonly EngineBridge _bridge;

    public ExportAnnotationsService(EngineBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    /// <summary>
    /// 导出批注，返回产物路径。失败（引擎不可用 / 目录无效）返回空列表——宿主据此提示，不抛。
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportAsync(
        string path,
        string? password,
        string outputDir,
        string format = FormatBoth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(outputDir))
        {
            return [];
        }

        var request = new EngineRequest
        {
            Operation = "export_annotations",
            Files = [path],
            OutputDir = outputDir,
        };
        request.Options["format"] = format;
        if (!string.IsNullOrEmpty(password))
        {
            request.Options["password"] = password;
        }

        try
        {
            var result = await _bridge.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return result.Ok ? result.Outputs : [];
        }
        catch (Exception)
        {
            return [];   // 导出失败不阻断阅读
        }
    }
}
