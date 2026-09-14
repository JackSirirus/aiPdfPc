// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译服务层：
//   - 把「选中文本」经既有 EngineBridge 的轻量 `translate_text` op 翻回一个字符串（不落任务目录、不产出 PDF）；
//   - 运行时配置（服务商 / 端点 / 模型 / 密钥 / 语言 / 模板）由 ITranslationRuntimeConfigProvider 提供；
//     本任务不写 AppSettings（归 T13/T17），无配置时返回明确的可读错误，绝不静默或伪造译文；
//   - 密钥只在本进程内从配置提供者 → EngineBridge options → 子进程 stdin 传递，绝不写入 argv / 环境变量 / 日志 / 文件。
//   - 「替换标注」只依赖 ISelectionSidecarStore 抽象；真实 sidecar 落盘由 T19 实现。
using System.Text.Json;
using AIPDF.Windows.Bridge;

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

/// <summary>一条划词替换标注（写入独立 sidecar，绝不写回源 PDF）。</summary>
public sealed record SelectionSidecarEntry(
    string SourcePath,
    int PageNumber,
    string SourceText,
    string Translation,
    string SourceLang,
    string TargetLang);

/// <summary>sidecar 保存结果。</summary>
public sealed record SelectionSidecarResult(bool Ok, string Message);

/// <summary>
/// 「替换标注」持久化抽象（T19 实现真实 sidecar：源目录 + FILE_ATTRIBUTE_HIDDEN、只读回退 %TEMP%、
/// 仅会话内有效）。T14 只把卡片按钮接到本接口，不自行落盘。
/// </summary>
public interface ISelectionSidecarStore
{
    /// <summary>保存一条替换标注。实现不得写回源 PDF。</summary>
    Task<SelectionSidecarResult> SaveAsync(SelectionSidecarEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>占位实现：T19 接入前明确返回「尚未接入」，不假装成功。</summary>
public sealed class NullSelectionSidecarStore : ISelectionSidecarStore
{
    /// <inheritdoc />
    public Task<SelectionSidecarResult> SaveAsync(SelectionSidecarEntry entry, CancellationToken cancellationToken = default)
        => Task.FromResult(new SelectionSidecarResult(false, "替换标注存储尚未接入，稍后版本提供。"));
}
