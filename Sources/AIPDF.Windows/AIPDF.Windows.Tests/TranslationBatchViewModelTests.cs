// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 批量翻译视图模型单测（纯逻辑，不依赖桌面 / 真实引擎）：
// 串行逐文件执行（顺序即列表顺序）/ 单份失败不中断整体 / 共享配置映射 / 失败项重试 /
// 结果【按需打开】（绝不自动触发 OpenDocumentRequested）。
using System.IO;
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tools;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

public sealed class TranslationBatchViewModelTests
{
    private sealed class FixedConfigProvider(TranslationRuntimeConfig? config) : ITranslationRuntimeConfigProvider
    {
        public TranslationRuntimeConfig? Get() => config;
    }

    /// <summary>可变更同意代次的配置提供者（模拟运行中撤销同意，F21）。</summary>
    private sealed class RevocableConfigProvider(TranslationRuntimeConfig? config) : ITranslationRuntimeConfigProvider
    {
        public int Generation { get; set; }

        public TranslationRuntimeConfig? Get() => config;

        public int ConsentGeneration => Generation;
    }

    /// <summary>
    /// 可变更配置 / 解析结果的提供者：模拟批量运行中途改端点（F22）或变为未就绪（F22/F23）。
    /// </summary>
    private sealed class MutableConfigProvider : ITranslationRuntimeConfigProvider
    {
        private Func<TranslationRuntimeConfigResolution> _resolution;

        public MutableConfigProvider(TranslationRuntimeConfig? config)
            => _resolution = () => ResolutionOf(config);

        public void SetConfig(TranslationRuntimeConfig? config)
            => _resolution = () => ResolutionOf(config);

        public void SetResolution(Func<TranslationRuntimeConfigResolution> resolution)
            => _resolution = resolution;

        public TranslationRuntimeConfig? Get() => Resolve().Config;

        public TranslationRuntimeConfigResolution Resolve(string? selectedProviderId = null) => _resolution();

        private static TranslationRuntimeConfigResolution ResolutionOf(TranslationRuntimeConfig? config)
            => config is null
                ? new TranslationRuntimeConfigResolution(
                    TranslationRuntimeConfigStatus.NoProvider, null, "", "", false,
                    TranslationRuntimeConfigResolution.NotConfiguredMessage)
                : new TranslationRuntimeConfigResolution(
                    TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint, false, "");
    }

    /// <summary>带具体原因的「云端未同意」解析结果（F23 断言其 Message 被原样展示）。</summary>
    private static TranslationRuntimeConfigResolution NoConsentResolution()
        => new(
            TranslationRuntimeConfigStatus.NoConsent, null, "deepseek", "https://api.deepseek.com/v1", IsCloud: true,
            "云端翻译需要你的明确同意（服务商：deepseek）。");

    private static TranslationRuntimeConfig RuntimeConfig()
        => new("local", "http://127.0.0.1:11434/v1", "qwen", "sk-test", "auto", "zh", "academic");

    private static Tool Tool() => ToolCatalog.Load().First(t => t.Id == "translate_batch");

    /// <summary>记录请求、可注入成功/失败行为的假桥（不启动真实进程）。</summary>
    private sealed class RecordingBridge : EngineBridge
    {
        public RecordingBridge() : base("nonexistent-python", "nonexistent-engine")
        {
        }

        public List<EngineRequest> Requests { get; } = [];

        public Func<EngineRequest, EngineResult> Handler { get; set; } = _ => Success();

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }

