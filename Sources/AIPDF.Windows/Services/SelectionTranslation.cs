// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译服务层：
//   - 把「选中文本」经既有 EngineBridge 的轻量 `translate_text` op 翻回一个字符串（不落任务目录、不产出 PDF）；
//   - 运行时配置（服务商 / 端点 / 模型 / 密钥 / 语言 / 模板）由 ITranslationRuntimeConfigProvider 提供；
//     本任务不写 AppSettings（归 T13/T17），无配置时返回明确的可读错误，绝不静默或伪造译文；
//   - 密钥只在本进程内从配置提供者 → EngineBridge options → 子进程 stdin 传递，绝不写入 argv / 环境变量 / 日志 / 文件。
//   - 「替换标注」只依赖 ISelectionSidecarStore 抽象；真实 sidecar 落盘由 T19 实现（SelectionSidecarStore.cs）。
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Services;

/// <summary>一次划词翻译请求（语言 / 模板为空则回退运行时配置）。</summary>
public sealed record SelectionTranslationRequest(string Text, string SourceLang, string TargetLang, string Template);

/// <summary>划词翻译结果。<see cref="Ok"/> 为 false 时 <see cref="Error"/> 为可直接展示的原因。</summary>
public sealed record SelectionTranslationResult(bool Ok, string Translation, string? Error, double ElapsedSeconds);

