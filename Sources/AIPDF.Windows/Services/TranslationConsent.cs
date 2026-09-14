// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端同意记录模型（决策 #14 / #21；云端配置复用 (c)(d)(g)(i)）：
//   - 记录只含 provider + endpoint + 密钥指纹 + 授予时间 + TTL，绝不存密钥本体与文档正文；
//   - endpoint 归一为 origin（scheme://host[:port]）：主机名 / 协议 / 端口任一变更即视为换目的地，
//     仅改模型名（端点 origin 与密钥不变）不重置同意；
//   - 密钥只保留 SHA-256 短指纹，用于识别「换 key 即同意失效」，无法从指纹反推密钥。
//
// 红线（决策 #21）：本类型及其序列化结果不得出现密钥 / 正文；指纹是唯一与密钥相关的派生值。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AIPDF.Windows.Services;

/// <summary>
/// 单条云端同意记录。与 <see cref="TranslationCredentialStore"/> 中的凭据一一对应目的地，但只存指纹、不存密钥。
/// <see cref="IsExpired"/> 以授予时间 + TTL 判定：到期视为未同意，需重新征求（决策 (g)）。
/// </summary>
public sealed class TranslationConsent
{
    /// <summary>同意 TTL 默认天数（决策 (g)：默认 180 天）。</summary>
    public const int DefaultTtlDays = 180;

    /// <summary>同意 TTL 下限。</summary>
    public const int MinTtlDays = 1;

    /// <summary>同意 TTL 上限（约 10 年，防手工编辑注入离谱值）。</summary>
    public const int MaxTtlDays = 3650;

    /// <summary>密钥指纹长度（SHA-256 十六进制前 16 位 = 64 bit，足以识别换 key 且不泄露密钥）。</summary>
    public const int FingerprintLength = 16;

    /// <summary>服务商标识（如 <c>deepseek</c> / <c>moonshot</c>）。</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    /// <summary>归一化后的端点 origin（<c>scheme://host[:port]</c>，小写）；变更即需重新同意。</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    /// <summary>密钥短指纹（<see cref="ComputeFingerprint"/>）；仅用于识别换 key，不存密钥本体。</summary>
    [JsonPropertyName("keyFingerprint")]
    public string KeyFingerprint { get; set; } = "";

    /// <summary>同意授予时间（UTC）。</summary>
    [JsonPropertyName("grantedAt")]
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>同意有效期天数（钳制到 [1,3650]，缺省 180）。</summary>
    [JsonPropertyName("ttlDays")]
    public int TtlDays { get; set; } = DefaultTtlDays;

    /// <summary>该记录在 <paramref name="now"/> 时刻是否已过期（含边界：到期即失效）。</summary>
    public bool IsExpired(DateTimeOffset now) => now >= GrantedAt.AddDays(TtlDays);

    /// <summary>
    /// 端点归一化为 origin：<c>scheme://host[:port]</c>（scheme/host 小写，忽略 path / query / userinfo）。
    /// 这样「主机名 / 协议 / 端口」变更会被识别为换目的地，而仅改模型名（路径不同）不重置同意。
    /// 非 http(s) 绝对 URL 回退为去首尾空白的原始串（小写），仍能精确匹配。
    /// </summary>
    public static string NormalizeEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim();
        if (string.IsNullOrEmpty(value)) return "";

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant();
        }

        return value.ToLowerInvariant();
    }

    /// <summary>
    /// 计算密钥短指纹（SHA-256 → 小写十六进制 → 取前 <see cref="FingerprintLength"/> 位）。
    /// 空密钥返回空串（视为无效，调用方应据此判为未同意）。
    /// </summary>
    public static string ComputeFingerprint(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..FingerprintLength];
    }

    /// <summary>TTL 归一化：非正数回退默认 180，正数钳制到 [1,3650]。</summary>
    public static int NormalizeTtlDays(int ttlDays)
        => ttlDays <= 0 ? DefaultTtlDays : Math.Clamp(ttlDays, MinTtlDays, MaxTtlDays);
}
