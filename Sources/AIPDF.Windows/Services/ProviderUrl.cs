// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · URL 双归一化（T3；安全不变量沿用 v1.1 决策 #10）：
//   - 「请求基址」（NormalizeRequestBase）供下游拼装真实请求与模型列表，保留自定义路径 / 版本段 / 端口：
//       OpenAI 兼容：{base}/chat/completions 与 {base}/models —— base 恰好一个版本段
//       （缺则补 v1；重复 /v1/v1 收敛为一个；已有 v4 / v1beta 等原样保留，否则 GLM
//       `https://open.bigmodel.cn/api/paas/v4`、Gemini `.../v1beta` 会被拼坏）；
//       Anthropic：{base}/v1/messages —— base 不以 /v1 结尾，由引擎层拼 /v1/messages。
//   - 「安全 origin」（SecurityOrigin）是凭据寻址与同意哈希的输入，委托 TranslationConsent.NormalizeEndpoint，
//     与 v1.1 结果逐字一致（否则既有密钥 / 同意会被静默孤立）。
//
// ⚠ 两者用途不同、绝不可混用：请求基址决定「发往哪个完整地址」，安全 origin 只决定「是否同一目的地」。
using System.Text.RegularExpressions;

namespace AIPDF.Windows.Services;

/// <summary>
/// 端点 URL 双归一化（T3）。<see cref="NormalizeRequestBase"/> 产出拼接请求用的请求基址；
/// <see cref="SecurityOrigin"/> 产出身份 / 同意判定用的安全 origin。
/// </summary>
public static class ProviderUrl
{
    /// <summary>OpenAI 兼容协议标识（引擎缺省协议，缺失 = openai）；复用 <see cref="TranslationProviderConfig.ProtocolOpenAi"/>。</summary>
    public const string OpenAiProtocol = TranslationProviderConfig.ProtocolOpenAi;

    /// <summary>Anthropic 协议标识；复用 <see cref="TranslationProviderConfig.ProtocolAnthropic"/>。</summary>
    public const string AnthropicProtocol = TranslationProviderConfig.ProtocolAnthropic;

    /// <summary>OpenAI 兼容 base 缺省补全的版本段（<c>{base}/models</c> 可用的前提）。</summary>
    private const string DefaultVersionSegment = "v1";

    /// <summary>Anthropic base 不得保留的尾部版本段（客户端另行拼 <c>/v1/messages</c>）。</summary>
    private const string AnthropicBaseVersionSegment = "v1";

    /// <summary>
    /// 用户整段粘贴完整端点时的可剥离后缀（按「多段优先」匹配，大小写不敏感）：
    /// <c>/v1/chat/completions</c>、<c>/chat/completions</c>、<c>/v1/messages</c>、<c>/messages</c>。
    /// </summary>
    private static readonly string[][] EndpointSuffixes =
    [
        ["chat", "completions"],
        ["completions"],
        ["messages"],
    ];

