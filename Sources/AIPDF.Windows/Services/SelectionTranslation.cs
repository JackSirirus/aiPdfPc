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
/// <param name="SelectedProviderId">面板选中的稳定供应商 id；null 保留本地优先回退（未接线宿主 / 旧调用方行为不变）。</param>
public sealed record SelectionTranslationRequest(
    string Text,
    string SourceLang,
    string TargetLang,
    string Template,
    string? SelectedProviderId = null);

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
    string ApiProtocol = TranslationProviderConfig.ProtocolOpenAi,
    string? GlossaryPath = null,
    int? MaxTokens = null)
{
    /// <summary>
    /// 显式脱敏 <c>ToString()</c>：记录的默认 PrintMembers 会逐个打印位置成员，把 <see cref="ApiKey"/>
    /// 明文带进调试 / 日志 / 异常文本。此覆盖以固定占位替换密钥，密钥只存在于内存字段与子进程 stdin。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051",
        Justification = "由编译器生成的 ToString() 调用。")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Provider = ").Append(Provider)
               .Append(", Endpoint = ").Append(Endpoint)
               .Append(", Model = ").Append(Model)
               .Append(", ApiKey = <redacted>, SourceLang = ").Append(SourceLang)
               .Append(", TargetLang = ").Append(TargetLang)
               .Append(", Template = ").Append(Template);
        if (GlossaryPath is not null) builder.Append(", GlossaryPath = ").Append(GlossaryPath);
        if (MaxTokens is int maxTokens) builder.Append(", MaxTokens = ").Append(maxTokens);
        return true;
    }
}

/// <summary>运行时配置「未就绪」的具体原因（供 UI 给出可操作的引导，不把不同缺口混为一谈）。</summary>
public enum TranslationRuntimeConfigStatus
{
    /// <summary>服务商 / 端点 / 凭据 / 同意均已就绪，配置可用。</summary>
    Configured,

    /// <summary>尚未配置任何服务商。</summary>
    NoProvider,

    /// <summary>服务商缺少端点地址。</summary>
    NoEndpoint,

    /// <summary>标记为「本地」但端点不是 loopback（拒绝，防绕过云端同意）。</summary>
    NotLoopback,

    /// <summary>云端服务商尚未保存密钥。</summary>
    NoCredential,

    /// <summary>云端服务商尚未获得明确同意。</summary>
    NoConsent,
}

/// <summary>
/// 运行时配置解析结果：<see cref="Config"/> 非空即可用；否则 <see cref="Status"/> 指明具体缺口、
/// <see cref="Message"/> 为可直接展示的引导文案，<see cref="Provider"/> / <see cref="Endpoint"/> 供同意弹窗等定位目的地。
/// <see cref="ProviderId"/> 为供应商稳定身份键（T8：凭据 / 同意寻址以此为准；缺省空串 = 回退 <see cref="Provider"/>）。
/// </summary>
public sealed record TranslationRuntimeConfigResolution(
    TranslationRuntimeConfigStatus Status,
    TranslationRuntimeConfig? Config,
    string Provider,
    string Endpoint,
    bool IsCloud,
    string Message,
    string ProviderId = "")
{
    /// <summary>配置是否可用。</summary>
    public bool IsConfigured => Config is not null;

    /// <summary>是否因「云端未同意」而阻塞（宿主据此弹 F5 同意模态）。</summary>
    public bool NeedsConsent => Status == TranslationRuntimeConfigStatus.NoConsent;

    /// <summary>未配置的通用回退文案（保留既有措辞，兼容既有调用方与测试）。</summary>
    public const string NotConfiguredMessage = "翻译服务尚未配置，请在「设置 → 翻译」中配置服务商与语言后重试。";
}

/// <summary>
/// 一次云端首次同意请求（F5 模态展示用）。只含目的地标识，<b>不含密钥</b>；
/// 同意后由宿主从凭据管理器读取密钥并按指纹授予同意。
/// <see cref="ProviderId"/> 为稳定身份键（T8 凭据 / 同意寻址用；空串时宿主回退 <see cref="Provider"/>）。
/// </summary>
public sealed record TranslationConsentRequest(string Provider, string Endpoint, string Model = "", string ProviderId = "");

/// <summary>翻译运行时配置来源（生产实现读取设置 + 系统凭据管理器 + 同意记录）。</summary>
public interface ITranslationRuntimeConfigProvider
{
    /// <summary>返回当前配置；未配置返回 null（等价于 <see cref="Resolve"/> 未就绪）。</summary>
    TranslationRuntimeConfig? Get();

    /// <summary>
    /// 同意代次：每次云端同意被撤销时递增。运行中的云端任务据此在代次变化时取消，
    /// 停止继续向已撤回同意的服务商发送请求。默认 0（无同意来源 / 本地提供者）。
    /// </summary>
    int ConsentGeneration => 0;

    /// <summary>
    /// 解析运行时配置，并区分「未就绪」的具体原因。默认实现仅区分「可用 / 未配置」，
    /// 生产实现（<c>SettingsTranslationRuntimeConfigProvider</c>）覆盖为细分原因。
    /// <paramref name="selectedProviderId"/> 为面板 / 调用方选中的稳定供应商 id：生产实现据此选择目的地；
    /// 默认实现用它判定本地 / 云端（稳定 id <c>local</c> 优先，旧数据回退显示名）。
    /// </summary>
    TranslationRuntimeConfigResolution Resolve(string? selectedProviderId = null)
        => Get() is { } config
            ? new TranslationRuntimeConfigResolution(
                TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint,
                !TranslationProviderConfig.IsLocalService(selectedProviderId, config.Provider), "")
            : new TranslationRuntimeConfigResolution(
                TranslationRuntimeConfigStatus.NoProvider, null, "", "", false,
                TranslationRuntimeConfigResolution.NotConfiguredMessage);
}

/// <summary>默认提供者：尚未接入设置/凭据 → 恒为未配置。</summary>
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

        // v1.2 修复：目的地必须随「面板选中的供应商」解析（Read 路径），否则云端选择被本地优先回退吞掉。
        var resolution = _configProvider.Resolve(request.SelectedProviderId);
        if (resolution.Config is not { } config)
        {
            return new SelectionTranslationResult(false, "", resolution.Message, 0);
        }

        var options = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["sourceLang"] = string.IsNullOrWhiteSpace(request.SourceLang) ? config.SourceLang : request.SourceLang,
            ["targetLang"] = string.IsNullOrWhiteSpace(request.TargetLang) ? config.TargetLang : request.TargetLang,
            ["template"] = string.IsNullOrWhiteSpace(request.Template) ? config.Template : request.Template,
            ["endpoint"] = config.Endpoint,
            ["model"] = config.Model,
            // 协议是传输层字段（T4 引擎契约）：openai → /chat/completions，anthropic → /v1/messages；
            // provider 名 / 显示名绝不进入 options。
            ["apiProtocol"] = config.ApiProtocol,
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
