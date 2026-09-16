// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · URL 双归一化测试（T3）：
//   - NormalizeRequestBase：保留自定义路径 / 版本段，剥完整端点后缀，尾斜杠剔除，query 丢弃；
//     OpenAI 兼容 base 恰好一个版本段（缺 /v1 补、重复收敛，v4 / v1beta 原样保留）；
//     Anthropic base 不以 /v1 结尾（引擎层另拼 /v1/messages）；非 http(s) 返回空串；
//   - SecurityOrigin：scheme://host[:port] 小写，与 TranslationConsent.NormalizeEndpoint 逐字一致
//     （凭据 / 同意哈希稳定，既有条目不得被孤立）。
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class ProviderUrlTests
{
    // ---------------------------------------------------------------- NormalizeRequestBase · OpenAI 基础形态

    [Theory]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1")]              // 尾部斜杠
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1")]               // 已含 /v1
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1")]  // 完整端点粘贴
    [InlineData("https://api.openai.com/v1/chat/completions/", "https://api.openai.com/v1")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1")]                  // 缺 /v1 → 补
    [InlineData("https://api.deepseek.com/v1/v1", "https://api.deepseek.com/v1")]            // /v1 重复收敛
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434/v1")]                      // 本地补 /v1
    [InlineData("http://127.0.0.1:11434/v1/", "http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    [InlineData("  https://api.deepseek.com/v1  ", "https://api.deepseek.com/v1")]           // 去空白
    [InlineData("https://api.example.com/v1?model=deepseek-chat", "https://api.example.com/v1")] // query 丢弃
    [InlineData("HTTPS://API.DeepSeek.COM/v1", "https://api.deepseek.com/v1")]               // scheme/host 小写
    [InlineData("https://api.deepseek.com:8443/v1", "https://api.deepseek.com:8443/v1")]     // 端口保留
    public void NormalizeRequestBase_OpenAi_NormalizesShape(string input, string expected)
        => Assert.Equal(expected, ProviderUrl.NormalizeRequestBase(input, "openai"));

    [Theory]
    [InlineData("https://gw.corp/llm/v1/", "https://gw.corp/llm/v1")]  // 自定义路径绝不塌陷
    [InlineData("https://gw.corp/llm", "https://gw.corp/llm/v1")]      // 自定义路径缺版本段 → 补
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4")]        // v4 保留（GLM）
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "https://generativelanguage.googleapis.com/v1beta")] // v1beta 保留（Gemini）
    [InlineData("https://api.groq.com/openai/v1", "https://api.groq.com/openai/v1")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1/", "https://dashscope.aliyuncs.com/compatible-mode/v1")]
    [InlineData("https://api.anthropic.com/v1/chat/completions", "https://api.anthropic.com/v1")]       // 后缀剥离后保留 /v1
    [InlineData("api.deepseek.com/v1", "https://api.deepseek.com/v1")] // 缺协议补 https
    public void NormalizeRequestBase_OpenAi_PreservesCustomPathAndExistingVersion(string input, string expected)
        => Assert.Equal(expected, ProviderUrl.NormalizeRequestBase(input, "openai"));

    // ---------------------------------------------------------------- NormalizeRequestBase · 非法输入

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://api.deepseek.com/v1")]
    [InlineData("file:///c:/endpoint")]
    [InlineData("/v1/chat/completions")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("not a url")]
    public void NormalizeRequestBase_InvalidInput_ReturnsEmpty(string input)
        => Assert.Equal("", ProviderUrl.NormalizeRequestBase(input, "openai"));

    [Fact]
    public void NormalizeRequestBase_NullInput_ReturnsEmpty()
        => Assert.Equal("", ProviderUrl.NormalizeRequestBase(null, "openai"));

    [Fact]
    public void NormalizeRequestBase_RejectsEmbeddedUserInfo()
        => Assert.Equal("", ProviderUrl.NormalizeRequestBase("https://key:secret@api.example.com/v1", "openai"));

    // ---------------------------------------------------------------- NormalizeRequestBase · Anthropic

    [Theory]
    [InlineData("https://open.bigmodel.cn/api/anthropic", "https://open.bigmodel.cn/api/anthropic")]      // 预设无 /v1 → 不加
    [InlineData("https://open.bigmodel.cn/api/anthropic/", "https://open.bigmodel.cn/api/anthropic")]
    [InlineData("https://open.bigmodel.cn/api/anthropic/v1/messages", "https://open.bigmodel.cn/api/anthropic")] // 完整端点粘贴
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com")]                            // 尾部 /v1 剥离
    [InlineData("https://api.kimi.com/coding/", "https://api.kimi.com/coding")]
    [InlineData("https://gw.corp/llm/v1/", "https://gw.corp/llm")]                                       // 无 /v1 结尾、路径保留
    public void NormalizeRequestBase_Anthropic_BaseNeverEndsWithV1(string input, string expected)
        => Assert.Equal(expected, ProviderUrl.NormalizeRequestBase(input, "anthropic"));

    [Fact]
    public void NormalizeRequestBase_ProtocolCaseInsensitive_UnknownDefaultsToOpenAi()
    {
        Assert.Equal("https://api.anthropic.com", ProviderUrl.NormalizeRequestBase("https://api.anthropic.com/v1", "ANTHROPIC"));
        Assert.Equal("https://api.deepseek.com/v1", ProviderUrl.NormalizeRequestBase("https://api.deepseek.com/v1"));
        Assert.Equal("https://api.deepseek.com/v1", ProviderUrl.NormalizeRequestBase("https://api.deepseek.com/v1", "responses"));
    }

    // ---------------------------------------------------------------- T5 契约：{requestBase}/models 绝不出现 /v1/v1

    [Theory]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/models")]
    [InlineData("https://api.deepseek.com/v1/chat/completions", "https://api.deepseek.com/v1/models")]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434/v1/models")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/models")]
    public void NormalizeRequestBase_ModelListAppendsOnlyModels(string input, string expectedListUri)
    {
        var requestBase = ProviderUrl.NormalizeRequestBase(input, "openai");

        Assert.Equal(expectedListUri, requestBase + "/models"); // T5 只拼 /models，不追加 /v1
        Assert.DoesNotContain("/v1/v1", requestBase, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 双归一化不得混用

    [Fact]
    public void RequestBaseAndSecurityOrigin_ServeDifferentPurposes()
    {
        const string input = "https://gw.corp/llm/v1";

        Assert.Equal("https://gw.corp/llm/v1", ProviderUrl.NormalizeRequestBase(input, "openai")); // 请求基址保路径
        Assert.Equal("https://gw.corp", ProviderUrl.SecurityOrigin(input));                        // 安全 origin 丢路径
    }

    // ---------------------------------------------------------------- SecurityOrigin

    [Theory]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1/chat/completions", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v2/chat", "https://api.deepseek.com")] // 仅改 path 不换目的地（v1.1 同意语义）
    [InlineData("https://api.deepseek.com:8443/v1", "https://api.deepseek.com:8443")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com")]
    [InlineData("http://api.deepseek.com/v1", "http://api.deepseek.com")]
    [InlineData("HTTPS://API.DeepSeek.COM/V1", "https://api.deepseek.com")]
    [InlineData("api.deepseek.com/v1", "api.deepseek.com/v1")] // 非 http(s)：回退为小写原串（与 v1.1 一致）
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SecurityOrigin_ReturnsLowercaseSchemeHostPortWithoutPath(string input, string expected)
        => Assert.Equal(expected, ProviderUrl.SecurityOrigin(input));

    [Fact]
    public void SecurityOrigin_Null_ReturnsEmpty()
        => Assert.Equal("", ProviderUrl.SecurityOrigin(null));

    // 稳定性：与既有 NormalizeEndpoint 逐字一致 —— 改了这里等于换哈希输入，既有凭据 / 同意会被孤立。
    [Theory]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com:8443/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("https://localhost:11434/v1/v1")]
    [InlineData("HTTPS://API.DeepSeek.COM:443/V1")]
    [InlineData("ftp://api.deepseek.com/v1")]
    [InlineData("api.deepseek.com/v1")]
    [InlineData("")]
    public void SecurityOrigin_MatchesTranslationConsentNormalizeEndpoint(string input)
        => Assert.Equal(TranslationConsent.NormalizeEndpoint(input), ProviderUrl.SecurityOrigin(input));
}
