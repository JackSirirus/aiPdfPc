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
    string ApiProtocol = TranslationProviderConfig.ProtocolOpenAi,
    string? GlossaryPath = null,
    int? MaxTokens = null,
    bool GenerateMono = true,
    bool GenerateDual = true,
    string? Password = null,
    bool Marker = true,
    bool ExportMarkdown = false,
    string? CustomPrompt = null)
{
    /// <summary>
    /// 显式脱敏 <c>ToString()</c>：记录的默认 PrintMembers 会逐个打印位置成员，把 <see cref="ApiKey"/>
    /// 与源文档 <see cref="Password"/> 明文带进调试 / 日志 / 异常文本。两者一律以固定占位替换。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051",
        Justification = "由编译器生成的 ToString() 调用。")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("FilePath = ").Append(FilePath)
               .Append(", OutputDir = ").Append(OutputDir)
               .Append(", SourceLang = ").Append(SourceLang)
               .Append(", TargetLang = ").Append(TargetLang)
               .Append(", Template = ").Append(Template)
               .Append(", Endpoint = ").Append(Endpoint)
               .Append(", Model = ").Append(Model)
               .Append(", ApiKey = <redacted>")
               .Append(", GenerateMono = ").Append(GenerateMono)
               .Append(", GenerateDual = ").Append(GenerateDual)
               .Append(", Password = <redacted>")
               .Append(", Marker = ").Append(Marker)
               .Append(", ExportMarkdown = ").Append(ExportMarkdown);
        if (GlossaryPath is not null) builder.Append(", GlossaryPath = ").Append(GlossaryPath);
        if (MaxTokens is int maxTokens) builder.Append(", MaxTokens = ").Append(maxTokens);
        if (CustomPrompt is not null) builder.Append(", CustomPrompt = ").Append(CustomPrompt);
        return true;
    }
}

/// <summary>
/// 输出形态 → 引擎 <c>format</c> 值（决策 #4）。mono/dual 组合为 <c>both/mono/dual</c>；
/// 勾选 Markdown 时追加 <c>+md</c>（仅 Markdown 时为 <c>md</c>），与后端
/// <c>engine._TRANSLATE_FORMATS</c> 的键一一对应。
/// </summary>
public static class TranslationOutputFormats
{
    /// <summary>把 mono / dual / markdown 复选组合映射为引擎 format 值。</summary>
    public static string ToEngineFormat(bool mono, bool dual, bool markdown)
    {
        var shape = mono && dual ? "both" : mono ? "mono" : dual ? "dual" : "";
        if (!markdown)
        {
            return shape;
        }

        return shape.Length == 0 ? "md" : shape + "+md";
    }
}

/// <summary>
/// 组装 `translate` op 引擎 options 的唯一实现（整篇服务与批量工具页共用，保证口径一致）。
/// 值一律字符串（<c>maxTokens</c> / <c>marker</c> 亦然）；密码 / 密钥仅经此处进入子进程 stdin。
/// <c>apiProtocol</c>（openai | anthropic）是传输层字段：引擎据它选择路径 / 鉴权（T4 契约，
/// 缺失或未知即按 openai）；provider 名 / 显示名绝不进入 options。
/// </summary>
internal static class TranslationEngineOptions
{
    /// <summary>按已解析的运行时 / 配置值组装选项字典。</summary>
    public static Dictionary<string, object?> Build(
        string endpoint,
        string model,
        string apiKey,
        string apiProtocol,
        string sourceLang,
        string targetLang,
        string template,
        bool generateMono,
        bool generateDual,
        bool exportMarkdown,
        bool marker,
        string? glossaryPath = null,
        string? customPrompt = null,
        int? maxTokens = null,
        string? password = null)
    {
        var options = new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["model"] = model,
            ["apiKey"] = apiKey,
            ["apiProtocol"] = apiProtocol,
            ["sourceLang"] = sourceLang,
            ["targetLang"] = targetLang,
            ["template"] = template,
            ["format"] = TranslationOutputFormats.ToEngineFormat(generateMono, generateDual, exportMarkdown),
            ["marker"] = marker ? "true" : "false",
        };