    /// <summary>版本段形态：<c>v1</c> / <c>v2</c> / <c>v4</c> / <c>v1beta</c>（大小写不敏感）。</summary>
    private static readonly Regex VersionSegmentPattern =
        new(@"^v\d+[a-z]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 归一化「请求基址」：供下游拼接 <c>{base}/chat/completions</c>（OpenAI）、
    /// <c>{base}/v1/messages</c>（Anthropic）与 <c>{base}/models</c>（模型列表，不再追加 /v1）。
    /// 规则：去首尾空白；剥掉粘贴的完整端点后缀；去尾斜杠；scheme / host 小写；丢弃 query 与片段；
    /// OpenAI 兼容 base 确保恰好一个版本段（缺则补 <c>v1</c>，重复 <c>/v1/v1</c> 收敛为一个，
    /// 已有其他版本段如 <c>v4</c> / <c>v1beta</c> 原样保留）；Anthropic base 剥离尾部 <c>/v1</c>。
    /// 自定义路径（如 <c>/llm/v1</c>、<c>/api/paas/v4</c>）绝不塌陷为 origin。
    /// 空串 / 非 http(s) / 缺少可识别主机 / 含 userinfo（内嵌密钥）返回空串，调用方应按校验错误提示处理。
    /// <paramref name="protocol"/> 为空或未知时按 OpenAI 处理（与引擎缺省一致）。
    /// </summary>
    public static string NormalizeRequestBase(string? input, string protocol = OpenAiProtocol)
    {
        var uri = ParseHttpOrSchemeLess(input);
        if (uri is null) return "";

        // 内嵌密钥（https://key:secret@host）绝不进入请求基址（与 ValidateEndpoint 同口径拦截）。
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "";

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        StripEndpointSuffix(segments);

        if (string.Equals(protocol?.Trim(), AnthropicProtocol, StringComparison.OrdinalIgnoreCase))
        {
            // Anthropic：base 不以 /v1 结尾（引擎层拼 /v1/messages），剥掉尾部 v1 段。
            while (segments.Count > 0 && IsVersionSegment(segments[^1], AnthropicBaseVersionSegment))
            {
                segments.RemoveAt(segments.Count - 1);
            }
        }
        else
        {
            // OpenAI 兼容：收敛尾部重复版本段，再确保恰有一个版本段。
            while (segments.Count >= 2
                   && IsVersionSegment(segments[^1], expectedVersion: null)
                   && string.Equals(segments[^1], segments[^2], StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(segments.Count - 1);
            }

            if (segments.Count == 0 || !IsVersionSegment(segments[^1], expectedVersion: null))
            {
                segments.Add(DefaultVersionSegment);
            }
        }

        var authority = uri.Authority.ToLowerInvariant();
        var path = segments.Count == 0 ? "" : "/" + string.Join('/', segments);
        return $"{uri.Scheme.ToLowerInvariant()}://{authority}{path}";
    }

    /// <summary>
    /// 归一化「安全 origin」：<c>scheme://host[:port]</c>（scheme / host 小写，丢 path / query / userinfo），
    /// 非 http(s) 绝对地址回退为去首尾空白的原始串（小写）。与
    /// <see cref="TranslationConsent.NormalizeEndpoint"/> 结果逐字一致——这是凭据目标名与同意哈希的输入，
    /// 重写语义会孤立既有密钥与同意记录，禁止在此处另实现一份。
    /// </summary>
    public static string SecurityOrigin(string? input) => TranslationConsent.NormalizeEndpoint(input);

    /// <summary>
    /// 解析为 http(s) 绝对地址：显式其他协议（<c>ftp:</c> 等）与相对路径直接拒绝；
    /// 缺协议的「主机[:端口]/路径」按设计补 <c>https://</c> 后再解析（§URL 规范化要点「缺协议」行）。
    /// </summary>
    private static Uri? ParseHttpOrSchemeLess(string? raw)
    {
        var value = raw?.Trim() ?? "";
        if (value.Length == 0) return null;

        Uri? uri;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && IsHttpScheme(uri) ? uri : null;
        }

        // 相对路径不是可用端点；只有「主机形态」才允许补协议，避免把 mailto: 等当主机。
        if (value[0] is '/' or '\\' || !LooksLikeSchemeLessHost(value)) return null;

        return Uri.TryCreate("https://" + value, UriKind.Absolute, out uri) && IsHttpScheme(uri) ? uri : null;
    }

    private static bool IsHttpScheme(Uri? uri)
        => uri is not null
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 缺协议输入是否像「主机[:端口][/路径]」：主机部分为 localhost / 含点号的域名或 IPv4 / IPv6 字面量。
    /// </summary>
    private static bool LooksLikeSchemeLessHost(string value)
    {
        var hostish = value.Split('/', 2)[0];
        if (hostish.StartsWith('['))
        {
            var close = hostish.IndexOf(']');
            return close > 0 && hostish.Contains(']', StringComparison.Ordinal); // [::1]:8080
        }

        var colon = hostish.IndexOf(':');
        if (colon >= 0) hostish = hostish[..colon];

        return hostish.Length > 0
               && (hostish.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                   || hostish.Contains('.', StringComparison.Ordinal));
    }

    /// <summary>段是否为版本段；<paramref name="expectedVersion"/> 非空时还要求与该具体版本相等（大小写不敏感）。</summary>
    private static bool IsVersionSegment(string segment, string? expectedVersion)
        => VersionSegmentPattern.IsMatch(segment)
           && (expectedVersion is null || string.Equals(segment, expectedVersion, StringComparison.OrdinalIgnoreCase));

    /// <summary>剥掉一个已知的完整端点后缀（仅剥一次；大小写不敏感）。</summary>
    private static void StripEndpointSuffix(List<string> segments)
    {
        foreach (var suffix in EndpointSuffixes)
        {
            if (!EndsWithSuffix(segments, suffix)) continue;
            segments.RemoveRange(segments.Count - suffix.Length, suffix.Length);
            return;
        }
    }

    private static bool EndsWithSuffix(List<string> segments, string[] suffix)
    {
        if (segments.Count < suffix.Length) return false;

        var offset = segments.Count - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (!string.Equals(segments[offset + i], suffix[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
