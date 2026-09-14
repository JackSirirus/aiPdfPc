// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 批量翻译视图模型单测（纯逻辑，不依赖桌面 / 真实引擎）：
// 串行逐文件执行（顺序即列表顺序）/ 单份失败不中断整体 / 共享配置映射 / 失败项重试 /
// 结果【按需打开】（绝不自动触发 OpenDocumentRequested）。
using System.IO;
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
        });
        Assert.All(vm.Files, f => Assert.True(f.IsDone));
        Assert.Equal(3, vm.SuccessCount);
        Assert.False(vm.HasFailedItems);
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