/// <summary>划词翻译执行器抽象（便于单测与后续替换真实实现）。</summary>
public interface ISelectionTranslationService
{
    /// <summary>翻译一段选中文本。实现必须捕获传输/取消异常并转成失败结果，不向 UI 抛。</summary>
    Task<SelectionTranslationResult> TranslateAsync(SelectionTranslationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 翻译运行时配置。密钥只在此记录的内存字段中短暂存在，经 <see cref="EngineBridge"/> options 走 stdin。
/// <b>禁止</b>把本记录序列化进 settings / recent / session / 日志 / 预设。
/// </summary>
public sealed record TranslationRuntimeConfig(
    string Provider,
    string Endpoint,
    string Model,
    string ApiKey,
    string SourceLang,
    string TargetLang,
    string Template,
    string? GlossaryPath = null,
    int? MaxTokens = null);

/// <summary>翻译运行时配置来源（T13/T17 接入设置与凭据管理器后填充）。</summary>
public interface ITranslationRuntimeConfigProvider
{
    /// <summary>返回当前配置；未配置返回 null。</summary>
    TranslationRuntimeConfig? Get();
}

/// <summary>默认提供者：尚未接入设置/凭据（T13/T17 之前）→ 恒为未配置。</summary>
public sealed class NullTranslationRuntimeConfigProvider : ITranslationRuntimeConfigProvider
{
    /// <inheritdoc />
    public TranslationRuntimeConfig? Get() => null;
}

/// <summary>
/// 经 <see cref="EngineBridge"/> 的 `translate_text` op 执行划词翻译。
/// </summary>
public sealed class EngineSelectionTranslationService : ISelectionTranslationService
{
    private readonly EngineBridge _bridge;
    private readonly ITranslationRuntimeConfigProvider _configProvider;

    public EngineSelectionTranslationService(
        EngineBridge bridge,
        ITranslationRuntimeConfigProvider? configProvider = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _configProvider = configProvider ?? new NullTranslationRuntimeConfigProvider();
    }

    /// <inheritdoc />
    public async Task<SelectionTranslationResult> TranslateAsync(
        SelectionTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _configProvider.Get();
        if (config is null)
        {
            return new SelectionTranslationResult(
                false, "", "翻译服务尚未配置，请在「设置 → 翻译」中配置服务商与语言后重试。", 0);
        }

        var options = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["sourceLang"] = string.IsNullOrWhiteSpace(request.SourceLang) ? config.SourceLang : request.SourceLang,
            ["targetLang"] = string.IsNullOrWhiteSpace(request.TargetLang) ? config.TargetLang : request.TargetLang,
            ["template"] = string.IsNullOrWhiteSpace(request.Template) ? config.Template : request.Template,
            ["endpoint"] = config.Endpoint,
            ["model"] = config.Model,
            ["apiKey"] = config.ApiKey,
        };
        if (!string.IsNullOrWhiteSpace(config.GlossaryPath))
        {
            options["glossary"] = config.GlossaryPath;
        }

        if (config.MaxTokens is int cap)
        {
            options["maxTokens"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var engineRequest = new EngineRequest { Operation = "translate_text", Options = options };

        try
        {
            var result = await _bridge.ExecuteAsync(engineRequest, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return new SelectionTranslationResult(false, "", result.Error ?? "翻译失败，请稍后重试。", 0);
            }

            var translation = TryGetString(result, "translation");
            if (string.IsNullOrEmpty(translation))
            {
                return new SelectionTranslationResult(false, "", "翻译服务未返回译文（可能被拒译或已降级）。", 0);
            }

            return new SelectionTranslationResult(true, translation, null, result.Elapsed ?? 0);
        }
        catch (OperationCanceledException)
        {
            return new SelectionTranslationResult(false, "", "翻译已取消。", 0);
        }
        catch (EngineBridgeException error)
        {
            return new SelectionTranslationResult(false, "", error.Message, 0);
        }
    }

    private static string? TryGetString(EngineResult result, string name)
        => result.Extra.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// 一条划词替换标注（仅存在于会话内的独立 sidecar，绝不写回源 PDF）。
/// 只存 <c>{页码, 坐标, 译文, 语言对, 时间戳}</c>——<b>不存原文全文</b>（设计 §sidecar 标注文件）。
/// <see cref="PageIndex"/> 为 0 起（与阅读器 / 划词卡片一致）；<see cref="Rect"/> 为 PDF 点、左上原点，与批注同一口径。
/// </summary>
public sealed record SelectionSidecarAnnotation(
    int PageIndex,
    PdfRect Rect,
    string Translation,
    string LangPair,
    DateTimeOffset Timestamp);

/// <summary>sidecar 保存结果。</summary>
public sealed record SelectionSidecarResult(bool Ok, string Message);

/// <summary>
/// 「替换标注」sidecar 抽象。真实实现（T19）：
/// <list type="bullet">
///   <item>优先写源目录 <c>.{源文件名}.transl_sidecar.json</c> 并置 <see cref="System.IO.FileAttributes.Hidden"/>；</item>
///   <item>源目录不可写时回退 <c>%TEMP%\AIPDF-Sidecar\{hash}.json</c>；</item>
///   <item>仅会话内有效——应用退出 <see cref="Clear"/>，下次打开不自动加载旧 sidecar；</item>
///   <item>绝不写回源 PDF，绝不缓存原文全文。</item>
/// </list>
/// <see cref="ActiveDocument"/> 界定 <see cref="Add"/>/<see cref="Get"/>/<see cref="GetAll"/> 的作用域；
/// <see cref="ClearFor"/> 按文档路径清理（关闭标签）。
/// </summary>
public interface ISelectionSidecarStore
{
    /// <summary>当前活动文档（规范化全路径）；无文档为 null。切换标签 / 打开文档时更新。</summary>
    string? ActiveDocument { get; set; }

    /// <summary>保存一条替换标注（<paramref name="pageNo"/> 0 起）。实现不得写回源 PDF。</summary>
    SelectionSidecarResult Add(int pageNo, PdfRect rect, string translation, string langPair);

    /// <summary>取当前文档某页的全部替换标注（阅读视图叠加用）。</summary>
    IReadOnlyList<SelectionSidecarAnnotation> Get(int pageNo);

    /// <summary>取当前文档的全部替换标注（文档打开时投影到阅读视图）。</summary>
    IReadOnlyList<SelectionSidecarAnnotation> GetAll();

    /// <summary>清空整个会话的所有 sidecar 并删除落盘文件（应用退出）。</summary>
    void Clear();

    /// <summary>清除指定文档的 sidecar 并删除其落盘文件（关闭标签）。</summary>
    void ClearFor(string documentPath);
}

/// <summary>占位实现：T19 接入前明确返回「尚未接入」，不假装成功（读操作返回空）。</summary>
public sealed class NullSelectionSidecarStore : ISelectionSidecarStore
{
    /// <inheritdoc />
    public string? ActiveDocument { get; set; }

    /// <inheritdoc />
    public SelectionSidecarResult Add(int pageNo, PdfRect rect, string translation, string langPair)
        => new(false, "替换标注存储尚未接入，稍后版本提供。");

    /// <inheritdoc />
    public IReadOnlyList<SelectionSidecarAnnotation> Get(int pageNo) => [];

    /// <inheritdoc />
    public IReadOnlyList<SelectionSidecarAnnotation> GetAll() => [];

    /// <inheritdoc />
    public void Clear()
    {
    }

    /// <inheritdoc />
    public void ClearFor(string documentPath)
    {
    }
}
