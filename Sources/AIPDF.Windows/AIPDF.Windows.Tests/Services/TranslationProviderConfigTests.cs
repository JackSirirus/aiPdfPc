// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端连接配置模型测试（决策 (a)(d)(j)；T23 设置→翻译→云端服务商）：
//   - 端点必须为 http(s) 绝对地址；拒绝 userinfo / key·token 查询参数（防设置漫游泄漏密钥）；
//   - 连接配置只含 provider + endpoint + model，模型名可为空；结构层不得出现密钥属性。
using System.Net;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationProviderConfigTests
{
    [Theory]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("http://127.0.0.1:8080/v1/chat")]
    [InlineData("https://api.example.com/v1?model=deepseek-chat")] // 非密钥查询参数允许
    public void ValidateEndpoint_AcceptsHttpAbsoluteUrls(string endpoint)
        => Assert.Null(TranslationProviderConfig.ValidateEndpoint(endpoint));

    // 明文 http 仅限 loopback；云端端点用 http 会让 Bearer 密钥在链路上明文传输。
    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://[::1]:8080/v1")]
    [InlineData("http://localhost:11434/v1")]
    public void ValidateEndpoint_AllowsPlainHttpForLoopback(string endpoint)
        => Assert.Null(TranslationProviderConfig.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("http://api.deepseek.com/v1")]
    [InlineData("http://192.168.1.10:11434/v1")]
    [InlineData("http://remote.example.com:8443/v1")]
    public void ValidateEndpoint_RejectsPlainHttpForNonLoopback(string endpoint)
        => Assert.Equal(TranslationProviderConfig.PlainHttpMessage,
                        TranslationProviderConfig.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("", "请填写端点地址。")]
    [InlineData("   ", "请填写端点地址。")]
    [InlineData("api.deepseek.com/v1", "端点必须是 http(s) 绝对地址（如 https://api.example.com/v1）。")]
    [InlineData("ftp://api.deepseek.com/v1", "端点必须是 http(s) 绝对地址（如 https://api.example.com/v1）。")]
    public void ValidateEndpoint_RejectsMissingOrNonHttp(string endpoint, string expected)
        => Assert.Equal(expected, TranslationProviderConfig.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("https://key:secret@api.deepseek.com/v1")]        // userinfo 内嵌密钥
    [InlineData("https://api.deepseek.com/v1?api_key=sk-abc")]    // 查询参数内嵌密钥
    [InlineData("https://api.deepseek.com/v1?APIKEY=sk-abc")]     // 大小写不敏感
    [InlineData("https://api.deepseek.com/v1?token=sk-abc")]
    [InlineData("https://api.deepseek.com/v1?access_token=sk-abc")]
    [InlineData("https://api.deepseek.com/v1?secret=sk-abc")]
    public void ValidateEndpoint_RejectsEmbeddedSecret(string endpoint)
        => Assert.Equal(TranslationProviderConfig.EmbeddedSecretMessage, TranslationProviderConfig.ValidateEndpoint(endpoint));

    [Fact]
    public void Validate_RequiresProvider()
    {
        Assert.NotNull(TranslationProviderConfig.Validate("", "https://api.deepseek.com/v1"));
        Assert.NotNull(TranslationProviderConfig.Validate("   ", "https://api.deepseek.com/v1"));
        Assert.Null(TranslationProviderConfig.Validate("deepseek", "https://api.deepseek.com/v1"));
    }

    [Fact]
    public void IsConfigured_RequiresBothProviderAndEndpoint()
    {
        Assert.False(new TranslationProviderConfig().IsConfigured);
        Assert.False(new TranslationProviderConfig { Provider = "deepseek" }.IsConfigured);
        Assert.False(new TranslationProviderConfig { Endpoint = "https://api.deepseek.com/v1" }.IsConfigured);
        Assert.True(new TranslationProviderConfig { Provider = "deepseek", Endpoint = "https://api.deepseek.com/v1" }.IsConfigured);
    }

    [Fact]
    public void Model_HasNoSecretBearingMember()
    {
        var properties = typeof(TranslationProviderConfig).GetProperties();

        Assert.DoesNotContain(properties, p =>
            p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name, "key", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name, "apiKey", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- loopback（防同意绕过）

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("https://localhost/v1", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("https://api.deepseek.com/v1", false)]
    [InlineData("http://192.168.1.10:11434/v1", false)] // 局域网地址≠loopback
    [InlineData("api.deepseek.com/v1", false)]          // 非绝对 URL
    [InlineData("", false)]
    public void IsLoopbackEndpoint_OnlyAcceptsLoopback(string endpoint, bool expected)
        => Assert.Equal(expected, TranslationProviderConfig.IsLoopbackEndpoint(endpoint));

    [Theory]
    [InlineData("local", true)]
    [InlineData("LOCAL", true)]
    [InlineData(" local ", true)]
    [InlineData("deepseek", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalProvider_MatchesLocalCaseInsensitively(string? provider, bool expected)
        => Assert.Equal(expected, TranslationProviderConfig.IsLocalProvider(provider));

    [Fact]
    public void LoopbackViolationMessage_ListsHostAndAllowedAddresses()
    {
        // 用非默认端口，确保 Authority 带上端口（默认端口 Uri.Authority 会省略）。
        var message = TranslationProviderConfig.LoopbackViolationMessage("https://remote.example.com:8443/v1");

        Assert.Contains("remote.example.com:8443", message);
        Assert.Contains("127.0.0.1 / ::1 / localhost", message);
    }

    // ---------------------------------------------------------------- F30：主机名必须解析为 loopback

    [Theory]
    [InlineData("http://localhost:11434/v1", new[] { "10.0.0.5" }, false)]              // localhost 被解析到非本机
    [InlineData("http://localhost:11434/v1", new[] { "127.0.0.1" }, true)]
    [InlineData("http://localhost:11434/v1", new[] { "127.0.0.1", "::1" }, true)]
    [InlineData("http://localhost:11434/v1", new[] { "127.0.0.1", "10.0.0.5" }, false)] // 混入非本机 → false
    [InlineData("http://localma.ch:8080/v1", new[] { "127.0.0.1" }, true)]              // 任意本机名
    public void IsLoopbackEndpoint_ResolvesHostName_AllAddressesMustBeLoopback(
        string endpoint, string[] resolved, bool expected)
    {
        var resolvedIps = resolved.Select(IPAddress.Parse).ToArray();

        var actual = TranslationProviderConfig.IsLoopbackEndpoint(endpoint, _ => resolvedIps);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsLoopbackEndpoint_ResolverThrows_ReturnsFalse()
        => Assert.False(TranslationProviderConfig.IsLoopbackEndpoint(
            "http://localhost:11434/v1", _ => throw new InvalidOperationException("dns down"))); // fail-closed

    [Fact]
    public void IsLoopbackEndpoint_LiteralIp_DoesNotCallResolver()
    {
        var called = false;

        var actual = TranslationProviderConfig.IsLoopbackEndpoint("http://127.0.0.1:11434/v1", _ =>
        {
            called = true;
            return [IPAddress.Loopback];
        });

        Assert.True(actual);
        Assert.False(called); // 字面量 IP 直接判定，不解析
    }

    // R5-6：生产解析器带短 TTL 缓存，但【注入解析器】不经过缓存，
    // 同一主机两次不同解析结果必须各自生效（防测试 / 行为互相污染）。
    [Fact]
    public void IsLoopbackEndpoint_InjectedResolver_NotCachedAcrossCalls()
    {
        const string endpoint = "http://cached-host.test/v1";

        var nonLoopback = TranslationProviderConfig.IsLoopbackEndpoint(endpoint, _ => [IPAddress.Parse("10.0.0.5")]);
        var loopback = TranslationProviderConfig.IsLoopbackEndpoint(endpoint, _ => [IPAddress.Loopback]);

        Assert.False(nonLoopback);
        Assert.True(loopback);
    }

    // ---------------------------------------------------------------- F31：URL 片段内嵌密钥

    [Theory]
    [InlineData("https://api.deepseek.com/v1#key=sk-abc")]
    [InlineData("https://api.deepseek.com/v1#access_token=sk-abc")]
    [InlineData("https://api.deepseek.com/v1#api_key=sk-abc")]
    public void ValidateEndpoint_RejectsSecretInFragment(string endpoint)
        => Assert.Equal(TranslationProviderConfig.EmbeddedSecretMessage, TranslationProviderConfig.ValidateEndpoint(endpoint));

    // R5-4：片段里的裸密钥（无 key= 名称）此前漏检，必须与 key=value 同口径拒绝。
    [Theory]
    [InlineData("https://api.deepseek.com/v1#sk-abcdefgh12345678")]
    [InlineData("https://api.deepseek.com/v1#token=ABCDEFGH1234")]
    public void ValidateEndpoint_RejectsBareSecretInFragment(string endpoint)
        => Assert.Equal(TranslationProviderConfig.EmbeddedSecretMessage, TranslationProviderConfig.ValidateEndpoint(endpoint));

    // ---------------------------------------------------------------- F32：路径段疑似内嵌密钥

    [Theory]
    [InlineData("https://api.deepseek.com/v1/sk-abcdefgh12345678")]
    [InlineData("https://api.deepseek.com/v1/key=sk-abcdefgh")]
    [InlineData("https://api.deepseek.com/v1/token:abcdefgh1234")]
    [InlineData("https://api.deepseek.com/v1/api_key-abcdefgh1234")]
    [InlineData("https://api.deepseek.com/v1/token=ABCDEFGH1234")]
    public void ValidateEndpoint_RejectsSecretLookingPath(string endpoint)
        => Assert.Equal(TranslationProviderConfig.EmbeddedSecretInPathMessage, TranslationProviderConfig.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("https://api.deepseek.com/v1/chat")]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    public void ValidateEndpoint_AcceptsNormalPath(string endpoint)
        => Assert.Null(TranslationProviderConfig.ValidateEndpoint(endpoint));

    // R5-3：正常路径词含 sk- / key- 子串时不得被误判（此前 /v1/task-management 等会被拒）。
    [Theory]
    [InlineData("https://api.deepseek.com/v1/task-management")]
    [InlineData("https://api.deepseek.com/v1/risk-assessment")]
    [InlineData("https://api.deepseek.com/v1/disk-encryption")]
    [InlineData("https://api.deepseek.com/v1/monkey-12345678")]
    [InlineData("https://api.deepseek.com/v1/token-management")]
    [InlineData("https://api.deepseek.com/v1/key-value-store")]
    public void ValidateEndpoint_AcceptsLegitimatePathWords(string endpoint)
        => Assert.Null(TranslationProviderConfig.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("sk-abcdefgh12345678", true)]
    [InlineData("/v1/sk-abcdefgh12345678", true)]
    [InlineData("v1/token=abcdefgh1234", true)]
    [InlineData("/v1/chat", false)]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("", false)]
    [InlineData("/v1/task-management", false)]     // sk- 非段首 / 非分隔符前缀
    [InlineData("/v1/risk-assessment", false)]
    [InlineData("/v1/disk-encryption", false)]
    [InlineData("/v1/monkey-12345678", false)]     // key- 非段首 / 非分隔符前缀
    [InlineData("/v1/token-management", false)]    // 命名值无高熵特征（纯小写短词）
    public void LooksLikeSecretInPath_DetectsKeyLookingSegments(string path, bool expected)
        => Assert.Equal(expected, TranslationProviderConfig.LooksLikeSecretInPath(path));
}