        public static EngineResult Success()
            => new()
            {
                Ok = true,
                Outputs = [@"D:\out\task\doc-mono.pdf", @"D:\out\task\doc-dual.pdf"],
                OutputDir = @"D:\out\task",
                ExitCode = 0,
            };
    }

    /// <summary>会阻塞到取消的假桥（验证运行中撤销同意会停止在途批量）。</summary>
    private sealed class BlockingBridge : EngineBridge
    {
        public BlockingBridge() : base("nonexistent-python", "nonexistent-engine")
        {
        }

        public List<EngineRequest> Requests { get; } = [];

        public override async Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return RecordingBridge.Success();
        }
    }

    private static TranslationBatchViewModel NewViewModel(RecordingBridge bridge, params string[] files)
    {
        var vm = new TranslationBatchViewModel(bridge, Tool(), new FixedConfigProvider(RuntimeConfig()));
        vm.AddFiles(files);
        return vm;
    }

    [Fact]
    public async Task Start_ProcessesFilesSeriallyInListOrder_WithTranslateOperation()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var vm = NewViewModel(bridge, temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf"), temp.CreatePdf("c.pdf"));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(3, bridge.Requests.Count);
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf"], bridge.Requests.Select(r => Path.GetFileName(r.Files[0])));
        Assert.All(bridge.Requests, r =>
        {
            Assert.Equal(TranslationBatchViewModel.TranslateOperation, r.Operation);
            Assert.Equal("both", (string)r.Options["format"]!);
            Assert.Equal("zh", (string)r.Options["targetLang"]!);
            Assert.Equal("sk-test", (string)r.Options["apiKey"]!);
            // v1.2 T6：协议随运行时配置贯通（默认 openai，T4 契约）。
            Assert.Equal("openai", (string)r.Options["apiProtocol"]!);
        });
        Assert.All(vm.Files, f => Assert.True(f.IsDone));
        Assert.Equal(3, vm.SuccessCount);
        Assert.False(vm.HasFailedItems);
    }

    // R5-7：逐项结果不只显示「N 页降级」计数，还展示原因文案与溢出/裁切聚合。
    [Fact]
    public async Task Start_DegradedFile_SurfacesReasonLabelsAndOverflowCounts()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge { Handler = _ => ResultWithReport() };
        var vm = NewViewModel(bridge, temp.CreatePdf("doc.pdf"));

        await vm.StartCommand.ExecuteAsync(null);

        var text = vm.Files[0].ResultText;
        Assert.Contains("已生成 mono + dual", text);
        Assert.Contains("2 页降级", text);
        Assert.Contains("该页表格结构可能丢失", text);     // table_fail → ReasonLabels 文案
        Assert.Contains("该页译文超出原文块范围", text);   // overflow_margin → ReasonLabels 文案
        Assert.Contains("2 页译文超出范围", text);
        Assert.Contains("1 页被裁切", text);
    }

    /// <summary>带逐页降级与 results[].overflow 聚合的引擎结果（经真实 ParseOutcome 解析）。</summary>
    private static EngineResult ResultWithReport()
    {
        const string json = """
        {
          "ok": true,
          "outputs": ["/out/task/doc-mono.pdf", "/out/task/doc-dual.pdf"],
          "outputDir": "/out/task",
          "results": [
            {
              "file": "doc.pdf",
              "taskDir": "/out/task",
              "mono": "/out/task/doc-mono.pdf",
              "dual": "/out/task/doc-dual.pdf",
              "report": {
                "pages": [
                  { "page": 3, "degraded": true, "reasons": ["table_fail"], "searchable": true },
                  { "page": 7, "degraded": true, "reasons": ["overflow_margin"], "searchable": true }
                ],
                "summary": { "degraded_pages": 2, "unsearchable_pages": 0, "total_pages": 10 }
              },
              "overflow": { "pages": 2, "clipPages": 1, "warn": true }
            }
          ]
        }
        """;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new EngineResult
        {
            Ok = true,
            OutputDir = root.GetProperty("outputDir").GetString(),
            Outputs = [.. root.GetProperty("outputs").EnumerateArray().Select(e => e.GetString() ?? "")],
            Extra = new Dictionary<string, JsonElement> { ["results"] = root.GetProperty("results").Clone() },
        };
    }

    [Fact]
    public async Task Start_OneFileFails_DoesNotAbortRest_AndOffersRetry()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        bridge.Handler = request => Path.GetFileName(request.Files[0]) == "b.pdf"
            ? new EngineResult { Ok = false, Error = "需要打开密码（未提供）", ExitCode = 1 }
            : RecordingBridge.Success();

        var vm = NewViewModel(bridge, temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf"), temp.CreatePdf("c.pdf"));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(3, bridge.Requests.Count); // b 失败后仍继续处理 c
        Assert.True(vm.Files[0].IsDone);
        Assert.True(vm.Files[1].IsFailed);
        Assert.Contains("需要打开密码", vm.Files[1].Error);
        Assert.True(vm.Files[2].IsDone);
        Assert.Equal(1, vm.FailedCount);
        Assert.True(vm.HasFailedItems);
        Assert.Equal("成功 2 · 失败 1 · 跳过 0", vm.SummaryText);
    }

    [Fact]
    public async Task RetryAllFailed_RerunsOnlyFailedItems()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var attempts = 0;
        bridge.Handler = request =>
        {
            if (Path.GetFileName(request.Files[0]) == "b.pdf" && Interlocked.Increment(ref attempts) == 1)
            {
                return new EngineResult { Ok = false, Error = "临时故障", ExitCode = 1 };
            }

            return RecordingBridge.Success();
        };

        var vm = NewViewModel(bridge, temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf"));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.FailedCount);

        await vm.RetryAllFailedCommand.ExecuteAsync(null);

        Assert.True(vm.Files[1].IsDone);
        Assert.Equal(0, vm.FailedCount);
        Assert.Equal(3, bridge.Requests.Count); // a + b + b(重试)，a 不再重跑
    }

    [Fact]
    public async Task Results_AreOpenedOnDemandOnly_NeverAutoOpened()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var mono = temp.CreatePdf("doc-mono.pdf");
        var dual = temp.CreatePdf("doc-dual.pdf");
        bridge.Handler = _ => new EngineResult
        {
            Ok = true,
            Outputs = [mono, dual],
            OutputDir = temp.Path,
            ExitCode = 0,
        };

        var vm = NewViewModel(bridge, temp.CreatePdf("doc.pdf"));
        var opened = new List<string>();
        vm.OpenDocumentRequested += (_, path) => opened.Add(path);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(opened); // 完成不自动开标签（避免 20×2 标签爆炸）

        vm.OpenMonoCommand.Execute(vm.Files[0]);
        vm.OpenDualCommand.Execute(vm.Files[0]);
        Assert.Equal([mono, dual], opened);
    }

    [Fact]
    public async Task Start_WithoutRuntimeConfig_ReportsConfiguredRequired()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var vm = new TranslationBatchViewModel(bridge, Tool(), new FixedConfigProvider(null));
        vm.AddFiles([temp.CreatePdf("a.pdf")]);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(bridge.Requests);
        Assert.Contains("翻译服务尚未配置", vm.StatusMessage);
    }

    [Fact]
    public async Task Start_ConsentRevokedMidRun_CancelsBatch_AndReportsRevocation()
    {
        using var temp = new TempDirectory();
        var provider = new RevocableConfigProvider(new TranslationRuntimeConfig(
            "deepseek", "https://api.deepseek.com/v1", "deepseek-chat", "sk-cloud", "auto", "zh", "academic"));
        var bridge = new BlockingBridge();
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider)
        {
            ConsentCheckInterval = TimeSpan.FromMilliseconds(10),
        };
        // 面板选中云端供应商：云端判定现在由「选中 id × 解析结果」派生（与生产配置一致）。
        vm.Config.SelectedProvider = "deepseek";
        vm.AddFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        var start = vm.StartCommand.ExecuteAsync(null);
        var deadline = Environment.TickCount64 + 3000;
        while (bridge.Requests.Count == 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(bridge.Requests); // 第一个文件已在途

        provider.Generation++; // 运行中撤销云端同意 → 在途批量必须停止

        await start;

        Assert.False(vm.IsRunning);
        Assert.Contains("同意已被撤销", vm.StatusMessage);
        Assert.Single(bridge.Requests); // 撤销后不再发起第二个文件
    }

    [Fact]
    public void BuildSubmitItems_ProducesOneItemPerFile_ForPartialFailureIsolation()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var vm = NewViewModel(bridge, temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf"));

        var items = vm.BuildSubmitItems([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        Assert.Equal(2, items.Count); // 逐文件一项：某份失败不中断其余
        Assert.All(items, item => Assert.Single(item.Files));
        Assert.Null(vm.ValidateParameters([temp.CreatePdf("a.pdf")]));
    }

    // ---------------------------------------------------------------- F22：批量中途重新解析配置

    [Fact]
    public async Task Start_EndpointChangedMidBatch_RemainingFilesUseNewEndpoint()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var provider = new MutableConfigProvider(RuntimeConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.AddFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        const string newEndpoint = "http://127.0.0.1:9999/v1";
        bridge.Handler = request =>
        {
            if (Path.GetFileName(request.Files[0]) == "a.pdf")
            {
                // 第一个文件在途时改端点：后续文件必须重新解析并改用新端点。
                provider.SetConfig(RuntimeConfig() with { Endpoint = newEndpoint });
            }

            return RecordingBridge.Success();
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal("http://127.0.0.1:11434/v1", bridge.Requests[0].Options["endpoint"]);
        Assert.Equal(newEndpoint, bridge.Requests[1].Options["endpoint"]);
        Assert.All(vm.Files, f => Assert.True(f.IsDone));
    }

    [Fact]
    public async Task Start_ProviderBecomesUnconfiguredMidBatch_StopsRemainingFiles_WithSpecificMessage()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var provider = new MutableConfigProvider(RuntimeConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.AddFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        bridge.Handler = request =>
        {
            if (Path.GetFileName(request.Files[0]) == "a.pdf")
            {
                // 第一个文件后配置变为「未同意」：第二个文件不得再发送。
                provider.SetResolution(NoConsentResolution);
            }

            return RecordingBridge.Success();
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Single(bridge.Requests); // 剩余文件未发送
        Assert.Contains("明确同意", vm.StatusMessage); // 展示具体原因，而非笼统文案
        Assert.True(vm.Files[0].IsDone);
        Assert.True(vm.Files[1].IsPending); // 未处理，保持待处理
    }

    // ---------------------------------------------------------------- F23：重试路径暴露具体原因

    [Fact]
    public async Task RetryFile_NonConfiguredResolution_SurfacesSpecificMessage()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        bridge.Handler = _ => new EngineResult { Ok = false, Error = "临时故障", ExitCode = 1 };
        var provider = new MutableConfigProvider(RuntimeConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.AddFiles([temp.CreatePdf("a.pdf")]);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.Files[0].IsFailed);
        var requestsAfterStart = bridge.Requests.Count;

        provider.SetResolution(NoConsentResolution);

        await vm.RetryFileCommand.ExecuteAsync(vm.Files[0]);

        Assert.Equal(requestsAfterStart, bridge.Requests.Count); // 未就绪不再发起请求
        Assert.Contains("明确同意", vm.StatusMessage); // 具体原因被原样展示
    }

    [Fact]
    public async Task RetryAllFailed_NonConfiguredResolution_SurfacesSpecificMessage()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        bridge.Handler = _ => new EngineResult { Ok = false, Error = "临时故障", ExitCode = 1 };
        var provider = new MutableConfigProvider(RuntimeConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.AddFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.FailedCount);
        var requestsAfterStart = bridge.Requests.Count;

        provider.SetResolution(NoConsentResolution);

        await vm.RetryAllFailedCommand.ExecuteAsync(null);

        Assert.Equal(requestsAfterStart, bridge.Requests.Count); // 未就绪不再发起请求
        Assert.Contains("明确同意", vm.StatusMessage);
    }

    // ---------------------------------------------------------------- 选中供应商透传：4 个解析点必须携带 Config.SelectedProvider

    /// <summary>
    /// 记录每次 Resolve 收到的选中供应商 id：4 个调用点（启动 EnsureAsync / 单文件重试 / 全部重试 /
    /// 每文件执行前）都必须透传面板选择，而非 null（null 会回退「本地优先」目的地）。
    /// </summary>
    private sealed class RecordingSelectedProviderConfigProvider(TranslationRuntimeConfig config)
        : ITranslationRuntimeConfigProvider
    {
        public List<string?> ReceivedSelectedProviderIds { get; } = [];

        public TranslationRuntimeConfig? Get() => config;

        public TranslationRuntimeConfigResolution Resolve(string? selectedProviderId = null)
        {
            ReceivedSelectedProviderIds.Add(selectedProviderId);
            return new TranslationRuntimeConfigResolution(
                TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint, false, "");
        }
    }

    private static TranslationRuntimeConfig DeepSeekConfig()
        => new("deepseek", "https://api.deepseek.com/v1", "deepseek-chat", "sk-cloud", "auto", "zh", "academic");

    [Fact]
    public async Task Start_PassesPanelSelectedProvider_ToEnsureConsentAndPerFileResolve()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var provider = new RecordingSelectedProviderConfigProvider(DeepSeekConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.Config.SelectedProvider = "deepseek"; // 面板选中云端供应商
        vm.AddFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal(3, provider.ReceivedSelectedProviderIds.Count); // EnsureAsync 1 次 + 每文件执行前 1 次
        Assert.All(provider.ReceivedSelectedProviderIds, id => Assert.Equal("deepseek", id));
    }

    [Fact]
    public async Task RetryFile_PassesPanelSelectedProvider_ToResolve()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        var attempts = 0;
        bridge.Handler = _ => Interlocked.Increment(ref attempts) == 1
            ? new EngineResult { Ok = false, Error = "临时故障", ExitCode = 1 }
            : RecordingBridge.Success();

        var provider = new RecordingSelectedProviderConfigProvider(DeepSeekConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.Config.SelectedProvider = "deepseek";
        vm.AddFiles([temp.CreatePdf("a.pdf")]);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.Files[0].IsFailed);

        provider.ReceivedSelectedProviderIds.Clear();

        await vm.RetryFileCommand.ExecuteAsync(vm.Files[0]);

        Assert.True(vm.Files[0].IsDone);
        Assert.Equal(2, provider.ReceivedSelectedProviderIds.Count); // 单文件重试 1 次 + 文件执行前 1 次
        Assert.All(provider.ReceivedSelectedProviderIds, id => Assert.Equal("deepseek", id));
    }

    [Fact]
    public async Task RetryAllFailed_PassesPanelSelectedProvider_ToResolve()
    {
        using var temp = new TempDirectory();
        var bridge = new RecordingBridge();
        bridge.Handler = _ => new EngineResult { Ok = false, Error = "临时故障", ExitCode = 1 };
        var provider = new RecordingSelectedProviderConfigProvider(DeepSeekConfig());
        var vm = new TranslationBatchViewModel(bridge, Tool(), provider);
        vm.Config.SelectedProvider = "deepseek";
        vm.AddFiles([temp.CreatePdf("a.pdf"), temp.CreatePdf("b.pdf")]);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.FailedCount);

        provider.ReceivedSelectedProviderIds.Clear();

        await vm.RetryAllFailedCommand.ExecuteAsync(null);

        Assert.Equal(3, provider.ReceivedSelectedProviderIds.Count); // 重试全部 1 次 + 每文件执行前 1 次
        Assert.All(provider.ReceivedSelectedProviderIds, id => Assert.Equal("deepseek", id));
    }

    // ---------------------------------------------------------------- V3 向导路径：校验 / 快照也必须携带面板选择（F3 审计回归）

    /// <summary>
    /// 记录每次 <c>Resolve</c> 收到的选中供应商 id，并可切换解析结果（负向用）：
    /// 覆盖向导参数步 ValidateParameters 与提交快照 BuildSubmitItems 两个解析点。
    /// </summary>
    private sealed class RecordingResolutionConfigProvider(TranslationRuntimeConfigResolution initial)
        : ITranslationRuntimeConfigProvider
    {
        private TranslationRuntimeConfigResolution _resolution = initial;

        public List<string?> ReceivedSelectedProviderIds { get; } = [];

        public void SetResolution(TranslationRuntimeConfigResolution next) => _resolution = next;

        public TranslationRuntimeConfig? Get() => _resolution.Config;

        public TranslationRuntimeConfigResolution Resolve(string? selectedProviderId = null)
        {
            ReceivedSelectedProviderIds.Add(selectedProviderId);
            return _resolution;
        }
    }

    private static TranslationRuntimeConfigResolution ConfiguredResolution(TranslationRuntimeConfig config)
        => new(
            TranslationRuntimeConfigStatus.Configured, config, config.Provider, config.Endpoint, IsCloud: false, "");

    [Fact]
    public void WizardPath_ValidateAndBuildSubmitItems_UsePanelSelectedProvider()
    {
        using var temp = new TempDirectory();
        var provider = new RecordingResolutionConfigProvider(ConfiguredResolution(DeepSeekConfig()));
        var vm = new TranslationBatchViewModel(new RecordingBridge(), Tool(), provider);
        vm.Config.SelectedProvider = "deepseek"; // 面板选中云端供应商
        var staged = new[] { temp.CreatePdf("a.pdf") };

        Assert.Null(vm.ValidateParameters(staged));

        var item = Assert.Single(vm.BuildSubmitItems(staged));

        Assert.Equal(2, provider.ReceivedSelectedProviderIds.Count); // 校验 1 次 + 快照 1 次
        Assert.All(provider.ReceivedSelectedProviderIds, id => Assert.Equal("deepseek", id));
        var endpoint = (string)item.Options["endpoint"]!;
        Assert.Equal("https://api.deepseek.com/v1", endpoint); // 目的地 = 面板选中，而非本地优先
        Assert.DoesNotContain("127.0.0.1", endpoint); // Get() / Resolve(null) 会回退本地，此处必须不发生
    }

    [Fact]
    public void WizardPath_UnusableSelectedProvider_SurfacesReadableReason_AndProducesNoSubmitItems()
    {
        using var temp = new TempDirectory();
        var provider = new RecordingResolutionConfigProvider(NoConsentResolution());
        var vm = new TranslationBatchViewModel(new RecordingBridge(), Tool(), provider);
        vm.Config.SelectedProvider = "deepseek";
        var staged = new[] { temp.CreatePdf("a.pdf") };

        var error = vm.ValidateParameters(staged);

        Assert.False(string.IsNullOrWhiteSpace(error)); // 可读非空原因
        Assert.Contains("明确同意", error); // 具体原因原样展示，而非笼统文案
        Assert.Empty(vm.BuildSubmitItems(staged)); // 无提交项 → 绝不回退本地 / 发往非预期目的地
        Assert.Contains("明确同意", vm.StatusMessage); // 快照无错误通道：原因落到同页状态
        Assert.All(provider.ReceivedSelectedProviderIds, id => Assert.Equal("deepseek", id));
    }

    /// <summary>测试用临时目录：释放时递归删除（句柄占用时容忍残留）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-t21-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreatePdf(string name)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, "%PDF-1.4\n");
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
