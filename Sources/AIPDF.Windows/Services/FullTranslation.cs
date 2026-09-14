// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 整篇翻译服务层：
//   - 经既有 EngineBridge 的 `translate` op 执行「整篇」翻译（抽取 → 分块 → LLM → 回填 → mono/dual PDF）；
//   - 进度经 EngineBridge.Progress（stderr 行式 JSON）转发为 FullTranslationProgress；
//   - 取消 = CancellationToken → EngineBridge taskkill /T /F 终止整棵子进程树，不产出半成品被误用；
//   - 密钥仅在本进程内从请求 → EngineBridge options → 子进程 stdin 传递，绝不写入 argv / 环境变量 / 日志 / 文件；
//   - 结果解析为产物（mono/dual）、每页降级报告与源文档受限披露，供结果区展示。
using System.Globalization;
using System.Text.Json;
using AIPDF.Windows.Bridge;

namespace AIPDF.Windows.Services;

/// <summary>整篇翻译的源文档受限披露（仅权限加密文档非空）。与 backend `report.source_protection` 对齐。</summary>
public sealed record FullTranslationSourceProtection(bool Restricted, string? Disclosure)
{
    /// <summary>未受限的默认值。</summary>
    public static FullTranslationSourceProtection None { get; } = new(false, null);
}

/// <summary>一次整篇翻译请求（单文件；批量由 T21 编排）。</summary>
public sealed record FullTranslationRequest(
    string FilePath,
    string OutputDir,
    string SourceLang,
    string TargetLang,
    string Template,
    string Endpoint,
    string Model,
    string ApiKey,
    string? GlossaryPath = null,
    int? MaxTokens = null,
    bool GenerateMono = true,
    bool GenerateDual = true,
    string? Password = null,
    bool Marker = true);

/// <summary>翻译进度（阶段名 + 0..1 比例）。</summary>
public sealed record FullTranslationProgress(string Stage, double Fraction);

/// <summary>一个产物（mono / dual）。</summary>
public sealed record FullTranslationArtifact(string Kind, string Path);

/// <summary>单页降级项（页码 1 起 + 原因枚举 + 可搜索性，与 backend verify 对齐）。</summary>
public sealed record FullTranslationPageIssue(
    int Page,
    IReadOnlyList<string> Reasons,
    bool Searchable = true,
    bool Degraded = true);

/// <summary>
/// 可搜索性聚合（与 backend <c>report.summary</c> 对齐，规格 §9.2）：
/// <c>degraded_pages</c> / <c>unsearchable_pages</c> / <c>total_pages</c>。
/// </summary>
public sealed record FullTranslationSearchSummary(int DegradedPages, int UnsearchablePages, int TotalPages)
{
    /// <summary>可提取文本页数（总页数 − 不可搜索页；下限 0）。</summary>
    public int SearchablePages => Math.Max(0, TotalPages - UnsearchablePages);
}

/// <summary>
/// 整篇翻译结果。<see cref="Ok"/> 为 false 时 <see cref="Error"/> 为可直接展示的原因；
/// <see cref="Cancelled"/> 为 true 表示用户取消（引擎已杀子树，半成品保留但不采用）。
/// </summary>
public sealed record FullTranslationOutcome(
    bool Ok,
    string? Error,
    bool Cancelled,
    string? OutputDir,
    string? TaskDir,
    IReadOnlyList<FullTranslationArtifact> Artifacts,
    IReadOnlyList<FullTranslationPageIssue> PageIssues,
    FullTranslationSourceProtection SourceProtection,
    double ElapsedSeconds,
    FullTranslationSearchSummary? SearchSummary = null)
{
    /// <summary>取消结果（不视为失败）。</summary>
    public static FullTranslationOutcome Cancelled_ { get; } =
        new(false, null, true, null, null, [], [], FullTranslationSourceProtection.None, 0);
}

