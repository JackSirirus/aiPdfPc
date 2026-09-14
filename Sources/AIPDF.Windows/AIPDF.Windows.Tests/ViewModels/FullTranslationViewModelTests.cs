// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 整篇翻译视图模型单测（纯逻辑，不依赖桌面 / 真实引擎）：
//   文档缺失 / 未配置 / 受限确认门禁 / 成功结果与披露 / 取消 / 超时 / 后台继续（会话内）/ 模板映射 / 扫描页提示。
using System.IO;
using System.Threading;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels.Translation;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public sealed class FullTranslationViewModelTests
{
    private sealed class FixedConfigProvider(TranslationRuntimeConfig? config) : ITranslationRuntimeConfigProvider
    {
        public TranslationRuntimeConfig? Get() => config;
    }

    private sealed class FakeFullTranslationService : IFullTranslationService
    {
        public Func<FullTranslationRequest, IProgress<FullTranslationProgress>?, CancellationToken, Task<FullTranslationOutcome>> Handler
        { get; set; } = (_, _, _) => Task.FromResult(Success());

        public int Calls { get; private set; }

        public FullTranslationRequest? LastRequest { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task<FullTranslationOutcome> TranslateAsync(
            FullTranslationRequest request,
            IProgress<FullTranslationProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            LastToken = cancellationToken;
            return Handler(request, progress, cancellationToken);
        }
    }

    private static FullTranslationOutcome Success(
        bool restricted = false,
        string? disclosure = null,
        IReadOnlyList<FullTranslationPageIssue>? issues = null,
        IReadOnlyList<FullTranslationArtifact>? artifacts = null)
        => new(
            true, null, false, "out-dir", "task-dir",
            artifacts ?? [new FullTranslationArtifact("mono", "/out/report-mono.pdf"), new FullTranslationArtifact("dual", "/out/report-dual.pdf")],
            issues ?? [],
            new FullTranslationSourceProtection(restricted, disclosure),
            2.5);

    private static TranslationRuntimeConfig RuntimeConfig()
        => new("local", "http://127.0.0.1:11434/v1", "qwen", "sk-test", "auto", "zh", "academic");

    private static FullTranslationViewModel NewViewModel(
        FakeFullTranslationService service,
        TranslationRuntimeConfig? config = null)
    {
        var vm = new FullTranslationViewModel(service, new FixedConfigProvider(config ?? RuntimeConfig()));
        vm.ActiveDocumentPath = Path.Combine(Path.GetTempPath(), "source.pdf");
        vm.OutputDir = Path.GetTempPath();
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("条件在超时前未满足。");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Start_WithoutDocument_ReportsError_AndDoesNotCallService()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.ActiveDocumentPath = "";

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.Calls);
        Assert.Contains("请先打开", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Start_WithoutRuntimeConfig_ReportsNotConfigured_AndDoesNotCallService()
    {
        var service = new FakeFullTranslationService();
        var vm = new FullTranslationViewModel(service, new FixedConfigProvider(null))
        {
            ActiveDocumentPath = Path.Combine(Path.GetTempPath(), "source.pdf"),
            OutputDir = Path.GetTempPath(),
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.Calls);
        Assert.Contains("尚未配置", vm.StatusMessage);
    }

    [Fact]
    public async Task Start_RestrictedDocument_DeclinedConfirmation_DoesNotTranslate()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.IsRestrictedDocument = true;
        vm.ConfirmRestrictedDocumentAsync = () => Task.FromResult(false);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.Calls);
        Assert.Contains("未确认", vm.StatusMessage);
        Assert.True(vm.RequiresRestrictedConfirmation);
    }

    [Fact]
    public async Task Start_RestrictedDocument_Confirmed_Translates()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.IsRestrictedDocument = true;
        var asked = false;
        vm.ConfirmRestrictedDocumentAsync = () => { asked = true; return Task.FromResult(true); };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(asked);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Start_Success_PopulatesArtifactsDisclosureAndDegradation()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.IsRestrictedDocument = true;
        vm.ConfirmRestrictedDocumentAsync = () => Task.FromResult(true);
        var disclosure = FullTranslationViewModel.RestrictionDisclosureFallback;
        service.Handler = (_, _, _) => Task.FromResult(Success(
            restricted: true,
            disclosure: disclosure,
            issues: [new FullTranslationPageIssue(3, ["table_fail"])]));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.True(vm.HasResults);
        Assert.Equal(2, vm.Artifacts.Count);
        Assert.Equal("MONO", vm.Artifacts[0].Badge);
        Assert.True(vm.ShowRestrictionDisclosure);
        Assert.Equal(disclosure, vm.RestrictionDisclosure);
        Assert.True(vm.HasDegradation);
        Assert.Contains("每页降级：1 页", vm.DegradationSummary);
        Assert.Equal(100, vm.ProgressPercent);
    }

    [Fact]
    public async Task Start_SendsEngineTemplateMapping_AndOutputDir()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRequest);
        Assert.Equal("academic", service.LastRequest!.Template);
        Assert.Equal(Path.GetTempPath(), service.LastRequest.OutputDir);
        Assert.Equal("http://127.0.0.1:11434/v1", service.LastRequest.Endpoint);
        Assert.Equal("sk-test", service.LastRequest.ApiKey);
    }

    [Fact]
    public async Task Start_EngineFailure_SetsErrorStatus()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        service.Handler = (_, _, _) => Task.FromResult(
            new FullTranslationOutcome(false, "模型拒绝翻译。", false, null, null, [], [], FullTranslationSourceProtection.None, 0));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsErrorStatus);
        Assert.Contains("失败", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Start_UserCancel_ReportsCancelled_NotError()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        service.Handler = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // 真实服务把取消转成 Cancelled 结果。
            }

            return FullTranslationOutcome.Cancelled_;
        };

        var start = vm.StartCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsRunning);
        vm.CancelCommand.Execute(null);
        await start;

        Assert.Contains("已取消", vm.StatusMessage);
        Assert.False(vm.IsErrorStatus);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Start_Timeout_ReportsTimedOut()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Timeout = TimeSpan.FromMilliseconds(30);
        service.Handler = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }

            return FullTranslationOutcome.Cancelled_;
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("已超时", vm.StatusMessage);
        Assert.True(vm.IsErrorStatus);
    }

    [Fact]
    public async Task BackgroundContinue_IsSessionScoped_AndReturnable()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        service.Handler = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }

            return FullTranslationOutcome.Cancelled_;
        };

        var start = vm.StartCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsRunning);

        vm.BackgroundContinueCommand.Execute(null);
        Assert.True(vm.IsBackgrounded);
        Assert.False(vm.ShowTaskView);
        Assert.True(vm.ShowBackgroundNotice);
        Assert.Equal("仅本次会话内有效，应用退出即丢失未导出内容", vm.SessionScopeText);

        vm.ReturnToTaskCommand.Execute(null);
        Assert.False(vm.IsBackgrounded);
        Assert.True(vm.ShowTaskView);

        vm.CancelCommand.Execute(null);
        await start;
    }

    [Fact]
    public void ToEngineTemplate_MapsConfigIdsToEngineKeys()
    {
        Assert.Equal("academic", FullTranslationViewModel.ToEngineTemplate("academic"));
        Assert.Equal("legal", FullTranslationViewModel.ToEngineTemplate("legal"));
        Assert.Equal("generic", FullTranslationViewModel.ToEngineTemplate("general"));
        Assert.Equal("generic", FullTranslationViewModel.ToEngineTemplate("custom"));
        Assert.Equal("generic", FullTranslationViewModel.ToEngineTemplate(null));
    }

    [Fact]
    public void ScanNotice_AppearsOnlyWhenScanPagesDetected()
    {
        var vm = NewViewModel(new FakeFullTranslationService());
        Assert.False(vm.ShowScanNotice);

        vm.ScannedPageCount = 2;

        Assert.True(vm.ShowScanNotice);
        Assert.Contains("检测到 2 页扫描件", vm.ScanNoticeText);
        Assert.Contains("仅白底图片页效果好", vm.ScanNoticeText);
    }

    [Fact]
    public async Task Start_InvalidOutputDir_ReportsError()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.OutputDir = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.Calls);
        Assert.Contains("输出文件夹", vm.StatusMessage);
    }
}
