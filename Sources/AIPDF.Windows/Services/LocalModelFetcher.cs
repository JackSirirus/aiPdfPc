// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · 模型列表拉取（T5）：「拉取模型」按钮的 HTTP 实现（GET .../models）。
//
// 路径口径（T3 ProviderUrl 双归一化的请求基址）：
//   - OpenAI 兼容 base 已含版本段（v1 / v4 / v1beta，T3 保证）→ 直接拼 {base}/models，
//     绝不再追加 /v1（杜绝 /v1/v1/models）；
//   - Anthropic base 按 T3 约定【不含】/v1（引擎层拼 /v1/messages）→ 补齐 {base}/v1/models，
//     与设计稿「拉取模型调 GET {base}/v1/models」及引擎 /v1/messages 同构。
//   本类对入参做防御性再归一化（幂等）：调用方即使误传未归一化地址（如 http://localhost:11434），
//   也不会拼出缺版本段或 /v1/v1 的错误路径。
//
// 密钥口径（需求总结 v1.2 规则 7 与 §「测试连接」「拉取模型」的安全规则）：
//   - 本地 loopback → 一律不携带密钥（Ollama / LM Studio / vLLM / llama.cpp 默认无鉴权），即使调用方传入；
//   - 云端 → 按协议携带密钥（openai：Authorization: Bearer；anthropic：x-api-key + anthropic-version），
//     前提是调用方只对「用户已显式保存的端点」传入密钥；
//   - apiKey 为空 = 未确认端点 → 不发任何鉴权头（规则 7：不自动发送 key 到未确认端点）；
//   - 非 loopback 的明文 http 直接拒绝（防 Bearer 明文传输）。
//
// 网络口径：单次尝试 10s 超时；超时 / 传输失败重试 2 次（共 3 次尝试）；HTTP 状态错误不重试。
// 失败一律抛结构化 LocalModelFetchException，绝不回退发送文档内容；密钥绝不进入异常文本 / 日志。
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIPDF.Windows.Services;

/// <summary>拉取模型列表失败的类别（结构化错误：UI 按类别给提示并允许手动输入模型名）。</summary>
public enum LocalModelFetchFailureKind
{
    /// <summary>401：密钥缺失 / 无效。</summary>
    Auth,

    /// <summary>403：端点拒绝访问。</summary>
    Forbidden,

    /// <summary>404：端点不存在或未实现模型列表。</summary>
    NotFound,

    /// <summary>其他非成功状态码。</summary>
    HttpStatus,

    /// <summary>请求超时（已按上限重试）。</summary>
    Timeout,

    /// <summary>传输层失败（DNS / 连接被拒等，已按上限重试）。</summary>
    Network,

    /// <summary>响应不是 OpenAI 形状的 JSON（<c>{"data":[{"id":...}]}</c>）。</summary>
    InvalidResponse,

    /// <summary>非 loopback 的明文 http 端点（拒绝发起请求）。</summary>
    InsecureEndpoint,
}

/// <summary>
/// 模型列表拉取失败的结构化异常。消息只含状态码等非敏感信息；密钥绝不进入异常文本
/// （传输层 InnerException 为真实网络错误，不含请求头）。
/// </summary>
public sealed class LocalModelFetchException : Exception
{
    /// <summary>按失败类别构造（消息不得包含密钥）。</summary>
    public LocalModelFetchException(
        LocalModelFetchFailureKind kind, string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>失败类别。</summary>
    public LocalModelFetchFailureKind Kind { get; }

    /// <summary>HTTP 状态码（仅状态类失败存在）。</summary>
    public int? StatusCode { get; }
}

/// <summary>
/// OpenAI 兼容 <c>{base}/models</c> 模型列表拉取（T5）。本地服务零密钥外发；云端仅对已保存端点
/// 携带符合协议的鉴权头。详见文件头口径说明。
/// </summary>
public sealed class LocalModelFetcher : IDisposable
{
    /// <summary>单次尝试超时（需求：10s；超时后重试，共 <see cref="MaxAttempts"/> 次尝试）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>总尝试次数（1 次初始 + 2 次重试），对齐「10s 超时 + 2 次重试」。</summary>
    public const int MaxAttempts = 3;

    /// <summary>续拉页数上限（防上游游标异常造成失控请求；正常服务远达不到）。</summary>
    private const int MaxPages = 50;

