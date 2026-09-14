// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端连接配置模型测试（决策 (a)(d)(j)；T23 设置→翻译→云端服务商）：
//   - 端点必须为 http(s) 绝对地址；拒绝 userinfo / key·token 查询参数（防设置漫游泄漏密钥）；
//   - 连接配置只含 provider + endpoint + model，模型名可为空；结构层不得出现密钥属性。
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
}