        if (!string.IsNullOrWhiteSpace(glossaryPath))
        {
            options["glossary"] = glossaryPath;
        }

        // 自定义模板正文（决策 #10）：用户填写的 system 指令；引擎据此构建 system+user。
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            options["customPrompt"] = customPrompt;
        }

        if (maxTokens is int cap)
        {
            options["maxTokens"] = cap.ToString(CultureInfo.InvariantCulture);
        }

        // 密码仅本次使用、经 stdin 传递，绝不落盘 / 进 argv。
        if (!string.IsNullOrEmpty(password))
        {
            options["password"] = password;
        }

        return options;
    }
}

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
/// 溢出 / 裁切聚合（与引擎 <c>results[].overflow</c> 对齐，R5a/R5-7）：
/// <see cref="OverflowPages"/> = 命中 <c>overflow_margin</c>(S2) 或 <c>clip</c>(S3) 的页数；
/// <see cref="ClippedPages"/> = 其中命中 <c>clip</c> 的页数。引擎未给出该聚合时为 null（不伪造结论）。
/// </summary>
public sealed record FullTranslationOverflowSummary(int OverflowPages, int ClippedPages)
{
    /// <summary>是否记录到任何溢出 / 裁切。</summary>
    public bool HasAny => OverflowPages > 0 || ClippedPages > 0;
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
    FullTranslationSearchSummary? SearchSummary = null,
    FullTranslationOverflowSummary? Overflow = null)
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
        => TranslationEngineOptions.Build(
            request.Endpoint,
            request.Model,
            request.ApiKey,
            request.ApiProtocol,
            request.SourceLang,
            request.TargetLang,
            request.Template,
            request.GenerateMono,
            request.GenerateDual,
            request.ExportMarkdown,
            request.Marker,
            request.GlossaryPath,
            request.CustomPrompt,
            request.MaxTokens,
            request.Password);

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
        FullTranslationOverflowSummary? overflow = null;

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

                // R5a/R5-7：逐文件聚合溢出 / 裁切（引擎放在 results[].overflow；兼容 report.overflow）。
                if (ParseOverflow(entry) is { } fileOverflow)
                {
                    overflow = new FullTranslationOverflowSummary(
                        (overflow?.OverflowPages ?? 0) + fileOverflow.OverflowPages,
                        (overflow?.ClippedPages ?? 0) + fileOverflow.ClippedPages);
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
            artifacts, issues, protection, result.Elapsed ?? 0, summary, overflow);
    }

    /// <summary>
    /// 解析溢出 / 裁切聚合（R5a）：引擎实际放在 <c>results[].overflow</c>（<c>{pages, clipPages, warn}</c>），
    /// 兼容备选位置 <c>results[].report.overflow</c>；接受对象（含别名）或整数形式；无法识别时返回 null。
    /// </summary>
    private static FullTranslationOverflowSummary? ParseOverflow(JsonElement entry)
    {
        if (!entry.TryGetProperty("overflow", out var element)
            && !(entry.TryGetProperty("report", out var report)
                 && report.ValueKind == JsonValueKind.Object
                 && report.TryGetProperty("overflow", out element)))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var total))
        {
            return new FullTranslationOverflowSummary(total, 0);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var overflowPages = TryIntAny(element, "pages", "overflow", "overflow_count", "overflow_pages", "overflows");
        var clippedPages = TryIntAny(element, "clipPages", "clipped", "clip", "clip_count", "clipped_pages", "clips");
        if (overflowPages is null && clippedPages is null)
        {
            return null;
        }

        return new FullTranslationOverflowSummary(overflowPages ?? 0, clippedPages ?? 0);
    }

    private static int? TryIntAny(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }

        return null;
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
