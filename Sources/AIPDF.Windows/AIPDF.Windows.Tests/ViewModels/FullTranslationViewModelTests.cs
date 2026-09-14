// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 整篇翻译视图模型单测（纯逻辑，不依赖桌面 / 真实引擎）：
//   文档缺失 / 未配置 / 受限确认门禁 / 成功结果与披露 / 取消 / 超时 / 后台继续（会话内）/ 模板映射 / 扫描页提示。
using System.IO;
using System.Linq;
using System.Threading;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
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
        IReadOnlyList<FullTranslationArtifact>? artifacts = null,
        FullTranslationSearchSummary? searchSummary = null)
        => new(
            true, null, false, "out-dir", "task-dir",
            artifacts ?? [new FullTranslationArtifact("mono", "/out/report-mono.pdf"), new FullTranslationArtifact("dual", "/out/report-dual.pdf")],
            issues ?? [],
            new FullTranslationSourceProtection(restricted, disclosure),
            2.5,
            searchSummary);

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
    public async Task Start_HighRiskTemplate_DeclinedConfirmation_DoesNotTranslate()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Config.SelectedTemplate = vm.Config.TemplateOptions.First(t => t.Id == "legal");
        string? askedLabel = null;
        vm.ConfirmHighRiskTemplateAsync = label => { askedLabel = label; return Task.FromResult(false); };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.Calls);
        Assert.Equal("法律", askedLabel);
        Assert.Contains("未确认高风险模板", vm.StatusMessage);
        Assert.True(vm.Config.RequiresHighRiskConfirmation);
    }

    [Fact]
    public async Task Start_HighRiskTemplate_Confirmed_Translates()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Config.SelectedTemplate = vm.Config.TemplateOptions.First(t => t.Id == "medical");
        var asked = false;
        vm.ConfirmHighRiskTemplateAsync = _ => { asked = true; return Task.FromResult(true); };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(asked);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Start_HighRiskTemplate_WithoutConfirmationDelegate_DoesNotTranslate()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Config.SelectedTemplate = vm.Config.TemplateOptions.First(t => t.Id == "legal");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, service.Calls);
        Assert.Contains("未确认高风险模板", vm.StatusMessage);
    }

    [Fact]
    public async Task Start_HighRiskTemplate_ForcesMarker_EvenWhenSettingDisabled()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Config.SelectedTemplate = vm.Config.TemplateOptions.First(t => t.Id == "legal");
        vm.ConfirmHighRiskTemplateAsync = _ => Task.FromResult(true);
        vm.FooterMarkerProvider = () => false; // 设置关闭页脚标记

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRequest);
        Assert.True(service.LastRequest!.Marker); // 高风险强制写标记，忽略设置
    }

    [Fact]
    public async Task Start_NormalTemplate_MarkerFollowsSetting()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.FooterMarkerProvider = () => false;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRequest);
        Assert.False(service.LastRequest!.Marker); // 一般文档可关

        var service2 = new FakeFullTranslationService();
        var vm2 = NewViewModel(service2);
        await vm2.StartCommand.ExecuteAsync(null);
        Assert.True(service2.LastRequest!.Marker); // 默认开
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
        Assert.Equal("打开纯译文", vm.Artifacts[0].OpenLabel);
        Assert.Equal("打开对照", vm.Artifacts[1].OpenLabel);
        Assert.True(vm.ShowRestrictionDisclosure);
        Assert.Equal(disclosure, vm.RestrictionDisclosure);
        Assert.True(vm.HasDegradation);
        Assert.Single(vm.Degradations);
        Assert.Equal(3, vm.Degradations[0].Page);
        Assert.Contains("该页表格结构可能丢失", vm.Degradations[0].LabelText);
        Assert.Equal(100, vm.ProgressPercent);
    }

    [Fact]
    public async Task Start_Success_ShowsSearchableSummary_AndAggregateUploadDisclosure()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service); // 默认 runtime provider = local
        service.Handler = (_, _, _) => Task.FromResult(Success(
            searchSummary: new FullTranslationSearchSummary(2, 1, 12)));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasSearchSummary);
        Assert.False(vm.IsSearchSummaryOk);
        Assert.Contains("11/12 页可提取文本", vm.SearchSummaryText);
        Assert.Contains("1 页不可搜索", vm.SearchSummaryText);
        Assert.True(vm.ShowUploadDisclosure);
        Assert.Contains("本地引擎", vm.UploadDisclosure);
        Assert.Contains("全文未离开本机", vm.UploadDisclosure);
    }

    [Fact]
    public async Task Start_Success_AllSearchable_ReportsPass()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        service.Handler = (_, _, _) => Task.FromResult(Success(
            searchSummary: new FullTranslationSearchSummary(2, 0, 12)));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsSearchSummaryOk);
        Assert.Contains("通过", vm.SearchSummaryText);
        Assert.Contains("0 页意外不可搜索", vm.SearchSummaryText);
    }

    [Fact]
    public void OpenArtifact_ExistingFile_RaisesOpenRequest()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        var existing = Path.GetTempFileName();
        try
        {
            var artifact = new FullTranslationArtifactViewModel("mono", existing);
            FullTranslationArtifactViewModel? opened = null;
            vm.OpenArtifactRequested += (_, a) => opened = a;

            vm.OpenArtifactCommand.Execute(artifact);

            Assert.Same(artifact, opened);
            Assert.False(vm.HasArtifactError);
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Fact]
    public void OpenArtifact_MissingFile_ShowsGracefulError_AndDismisses()
    {
        var vm = NewViewModel(new FakeFullTranslationService());
        var missing = new FullTranslationArtifactViewModel(
            "dual", Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".pdf"));
        var raised = false;
        vm.OpenArtifactRequested += (_, _) => raised = true;

        vm.OpenArtifactCommand.Execute(missing);

        Assert.False(raised); // 不静默打开空白标签
        Assert.True(vm.HasArtifactError);
        Assert.Contains("找不到产物文件", vm.ArtifactErrorMessage);

        vm.DismissArtifactErrorCommand.Execute(null);
        Assert.False(vm.HasArtifactError);
        Assert.Null(vm.ArtifactErrorMessage);
    }

    [Fact]
    public void OpenArtifact_MissingFile_RelocateOpensNewLocation()
    {
        var vm = NewViewModel(new FakeFullTranslationService());
        var relocated = Path.GetTempFileName();
        try
        {
            vm.RelocateArtifactHandler = _ => relocated;
            var missing = new FullTranslationArtifactViewModel(
                "mono", Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N") + ".pdf"));
            FullTranslationArtifactViewModel? opened = null;
            vm.OpenArtifactRequested += (_, a) => opened = a;

            vm.OpenArtifactCommand.Execute(missing);
            Assert.True(vm.HasArtifactError);

            vm.RelocateArtifactCommand.Execute(null);

            Assert.NotNull(opened);
            Assert.Equal(relocated, opened!.Path);
            Assert.Equal("mono", opened.Kind);
            Assert.False(vm.HasArtifactError);
        }
        finally
        {
            File.Delete(relocated);
        }
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
        Assert.Equal("custom", FullTranslationViewModel.ToEngineTemplate("custom"));
        Assert.Equal("generic", FullTranslationViewModel.ToEngineTemplate(null));
    }

    [Fact]
    public async Task Start_CustomTemplate_SendsCustomPromptAndMarkdown()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Config.SelectedTemplate = vm.Config.TemplateOptions.First(t => t.IsCustom);
        vm.Config.CustomPrompt = "把下文翻译为简洁的商务中文，保留数字与专名。";
        vm.Config.ExportMarkdown = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRequest);
        Assert.Equal("custom", service.LastRequest!.Template);
        Assert.Equal("把下文翻译为简洁的商务中文，保留数字与专名。", service.LastRequest.CustomPrompt);
        Assert.True(service.LastRequest.ExportMarkdown);
    }

    [Fact]
    public async Task Start_NonCustomTemplate_OmitsCustomPrompt()
    {
        var service = new FakeFullTranslationService();
        var vm = NewViewModel(service);
        vm.Config.CustomPrompt = "这段不应被发送。"; // 模板非自定义时忽略

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRequest);
        Assert.Null(service.LastRequest!.CustomPrompt);
        Assert.False(service.LastRequest.ExportMarkdown);
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