/// <summary>整篇翻译执行器抽象（便于单测与后续替换真实实现）。</summary>
public interface IFullTranslationService
{
    /// <summary>
    /// 执行一次整篇翻译。实现必须捕获传输异常并转成失败结果；取消转成 <c>Cancelled</c> 结果（不向 UI 抛）。
    /// 进度经 <paramref name="progress"/> 回调（可能在后台线程触发，宿主负责 marshal）。
    /// </summary>
    Task<FullTranslationOutcome> TranslateAsync(
        FullTranslationRequest request,
        IProgress<FullTranslationProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>经 <see cref="EngineBridge"/> 的 `translate` op 执行整篇翻译。</summary>
public sealed class EngineFullTranslationService : IFullTranslationService
{
    private readonly EngineBridge _bridge;

    public EngineFullTranslationService(EngineBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <inheritdoc />
    public async Task<FullTranslationOutcome> TranslateAsync(
        FullTranslationRequest request,
        IProgress<FullTranslationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var engineRequest = new EngineRequest
        {
            Operation = "translate",
            Files = [request.FilePath],
            Options = BuildOptions(request),
            OutputDir = request.OutputDir,
        };

        void OnProgress(object? sender, EngineProgressEventArgs e)
            => progress?.Report(new FullTranslationProgress(e.Message, e.Fraction));

        _bridge.Progress += OnProgress;
        try
        {
            var result = await _bridge.ExecuteAsync(engineRequest, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return new FullTranslationOutcome(
                    false, result.Error ?? "翻译失败，请稍后重试。", false, result.OutputDir, null,
                    [], [], FullTranslationSourceProtection.None, result.Elapsed ?? 0);
            }

            return ParseOutcome(result);
        }
        catch (OperationCanceledException)
        {
            return FullTranslationOutcome.Cancelled_;
        }
        catch (EngineBridgeException error)
        {
            return new FullTranslationOutcome(
                false, error.Message, false, null, null, [], [], FullTranslationSourceProtection.None, 0);
        }
        finally
        {
            _bridge.Progress -= OnProgress;
        }
    }

    /// <summary>组装引擎 options（值一律字符串；密钥仅经此处进入子进程 stdin）。</summary>
    private static Dictionary<string, object?> BuildOptions(FullTranslationRequest request)
    {
        var format = request.GenerateMono && request.GenerateDual
            ? "both"
            : request.GenerateMono ? "mono" : "dual";

        var options = new Dictionary<string, object?>
        {
            ["endpoint"] = request.Endpoint,
            ["model"] = request.Model,
            ["apiKey"] = request.ApiKey,
            ["sourceLang"] = request.SourceLang,
            ["targetLang"] = request.TargetLang,
            ["template"] = request.Template,
            ["format"] = format,
            ["marker"] = request.Marker ? "true" : "false",
        };

        if (!string.IsNullOrWhiteSpace(request.GlossaryPath))
        {
            options["glossary"] = request.GlossaryPath;
        }

        if (request.MaxTokens is int cap)
        {
            options["maxTokens"] = cap.ToString(CultureInfo.InvariantCulture);
        }

        // 密码仅本次使用、经 stdin 传递，绝不落盘 / 进 argv。
        if (!string.IsNullOrEmpty(request.Password))
        {
            options["password"] = request.Password;
        }

        return options;
    }

    /// <summary>把引擎结果解析为产物 / 降级报告 / 受限披露（内部可见，便于单测）。</summary>
    internal static FullTranslationOutcome ParseOutcome(EngineResult result)
    {
        var artifacts = new List<FullTranslationArtifact>();
        var issues = new List<FullTranslationPageIssue>();
        var protection = FullTranslationSourceProtection.None;
        string? taskDir = null;
        int? degradedPages = null;
        int? unsearchablePages = null;
        int? totalPages = null;

        if (result.Extra.TryGetValue("results", out var resultsElement)
            && resultsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in resultsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                taskDir ??= TryString(entry, "taskDir");

                foreach (var kind in new[] { "mono", "dual", "markdown" })
                {
                    if (TryString(entry, kind) is { Length: > 0 } path)
                    {
                        artifacts.Add(new FullTranslationArtifact(kind, path));
                    }
                }

                if (entry.TryGetProperty("report", out var report) && report.ValueKind == JsonValueKind.Object)
                {
                    if (report.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var page in pages.EnumerateArray())
                        {
                            if (page.ValueKind != JsonValueKind.Object
                                || !page.TryGetProperty("reasons", out var reasons)
                                || reasons.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            var list = new List<string>();
                            foreach (var reason in reasons.EnumerateArray())
                            {
                                if (reason.ValueKind == JsonValueKind.String)
                                {
                                    list.Add(reason.GetString() ?? "");
                                }
                            }

                            if (list.Count > 0)
                            {
                                var number = page.TryGetProperty("page", out var pageNumber)
                                    && pageNumber.ValueKind == JsonValueKind.Number
                                    && pageNumber.TryGetInt32(out var parsed)
                                        ? parsed
                                        : 0;
                                var searchable = !page.TryGetProperty("searchable", out var searchableElement)
                                    || searchableElement.ValueKind != JsonValueKind.False;
                                var degraded = !page.TryGetProperty("degraded", out var degradedElement)
                                    || degradedElement.ValueKind != JsonValueKind.False;
                                issues.Add(new FullTranslationPageIssue(number, list, searchable, degraded));
                            }
                        }
                    }

                    if (report.TryGetProperty("summary", out var summaryElement)
                        && summaryElement.ValueKind == JsonValueKind.Object)
                    {
                        if (TryInt(summaryElement, "degraded_pages") is int degraded)
                        {
                            degradedPages = (degradedPages ?? 0) + degraded;
                        }

                        if (TryInt(summaryElement, "unsearchable_pages") is int unsearchable)
                        {
                            unsearchablePages = (unsearchablePages ?? 0) + unsearchable;
                        }

                        if (TryInt(summaryElement, "total_pages") is int total)
                        {
                            totalPages = (totalPages ?? 0) + total;
                        }
                    }

                    if (report.TryGetProperty("source_protection", out var protectionElement)
                        && protectionElement.ValueKind == JsonValueKind.Object)
                    {
                        var restricted = protectionElement.TryGetProperty("restricted", out var restrictedElement)
                            && restrictedElement.ValueKind == JsonValueKind.True;
                        var disclosure = TryString(protectionElement, "disclosure");
                        protection = new FullTranslationSourceProtection(restricted, disclosure);
                    }
                }
            }
        }

        // 兜底：引擎平铺 outputs 已含产物路径（results 缺失时至少能展示）。
        if (artifacts.Count == 0)
        {
            foreach (var output in result.Outputs)
            {
                var kind = output.Contains("-dual", StringComparison.OrdinalIgnoreCase) ? "dual"
                    : output.Contains("-mono", StringComparison.OrdinalIgnoreCase) ? "mono"
                    : "output";
                artifacts.Add(new FullTranslationArtifact(kind, output));
            }
        }

        var summary = totalPages is int known
            ? new FullTranslationSearchSummary(degradedPages ?? 0, unsearchablePages ?? 0, known)
            : null;

        return new FullTranslationOutcome(
            true, null, false, result.OutputDir, taskDir,
            artifacts, issues, protection, result.Elapsed ?? 0, summary);
    }

    private static string? TryString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;
}
