// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端连接配置模型（决策 #9 / (a)(d)(j)；T23 设置→翻译→云端服务商）：
//   - 只存 provider + endpoint + model（连接参数），绝不存密钥 / 正文；
//   - endpoint 保留完整 URL（path 决定 API 版本，不归一为 origin），但拒绝内嵌密钥：
//     userinfo（https://key:secret@host）、key/token 查询参数、URL 片段（#key=...）或疑似含密钥的路径段
//     （F31/F32；都会随设置持久化），防止设置漫游泄漏；
//   - 同一 provider + endpoint origin 视为同一目的地（与同意 / 凭据寻址一致），设置层去重。
//
// 红线（决策 #21）：本类型及其序列化结果不得出现密钥；密钥交 TranslationCredentialStore。
using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIPDF.Windows.Services;

/// <summary>
/// 云端翻译连接配置：服务商标识 + 端点 + 模型名。存 <see cref="AppSettings.TranslationProviders"/>，
/// 与 <see cref="TranslationConsent"/>（同意）和 <see cref="TranslationCredentialStore"/>（密钥）分离。
/// </summary>
public sealed class TranslationProviderConfig
{
    /// <summary>端点内嵌密钥时的统一提示（决策 (j)）。</summary>
    public const string EmbeddedSecretMessage = "密钥请保存在系统凭据管理器中，不要写在端点地址里。";

    /// <summary>端点【路径】疑似内嵌密钥时的提示（F32：path 同样会被持久化到设置）。</summary>
    public const string EmbeddedSecretInPathMessage =
        "端点路径疑似包含密钥，请只填写 API 基础地址（如 https://api.example.com/v1），密钥保存在系统凭据管理器中。";

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

    /// <summary>路径段中形如 <c>sk-...</c> 的内嵌密钥（路径会被持久化，须与 query 同等拦截）。</summary>
    private static readonly Regex SkSecretPattern =
        new(@"sk-[A-Za-z0-9]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>路径段中「名称 + 分隔符 + 长值」型密钥（如 <c>token=...</c> / <c>api-key_...</c>）。</summary>
    private static readonly Regex NamedSecretPattern =
        new(@"(key|api_?key|token|access_token|secret|authorization)[=:_-][A-Za-z0-9_-]{8,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        // 查询串与 URL 片段（#key=...）都可能是内嵌密钥，且都会被持久化，须同等拦截（F31）。
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0].Trim();
            if (SecretQueryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return EmbeddedSecretMessage;
        }

        foreach (var pair in uri.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0].Trim();
            if (SecretQueryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return EmbeddedSecretMessage;
        }

        // F32：路径段同样可能被用户拼进密钥（如 /v1/sk-...），path 也会写入设置，须拒绝并给出独立文案。
        if (LooksLikeSecretInPath(uri.AbsolutePath)) return EmbeddedSecretInPathMessage;

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
    /// 主机名经系统解析后要求【全部】地址为 loopback（名字可被 hosts / DNS 指向非本机，不能仅凭名字白名单信任）。
    /// </summary>
    public static bool IsLoopbackEndpoint(string? endpoint)
        => IsLoopbackEndpoint(endpoint, SystemAddressResolver);

    /// <summary>
    /// 端点是否为 loopback，主机名解析器可注入（测试）。
    /// 字面量 IP 直接经 <see cref="IPAddress.IsLoopback"/> 判定（不调用解析器）；
    /// 主机【名】须解析出至少一个地址且【全部】为 loopback；解析失败（抛异常）或结果为空 → false（fail-closed）。
    /// </summary>
    internal static bool IsLoopbackEndpoint(string? endpoint, Func<string, IPAddress[]> resolveHost)
    {
        ArgumentNullException.ThrowIfNull(resolveHost);

        var value = endpoint?.Trim() ?? "";
        if (value.Length == 0) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;

        // 字面量 IP：无歧义，直接判定，不解析。
        if (IPAddress.TryParse(host, out var literal))
        {
            return IPAddress.IsLoopback(literal);
        }

        // 主机名（含 localhost）：解析后要求全部地址为 loopback；名字可能被 hosts / DNS 劫持到非本机。
        try
        {
            var addresses = resolveHost(host);
            return addresses is { Length: > 0 } && addresses.All(IPAddress.IsLoopback);
        }
        catch (Exception)
        {
            // 解析失败（DNS 不可用 / 无效主机名 / 注入解析器抛错）→ fail-closed，绝不误判为本地。
            return false;
        }
    }

    /// <summary>默认主机名解析器：调用系统 DNS（含 hosts 文件解析）。</summary>
    private static IPAddress[] SystemAddressResolver(string host) => Dns.GetHostAddresses(host);

    /// <summary>
    /// 端点【路径】段是否疑似内嵌密钥（F32）。path 与 query 一样会被持久化到 settings，
    /// 用户可能把 key 拼进路径（<c>/v1/sk-...</c> 或 <c>/v1/token=...</c>），故与 query / userinfo / fragment 同等拦截。
    /// 规则（大小写不敏感）：任一段匹配 <c>sk-[A-Za-z0-9]{8,}</c> 或
    /// <c>(key|api_?key|token|access_token|secret|authorization)[=:_-][A-Za-z0-9_-]{8,}</c>。
    /// 只做启发式拦截，不追求穷尽；正常基础地址（如 <c>/v1</c> / <c>/v1/chat</c>）不受影响。
    /// </summary>
    internal static bool LooksLikeSecretInPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SkSecretPattern.IsMatch(segment) || NamedSecretPattern.IsMatch(segment))
            {
                return true;
            }
        }

        return false;
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