    /// <summary>Anthropic Messages API 版本头（与引擎侧 T4 的 Anthropic 分支一致）。</summary>
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>游标属性名：响应根对象含任一字符串非空值即续拉；否则单页。</summary>
    private static readonly string[] CursorPropertyNames = ["next", "cursor"];

    private readonly HttpClient _http;
    private readonly Func<string, IPAddress[]>? _resolveHost;
    private bool _disposed;

    /// <summary>默认构造：自建 <see cref="HttpClient"/>，超时 <see cref="DefaultTimeout"/>。
    /// <paramref name="handler"/> 供测试注入内存替身（生产为 null）。</summary>
    public LocalModelFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        : this(handler, timeout, null)
    {
    }

    /// <summary>测试专用：额外注入主机名解析器，用于确定性地覆盖 localhost 的 loopback 判定。</summary>
    internal LocalModelFetcher(HttpMessageHandler? handler, TimeSpan? timeout, Func<string, IPAddress[]>? resolveHost)
    {
        _resolveHost = resolveHost;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>当前请求超时（单次尝试）。</summary>
    public TimeSpan Timeout => _http.Timeout;

    /// <summary>
    /// 释放内部 <see cref="HttpClient"/>（幂等）。测试注入的 <see cref="HttpMessageHandler"/>
    /// 由调用方负责处置（构造时 <c>disposeHandler: false</c>），本类不代管。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    /// <summary>
    /// 拉取模型列表：<c>GET {请求基址}/models</c>（Anthropic 协议为 <c>{基址}/v1/models</c>）。
    /// </summary>
    /// <param name="requestBase">
    /// 端点请求基址（T3 <see cref="ProviderUrl.NormalizeRequestBase"/> 口径；本方法会幂等地再归一化，
    /// 原始地址亦可）。空/无效输入抛 <see cref="ArgumentException"/>。
    /// </param>
    /// <param name="apiKey">
    /// 已保存的 API Key。<b>仅当该 provider + endpoint 已由用户在设置中显式保存（已确认端点）时才传入</b>；
    /// null / 空 = 未确认端点或本地服务 → 不发送任何鉴权头（规则 7）。loopback 端点即使传入也不发送。
    /// </param>
    /// <param name="protocol">API 协议（<c>openai</c> | <c>anthropic</c>；缺失/未知按 openai，与引擎缺省一致）。</param>
    /// <param name="cancellationToken">取消令牌；用户取消不视为超时、不重试。</param>
    /// <returns>按响应顺序去重的模型 id 列表（可能为空）。</returns>
    /// <exception cref="LocalModelFetchException">结构化失败（401/403/404/超时/网络/非 JSON 形状/明文 http）。</exception>
    public async Task<IReadOnlyList<string>> FetchAsync(
        string requestBase, string? apiKey, string protocol, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestBase);

        // 协议缺失/未知按 openai（与引擎缺省一致）；显式归一为常量，避免把 null 透传给下游。
        var effectiveProtocol = protocol ?? ProviderUrl.OpenAiProtocol;
        var anthropic = string.Equals(effectiveProtocol, ProviderUrl.AnthropicProtocol, StringComparison.OrdinalIgnoreCase);
        var baseUrl = ProviderUrl.NormalizeRequestBase(requestBase, effectiveProtocol);
        if (baseUrl.Length == 0)
        {
            throw new ArgumentException("请求基址无效，请检查端点地址。", nameof(requestBase));
        }

        var loopback = _resolveHost is null
            ? TranslationProviderConfig.IsLoopbackEndpoint(baseUrl)
            : TranslationProviderConfig.IsLoopbackEndpoint(baseUrl, _resolveHost);

        if (!loopback && baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalModelFetchException(
                LocalModelFetchFailureKind.InsecureEndpoint,
                "云端端点必须使用 https，已拒绝在明文 http 上发起请求。");
        }

        // 规则 7：loopback 一律零密钥外发；apiKey 为空 = 未确认端点 → 不带鉴权头。
        var keyToSend = loopback ? null : apiKey;

        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var listPath = anthropic ? "/v1/models" : "/models";
            var uri = cursor is null
                ? $"{baseUrl}{listPath}"
                : $"{baseUrl}{listPath}?cursor={Uri.EscapeDataString(cursor)}";

            var body = await SendWithRetryAsync(uri, keyToSend, effectiveProtocol, cancellationToken).ConfigureAwait(false);
            var next = AppendModels(body, models, seen);

            // 无游标 / 游标不前进（含重复）→ 单页或已到末页，停止续拉。
            if (string.IsNullOrEmpty(next) || string.Equals(next, cursor, StringComparison.Ordinal)) break;
            cursor = next;
        }

