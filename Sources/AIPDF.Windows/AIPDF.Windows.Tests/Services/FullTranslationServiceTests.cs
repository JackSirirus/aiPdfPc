// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 整篇翻译服务层单测：translate op 传参 / 失败透传 / 取消 / 结果解析（产物 + 降级 + 受限披露）。
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public class FullTranslationServiceTests
{
    private sealed class StubBridge(EngineResult result) : EngineBridge("python", "engine.py")
    {
        private readonly EngineResult _result = result;

        public EngineRequest? LastRequest { get; private set; }

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class CancellingBridge : EngineBridge
    {
        public CancellingBridge() : base("python", "engine.py")
        {
        }

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EngineResult { Ok = true });
        }
    }

    private static FullTranslationRequest Request(bool mono = true, bool dual = true)
        => new(
            FilePath: "/in/source.pdf",
            OutputDir: "/out",
            SourceLang: "auto",
            TargetLang: "zh",
            Template: "academic",
            Endpoint: "http://127.0.0.1:11434/v1",
            Model: "qwen",
            ApiKey: "sk-test",
            GenerateMono: mono,
            GenerateDual: dual);

    [Fact]
    public async Task TranslateAsync_SendsTranslateOp_WithFormatAndKeyViaOptions()
    {
        var bridge = new StubBridge(new EngineResult { Ok = true, Elapsed = 1.0 });
        var service = new EngineFullTranslationService(bridge);

        await service.TranslateAsync(Request(), progress: null);

        Assert.NotNull(bridge.LastRequest);
        Assert.Equal("translate", bridge.LastRequest!.Operation);
        Assert.Equal(["/in/source.pdf"], bridge.LastRequest.Files);
        Assert.Equal("/out", bridge.LastRequest.OutputDir);
        Assert.Equal("both", bridge.LastRequest.Options["format"]);
        Assert.Equal("academic", bridge.LastRequest.Options["template"]);
        // 密钥只经 options（→ stdin）传递，绝不进 argv / 环境变量。
        Assert.Equal("sk-test", bridge.LastRequest.Options["apiKey"]);
    }

    [Fact]
    public async Task TranslateAsync_MonoOnly_SendsMonoFormat()
    {
        var bridge = new StubBridge(new EngineResult { Ok = true });
        var service = new EngineFullTranslationService(bridge);

        await service.TranslateAsync(Request(mono: true, dual: false), progress: null);

        Assert.Equal("mono", bridge.LastRequest!.Options["format"]);
    }

    [Fact]
    public async Task TranslateAsync_MarkdownAndCustomPrompt_InOptions()
    {
        var bridge = new StubBridge(new EngineResult { Ok = true });
        var service = new EngineFullTranslationService(bridge);
        var request = Request(mono: true, dual: false) with
        {
            ExportMarkdown = true,
            CustomPrompt = "用极简风格翻译。",
        };

        await service.TranslateAsync(request, progress: null);

        Assert.Equal("mono+md", bridge.LastRequest!.Options["format"]);
        Assert.Equal("用极简风格翻译。", bridge.LastRequest.Options["customPrompt"]);
    }

    [Theory]
    [InlineData(true, true, false, "both")]
    [InlineData(true, false, false, "mono")]
    [InlineData(false, true, false, "dual")]
    [InlineData(false, false, true, "md")]
    [InlineData(true, false, true, "mono+md")]
    [InlineData(true, true, true, "both+md")]
    public void ToEngineFormat_MapsSelectionCombinations(
        bool mono, bool dual, bool markdown, string expected)
    {
        Assert.Equal(expected, TranslationOutputFormats.ToEngineFormat(mono, dual, markdown));
    }

    [Fact]
    public async Task TranslateAsync_EngineFailure_SurfacesError()
    {
        var bridge = new StubBridge(new EngineResult { Ok = false, Error = "请先配置翻译服务端点。" });
        var service = new EngineFullTranslationService(bridge);

        var outcome = await service.TranslateAsync(Request(), progress: null);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Cancelled);
        Assert.Equal("请先配置翻译服务端点。", outcome.Error);
    }

    [Fact]
    public async Task TranslateAsync_Cancelled_ReturnsCancelledOutcome()
    {
        var service = new EngineFullTranslationService(new CancellingBridge());

        var outcome = await service.TranslateAsync(Request(), progress: null, new CancellationToken(canceled: true));

        Assert.True(outcome.Cancelled);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public void ParseOutcome_ExtractsArtifactsDegradationsAndProtection()
    {
        const string json = """
        {
          "ok": true,
          "outputs": ["/out/a-mono.pdf", "/out/a-dual.pdf"],
          "outputDir": "/out",
          "elapsed": 3.2,
          "results": [
            {
              "file": "/in/a.pdf",
              "taskDir": "/out/task",
              "mono": "/out/a-mono.pdf",
              "dual": "/out/a-dual.pdf",
              "report": {
                "pages": [
                  { "page": 3, "degraded": true, "reasons": ["table_fail"], "searchable": true },
                  { "page": 7, "degraded": true, "reasons": ["raster_overlay"], "searchable": false },
                  { "page": 5, "degraded": false, "reasons": [], "searchable": true }
                ],
                "summary": { "degraded_pages": 2, "unsearchable_pages": 1, "total_pages": 12 },
                "source_protection": { "encrypted": true, "restricted": true, "disclosure": "已绕过源文档的抽取限制（经用户确认）" }
              }
            }
          ]
        }
        """;

        var outcome = EngineFullTranslationService.ParseOutcome(BuildResult(json));

        Assert.True(outcome.Ok);
        Assert.Equal("/out/task", outcome.TaskDir);
        Assert.Equal(2, outcome.Artifacts.Count);
        Assert.Contains(outcome.Artifacts, a => a.Kind == "mono" && a.Path == "/out/a-mono.pdf");
        Assert.Contains(outcome.Artifacts, a => a.Kind == "dual" && a.Path == "/out/a-dual.pdf");
        Assert.Equal(2, outcome.PageIssues.Count);
        Assert.Equal(3, outcome.PageIssues[0].Page);
        Assert.True(outcome.PageIssues[0].Searchable);
        Assert.False(outcome.PageIssues[1].Searchable);
        Assert.NotNull(outcome.SearchSummary);
        Assert.Equal(12, outcome.SearchSummary!.TotalPages);
        Assert.Equal(2, outcome.SearchSummary.DegradedPages);
        Assert.Equal(1, outcome.SearchSummary.UnsearchablePages);
        Assert.Equal(11, outcome.SearchSummary.SearchablePages);
        Assert.True(outcome.SourceProtection.Restricted);
        Assert.Equal("已绕过源文档的抽取限制（经用户确认）", outcome.SourceProtection.Disclosure);
    }

    [Fact]
    public void ParseOutcome_FallsBackToFlatOutputs_WhenResultsMissing()
    {
        const string json = """
        {
          "ok": true,
          "outputs": ["/out/a-mono.pdf", "/out/a-dual.pdf"],
          "outputDir": "/out",
          "elapsed": 1.0
        }
        """;

        var outcome = EngineFullTranslationService.ParseOutcome(BuildResult(json));

        Assert.Equal(2, outcome.Artifacts.Count);
        Assert.False(outcome.SourceProtection.Restricted);
    }

    private static EngineResult BuildResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new EngineResult
        {
            Ok = root.GetProperty("ok").GetBoolean(),
            OutputDir = root.GetProperty("outputDir").GetString(),
            Outputs = [.. root.GetProperty("outputs").EnumerateArray().Select(e => e.GetString() ?? "")],
            Elapsed = root.GetProperty("elapsed").GetDouble(),
            Extra = root.TryGetProperty("results", out var results)
                ? new Dictionary<string, JsonElement> { ["results"] = results.Clone() }
                : new Dictionary<string, JsonElement>(),
        };
    }
}
