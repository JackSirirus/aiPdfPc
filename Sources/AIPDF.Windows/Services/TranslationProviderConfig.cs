// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端连接配置模型（决策 #9 / (a)(d)(j)；T23 设置→翻译→云端服务商）：
//   - 只存 provider + endpoint + model（连接参数），绝不存密钥 / 正文；
//   - endpoint 保留完整 URL（path 决定 API 版本，不归一为 origin），但拒绝内嵌密钥：
//     userinfo（https://key:secret@host）或 key/token 查询参数（决策 (j)），防止设置漫游泄漏；
//   - 同一 provider + endpoint origin 视为同一目的地（与同意 / 凭据寻址一致），设置层去重。
//
// 红线（决策 #21）：本类型及其序列化结果不得出现密钥；密钥交 TranslationCredentialStore。
using System.Text.Json.Serialization;

namespace AIPDF.Windows.Services;

/// <summary>
/// 云端翻译连接配置：服务商标识 + 端点 + 模型名。存 <see cref="AppSettings.TranslationProviders"/>，
/// 与 <see cref="TranslationConsent"/>（同意）和 <see cref="TranslationCredentialStore"/>（密钥）分离。
/// </summary>
public sealed class TranslationProviderConfig
{
    /// <summary>端点内嵌密钥时的统一提示（决策 (j)）。</summary>
    public const string EmbeddedSecretMessage = "密钥请保存在系统凭据管理器中，不要写在端点地址里。";

    /// <summary>
    /// 非 loopback 端点使用明文 <c>http://</c> 时的提示：云端请求的
    /// <c>Authorization: Bearer &lt;key&gt;</c> 会在链路上明文传输；http 仅允许本机地址。
    /// </summary>
    public const string PlainHttpMessage =
        "云端端点必须使用 https（http 仅限 127.0.0.1 / ::1 / localhost 本机地址），否则 API Key 会以明文传输。";

    /// <summary>
    /// 「本地」服务商标识（大小写不敏感）。标记为「本地」时端点必须是 loopback
    /// （127.0.0.1 / ::1 / localhost），否则不得以本地名义绕过云端同意（§本地引擎 loopback 校验）。
    /// </summary>
    public const string LocalProviderName = "local";

    /// <summary>被识别为密钥的查询参数名（大小写不敏感）。</summary>
    private static readonly string[] SecretQueryNames =
        ["key", "api_key", "apikey", "token", "access_token", "secret", "authorization"];

    /// <summary>loopback 主机名白名单（IP 形式另经 <see cref="System.Net.IPAddress.IsLoopback"/> 判定）。</summary>
    private static readonly string[] LoopbackHostNames = ["localhost"];

    /// <summary>服务商标识（如 <c>deepseek</c> / <c>moonshot</c>）。</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    /// <summary>端点 URL（保留 path，如 <c>https://api.deepseek.com/v1</c>）。</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    /// <summary>模型名（如 <c>deepseek-chat</c>）；仅改模型名不重置同意（决策 (d)）。</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>该配置是否已保存（provider 与 endpoint 均非空）。</summary>
    public bool IsConfigured => Provider.Trim().Length > 0 && Endpoint.Trim().Length > 0;

    /// <summary>该配置的端点是否为 loopback（仅对「本地」服务商有意义）。</summary>
    public bool IsLoopback => IsLoopbackEndpoint(Endpoint);

    /// <summary>界面展示文案：<c>provider · endpoint</c>（不含模型与任何密钥）。</summary>
    public string DisplayName
        => string.IsNullOrEmpty(Model) ? $"{Provider} · {Endpoint}" : $"{Provider} · {Model} · {Endpoint}";

    /// <summary>
    /// 校验端点：必须为 http(s) 绝对地址、不含内嵌密钥，且非 loopback 时必须是 https
    /// （防 Bearer 密钥明文传输）。返回 null 表示通过，否则为完整提示文案。
    /// </summary>
    public static string? ValidateEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim() ?? "";
        if (value.Length == 0) return "请填写端点地址。";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "端点必须是 http(s) 绝对地址（如 https://api.example.com/v1）。";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)) return EmbeddedSecretMessage;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0].Trim();
            if (SecretQueryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return EmbeddedSecretMessage;
        }

        // 非 loopback（云端）端点必须是 https：明文 http 会把 Bearer 密钥暴露在链路上。
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackEndpoint(value)) return PlainHttpMessage;

        return null;
    }

    /// <summary>
    /// 校验一条连接配置：provider 非空 + 端点通过 <see cref="ValidateEndpoint"/>。返回 null 表示通过。
    /// 模型名允许为空（部分端点有默认模型），不阻塞保存。
    /// </summary>
    public static string? Validate(string? provider, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(provider)) return "请填写服务商标识（如 deepseek）。";
        return ValidateEndpoint(endpoint);
    }

    /// <summary>服务商标识是否表示「本地」引擎（大小写不敏感）。</summary>
    public static bool IsLocalProvider(string? provider)
        => string.Equals(provider?.Trim(), LocalProviderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 端点是否为 loopback（127.0.0.1 / ::1 / localhost）。仅接受 http(s) 绝对地址；
    /// 非绝对、非 http(s) 或非本机地址一律 false（「本地」名下的非 loopback 端点须被拒绝，防止绕过云端同意）。
    /// </summary>
    public static bool IsLoopbackEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim() ?? "";
        if (value.Length == 0) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        if (LoopbackHostNames.Contains(host, StringComparer.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>loopback 校验失败的用户可见文案（列出实际主机，引导改为本机地址）。</summary>
    public static string LoopbackViolationMessage(string? endpoint)
        => $"地址 {DescribeAuthority(endpoint)} 不是本机地址。本地引擎仅允许 127.0.0.1 / ::1 / localhost。";

    /// <summary>端点展示用的 <c>host[:port]</c>（无法解析时回退去首尾空白的原始串）。</summary>
    public static string DescribeAuthority(string? endpoint)
    {
        var value = endpoint?.Trim() ?? "";
        if (value.Length == 0) return "（空端点）";
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Authority : value;
    }
}
