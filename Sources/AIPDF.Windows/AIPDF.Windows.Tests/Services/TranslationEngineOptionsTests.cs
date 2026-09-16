// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T6 引擎 options 装配点（TranslationEngineOptions.Build）直接单测：
//   - apiProtocol（openai | anthropic）进入 options（T4 引擎契约）；
//   - 既有键与值语义保持不变（endpoint/model/apiKey/sourceLang/targetLang/template/format/marker）；
//   - provider 名 / 显示名绝不进入 options（仅协议值跨语言）；
//   - password 仅在非空时出现；glossary / customPrompt / maxTokens 同理（可选键）。
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public class TranslationEngineOptionsTests
{
    /// <summary>仅存在于运行时配置的显示名：断言它绝不泄漏进 options。</summary>
    private const string ProviderDisplayName = "显示名供应商";

    private static TranslationRuntimeConfig Runtime(
        string apiProtocol = TranslationProviderConfig.ProtocolOpenAi)
        => new(
            Provider: ProviderDisplayName,
            Endpoint: "https://api.example.com/v1",
            Model: "model-1",
            ApiKey: "sk-test",
            SourceLang: "en",
            TargetLang: "zh",
            Template: "academic",
            ApiProtocol: apiProtocol);

    private static Dictionary<string, object?> Build(
        TranslationRuntimeConfig runtime,
        bool marker = true,
        string? glossaryPath = null,
        string? customPrompt = null,
        int? maxTokens = null,
        string? password = null)
        => TranslationEngineOptions.Build(
            runtime.Endpoint,
            runtime.Model,
            runtime.ApiKey,
            runtime.ApiProtocol,
            runtime.SourceLang,
            runtime.TargetLang,
            runtime.Template,
            generateMono: true,
            generateDual: true,
            exportMarkdown: false,
            marker: marker,
            glossaryPath: glossaryPath,
            customPrompt: customPrompt,
            maxTokens: maxTokens,
            password: password);

    [Theory]
    [InlineData(TranslationProviderConfig.ProtocolOpenAi)]
    [InlineData(TranslationProviderConfig.ProtocolAnthropic)]
    public void Build_WritesApiProtocolAndCoreValues(string apiProtocol)
    {
        var runtime = Runtime(apiProtocol);

        var options = Build(runtime);

        Assert.Equal(apiProtocol, options["apiProtocol"]);
        Assert.Equal(runtime.Endpoint, options["endpoint"]);
        Assert.Equal(runtime.Model, options["model"]);
        Assert.Equal(runtime.ApiKey, options["apiKey"]);
        Assert.Equal(runtime.SourceLang, options["sourceLang"]);
        Assert.Equal(runtime.TargetLang, options["targetLang"]);
        Assert.Equal(runtime.Template, options["template"]);
        Assert.Equal("both", options["format"]);
        Assert.Equal("true", options["marker"]);
    }

    [Fact]
    public void Build_KeySet_MatchesContract_WithoutProviderIdentity()
    {
        var options = Build(Runtime(TranslationProviderConfig.ProtocolAnthropic));

        Assert.Equal(
            new[]
            {
                "endpoint", "model", "apiKey", "apiProtocol",
                "sourceLang", "targetLang", "template", "format", "marker",
            },
            options.Keys);

        // provider 名 / 显示名绝不进入 options（协议值除外）。
        Assert.DoesNotContain("provider", options.Keys);
        Assert.DoesNotContain("presetId", options.Keys);
        Assert.DoesNotContain(
            options.Values,
            value => value is string text && text.Contains(ProviderDisplayName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Build_Marker_AlwaysPresent(bool marker, string expected)
    {
        var options = Build(Runtime(), marker: marker);

        Assert.Equal(expected, options["marker"]);
    }

    [Fact]
    public void Build_Password_OnlyWhenProvided()
    {
        Assert.False(Build(Runtime()).ContainsKey("password"));
        Assert.False(Build(Runtime(), password: "").ContainsKey("password"));

        var withPassword = Build(Runtime(), password: "pdf-open-secret");

        Assert.Equal("pdf-open-secret", withPassword["password"]);
    }

    [Fact]
    public void Build_OptionalKeys_OnlyWhenProvided()
    {
        var plain = Build(Runtime());
        Assert.False(plain.ContainsKey("glossary"));
        Assert.False(plain.ContainsKey("customPrompt"));
        Assert.False(plain.ContainsKey("maxTokens"));

        var rich = Build(
            Runtime(),
            glossaryPath: @"C:\glossary\terms.csv",
            customPrompt: "用极简风格翻译。",
            maxTokens: 4096);

        Assert.Equal(@"C:\glossary\terms.csv", rich["glossary"]);
        Assert.Equal("用极简风格翻译。", rich["customPrompt"]);
        // 值一律字符串：maxTokens 也不例外（引擎按字符串解析）。
        Assert.Equal("4096", rich["maxTokens"]);
    }

    [Fact]
    public void Build_BlankGlossaryAndCustomPrompt_AreOmitted()
    {
        var options = Build(Runtime(), glossaryPath: "   ", customPrompt: "");

        Assert.False(options.ContainsKey("glossary"));
        Assert.False(options.ContainsKey("customPrompt"));
    }
}
