// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T15 面板内联展开回归：
//   - XAML 负向断言：不再含 v1.1「本地 / 云端」引擎单选（RadioButton / EngineToggle / IsLocalEngine / IsCloudEngine）；
//   - XAML 正向断言：含内联就地展开区的关键绑定（Inline.IsCollapsed / IsExpanded / ActionLabel / CanChangeProvider / ShowRunningHint）；
//   - VM 断言：TranslationConfigViewModel.Inline 就地折叠 / 展开、顶部行跟随供应商与模型、运行中禁用入口。
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tests.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.ViewModels.Translation;
using AIPDF.Windows.Views.Translation;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public sealed class TranslationConfigPanelTests
{
    /// <summary>XAML 负向 + 正向：引擎单选已移除，内联展开区存在。</summary>
    [Fact]
    public void PanelXaml_NoEngineRadio_AndHasInlineProviderRegion()
    {
        var xaml = File.ReadAllText(PanelXamlPath());

        // 旧「本地 / 云端」引擎单选（T9 移除，T15 不得复活）。
        Assert.DoesNotContain("RadioButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EngineToggle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLocalEngine", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCloudEngine", xaml, StringComparison.Ordinal);

        // 内联就地展开区（折叠 / 展开 / 运行中禁用）。
        Assert.Contains("Inline.IsCollapsed", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.IsExpanded", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.ActionLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.CanChangeProvider", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.ShowRunningHint", xaml, StringComparison.Ordinal);
    }

    /// <summary>折叠 → 展开 → 完成，全部就地（无窗口跳转）。</summary>
    [Fact]
    public void Config_Inline_ExpandsAndCollapses_InPlace()
    {
        var config = new TranslationConfigViewModel { SelectedProvider = "openai" };
        var inline = config.Inline;

        Assert.True(inline.IsExpandable);
        Assert.True(inline.IsCollapsed);
        Assert.Equal(TranslationProviderInlineViewModel.CollapsedActionLabel, inline.ActionLabel);
        Assert.Contains("OpenAI", inline.HeaderLabel, StringComparison.Ordinal);

        Assert.True(inline.Toggle());
        Assert.True(inline.IsExpanded);
        Assert.Equal(TranslationProviderInlineViewModel.ExpandedHeaderLabel, inline.HeaderLabel);
        Assert.Equal(TranslationProviderInlineViewModel.ExpandedActionLabel, inline.ActionLabel);

        inline.Complete();
        Assert.True(inline.IsCollapsed);
    }

    /// <summary>任务运行中：入口禁用 + 强制收起 + 提示先取消任务（P31）。</summary>
    [Fact]
    public void Config_Inline_DisabledWhileRunning()
    {
        var config = new TranslationConfigViewModel { SelectedProvider = "openai" };
        var inline = config.Inline;
        Assert.True(inline.Expand());

        inline.IsRunning = true;

        Assert.False(inline.IsExpandable);
        Assert.False(inline.CanChangeProvider);
        Assert.False(inline.IsExpanded);
        Assert.True(inline.ShowRunningHint);
        Assert.False(inline.Toggle());
    }

    /// <summary>收起后顶部行跟随新选中的供应商 + 模型。</summary>
    [Fact]
    public void Config_Inline_HeaderFollowsProviderAndModel()
    {
        var config = new TranslationConfigViewModel { SelectedProvider = "openai" };
        var inline = config.Inline;

        config.SelectedModel = "gpt-test-model";

        Assert.Equal("OpenAI · gpt-test-model", inline.HeaderLabel);

        config.SelectedProvider = "kimi-code";

        var preset = TranslationProviderPreset.Find("kimi-code")!;
        Assert.Equal($"Kimi Code · {preset.DefaultModels[0]}", inline.HeaderLabel);
    }

    /// <summary>v1.2 T15 修复：展开态含本地 API 地址 + 端口建议 chips + 校验 / 操作提示绑定。</summary>
    [Fact]
    public void PanelXaml_HasLocalEndpointAndPortSuggestionBindings()
    {
        var xaml = File.ReadAllText(PanelXamlPath());

        Assert.Contains("Inline.LocalEndpoint", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.PortSuggestions", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.LocalEndpointError", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.HasLocalEndpointError", xaml, StringComparison.Ordinal);
        Assert.Contains("Inline.OperationMessage", xaml, StringComparison.Ordinal);
        Assert.Contains("OnLocalPortSuggestionClick", xaml, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- v1.2 T15 修复：事件真正接到服务实例

    [Fact]
    public async Task InlineController_FetchModels_Local_FillsAvailableModelsAndReportsOk()
    {
        var handler = new ModelsHandler("""{"data":[{"id":"qwen2.5:7b"},{"id":"llama3.2:3b"}]}""");
        var (controller, _, _) = NewController(handler);
        var config = new TranslationConfigViewModel { SelectedProvider = "local" };
        config.Inline.LocalEndpoint = "http://127.0.0.1:11434/v1";

        await controller.FetchModelsAsync(config);

        Assert.Equal(new[] { "qwen2.5:7b", "llama3.2:3b" }, config.AvailableModels);
        Assert.Equal("qwen2.5:7b", config.SelectedModel);
        Assert.True(config.Inline.HasOperationMessage);
        Assert.False(config.Inline.IsOperationError);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InlineController_FetchModels_LocalNonLoopback_ReportsError_WithoutRequest()
    {
        var handler = new ModelsHandler("""{"data":[]}""");
        var (controller, _, _) = NewController(handler);
        var config = new TranslationConfigViewModel { SelectedProvider = "local" };
        config.Inline.LocalEndpoint = "http://192.168.1.20:8000/v1";

        await controller.FetchModelsAsync(config);

        Assert.True(config.Inline.IsOperationError);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void InlineController_SaveKey_StoresInCredentialStore_NotInSettingsFile()
    {
        var (controller, credentials, settings) = NewController(new ModelsHandler("""{"data":[]}"""));
        settings.Save(new AppSettings()); // 先落一份设置文件，断言密钥绝不进入 settings.json
        var config = new TranslationConfigViewModel { SelectedProvider = "local" };
        config.Inline.LocalEndpoint = "http://127.0.0.1:11434/v1";

        controller.SaveKey(config, "sk-inline-local-secret");

        Assert.Equal("sk-inline-local-secret", credentials.Get("local", "http://127.0.0.1:11434/v1"));
        Assert.DoesNotContain("sk-inline-local-secret", File.ReadAllText(settings.FilePath), StringComparison.Ordinal);
        Assert.False(config.Inline.IsOperationError);
    }

    [Fact]
    public async Task InlineController_FetchModels_Cloud_UsesSavedEndpointAndStoredKey()
    {
        var handler = new ModelsHandler("""{"data":[{"id":"deepseek-chat"}]}""");
        var (controller, credentials, settings) = NewController(handler);
        settings.Save(new AppSettings
        {
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = "DeepSeek",
                    Endpoint = "https://api.deepseek.com/v1",
                    Model = "deepseek-chat",
                    PresetId = "deepseek",
                },
            ],
        });
        credentials.Store("deepseek", "https://api.deepseek.com/v1", "sk-cloud-secret");

        var config = new TranslationConfigViewModel(settings.Load().TranslationProviders)
        {
            SelectedProvider = "deepseek",
        };

        await controller.FetchModelsAsync(config);

        Assert.False(config.Inline.IsOperationError);
        Assert.Contains("deepseek-chat", config.AvailableModels);
        Assert.Equal("https://api.deepseek.com/v1/models", handler.LastUri);
        Assert.Equal("sk-cloud-secret", handler.LastAuthorization);
    }

    [Fact]
    public async Task InlineController_TestConnection_Local_ReportsPass()
    {
        var (controller, _, _) = NewController(new ModelsHandler("""{"data":[]}"""));
        var config = new TranslationConfigViewModel { SelectedProvider = "local" };
        config.Inline.LocalEndpoint = "http://127.0.0.1:11434/v1";

        await controller.TestConnectionAsync(config);

        Assert.False(config.Inline.IsOperationError);
        Assert.Contains("通过", config.Inline.OperationMessage, StringComparison.Ordinal);
    }

    private static (TranslationInlineProviderController Controller, TranslationCredentialStore Credentials, SettingsStore Settings)
        NewController(HttpMessageHandler handler)
    {
        var settings = new SettingsStore(TempSettingsPath());
        var credentials = new TranslationCredentialStore(new InMemoryCredentialBackend());
        var controller = new TranslationInlineProviderController(settings, credentials, () => new LocalModelFetcher(handler));
        return (controller, credentials, settings);
    }

    private static string TempSettingsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aipdf-inline-panel-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "settings.json");
    }

    /// <summary>内存 HTTP 替身：返回固定模型列表 JSON，记录调用次数 / URI / Authorization（不发起真实网络请求）。</summary>
    private sealed class ModelsHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.Parameter;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static string PanelXamlPath()
        => Path.Combine(WindowsProjectDir(), "Views", "Translation", "TranslationConfigPanel.xaml");

    private static string WindowsProjectDir()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Sources", "AIPDF.Windows");
            if (File.Exists(Path.Combine(candidate, "App.xaml")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 AIPDF.Windows 工程目录（Sources/AIPDF.Windows/App.xaml）");
    }
}