        return models;
    }

    /// <summary>
    /// 单次 GET + 重试：超时（非用户取消）与传输失败重试至 <see cref="MaxAttempts"/>；
    /// HTTP 状态错误是确定性答案，不重试。
    /// </summary>
    private async Task<string> SendWithRetryAsync(
        string uri, string? apiKey, string protocol, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuth(request, protocol, apiKey);

            try
            {
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) throw StatusFailure(response.StatusCode);

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout 超时（用户未取消）→ 重试；超限后抛结构化超时错误。
                if (attempt >= MaxAttempts) throw TimeoutFailure(attempt);
            }
            catch (HttpRequestException exception)
            {
                if (attempt >= MaxAttempts) throw NetworkFailure(attempt, exception);
            }
        }
    }

    /// <summary>按协议附加鉴权头（仅已确认端点会调用到；<paramref name="apiKey"/> 为空时不附加任何头）。</summary>
    private static void ApplyAuth(HttpRequestMessage request, string protocol, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        if (string.Equals(protocol, ProviderUrl.AnthropicProtocol, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>
    /// 解析一页响应：追加 <c>data[].id</c>（去重、保序），返回下一游标（无则 null）。
    /// 非 JSON / 非 OpenAI 形状抛 <see cref="LocalModelFetchFailureKind.InvalidResponse"/>。
    /// </summary>
    private static string? AppendModels(string body, List<string> models, HashSet<string> seen)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new LocalModelFetchException(
                LocalModelFetchFailureKind.InvalidResponse, "拉取模型列表失败：响应不是有效的 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new LocalModelFetchException(
                    LocalModelFetchFailureKind.InvalidResponse,
                    "拉取模型列表失败：响应不是 OpenAI 形状（缺少 data 数组）。");
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;

                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value)) models.Add(value);
            }

            return ReadCursor(root);
        }
    }

    /// <summary>读取根对象上的下一游标（<c>next</c> / <c>cursor</c>，字符串且非空）。</summary>
    private static string? ReadCursor(JsonElement root)
    {
        foreach (var name in CursorPropertyNames)
        {
            if (!root.TryGetProperty(name, out var cursor) || cursor.ValueKind != JsonValueKind.String) continue;

            var value = cursor.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    /// <summary>非成功状态码 → 结构化错误（401 / 403 / 404 单列；其余归 HttpStatus）。</summary>
    private static LocalModelFetchException StatusFailure(HttpStatusCode status)
    {
        var code = (int)status;
        var kind = code switch
        {
            401 => LocalModelFetchFailureKind.Auth,
            403 => LocalModelFetchFailureKind.Forbidden,
            404 => LocalModelFetchFailureKind.NotFound,
            _ => LocalModelFetchFailureKind.HttpStatus,
        };

        var message = kind switch
        {
            LocalModelFetchFailureKind.Auth =>
                "拉取模型列表失败：服务端返回 401（未授权）。请检查 API Key 是否已保存且有效，或手动输入模型名。",
            LocalModelFetchFailureKind.Forbidden =>
                "拉取模型列表失败：服务端返回 403（拒绝访问）。该端点可能需要其他权限，可手动输入模型名。",
            LocalModelFetchFailureKind.NotFound =>
                "拉取模型列表失败：服务端返回 404（未找到模型列表端点）。请检查地址是否包含正确的版本路径，或手动输入模型名。",
            _ => $"拉取模型列表失败：服务端返回 {code}（{status}）。",
        };

        return new LocalModelFetchException(kind, message, code);
    }

    /// <summary>超时失败（消息不含任何密钥）。</summary>
    private static LocalModelFetchException TimeoutFailure(int attempts)
        => new(
            LocalModelFetchFailureKind.Timeout,
            $"拉取模型列表失败：请求超时（已尝试 {attempts} 次）。请检查地址与网络后重试。");

    /// <summary>传输失败（inner 为无害的真实网络错误，不含请求头 / 密钥）。</summary>
    private static LocalModelFetchException NetworkFailure(int attempts, HttpRequestException inner)
        => new(
            LocalModelFetchFailureKind.Network,
            $"拉取模型列表失败：网络请求失败（已尝试 {attempts} 次）。请检查地址与网络后重试。",
            innerException: inner);
}
