// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T10 面板内联快捷入口单测（设计参考 P27–P31）：
//   - 折叠 / 展开状态机与文案（供应商 · 模型 + 更换 ▾ / 模型供应商 + 收起 ▴）；
//   - 运行中禁用展开入口并强制收起（提示先取消任务）；
//   - 只读跟随共享选择：供应商切换 → 摘要同步。
using AIPDF.Windows.ViewModels.Translation;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public class TranslationProviderInlineViewModelTests
{
    private static TranslationProviderReadout NewReadout(
        string providerId = "deepseek",
        string displayName = "DeepSeek",
        string model = "deepseek-chat",
        string protocol = "openai")
    {
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot(providerId, displayName, model, protocol));
        readout.Refresh();
        return readout;
    }

    [Fact]
    public void StartsCollapsed_WithProviderModelSummaryAndChangeAction()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());

        Assert.True(inline.IsCollapsed);
        Assert.False(inline.IsExpanded);
        Assert.True(inline.IsExpandable);
        Assert.True(inline.CanChangeProvider);
        Assert.True(inline.IsConfigEnabled);
        Assert.False(inline.ShowRunningHint);
        Assert.Equal("DeepSeek · deepseek-chat", inline.SummaryLabel);
        Assert.Equal("DeepSeek · deepseek-chat", inline.HeaderLabel);
        Assert.Equal(TranslationProviderInlineViewModel.CollapsedActionLabel, inline.ActionLabel);
        Assert.Equal("DeepSeek", inline.ProviderLabel);
        Assert.Equal(TranslationProviderReadout.OpenAiBadge, inline.ProtocolBadge);
    }

    [Fact]
    public void Toggle_ExpandsAndCollapses_WithHeaderAndCaret()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());

        Assert.True(inline.Toggle());
        Assert.True(inline.IsExpanded);
        Assert.Equal(TranslationProviderInlineViewModel.ExpandedHeaderLabel, inline.HeaderLabel);
        Assert.Equal(TranslationProviderInlineViewModel.ExpandedActionLabel, inline.ActionLabel);

        Assert.True(inline.Toggle());
        Assert.True(inline.IsCollapsed);
        Assert.Equal("DeepSeek · deepseek-chat", inline.HeaderLabel);
        Assert.Equal(TranslationProviderInlineViewModel.CollapsedActionLabel, inline.ActionLabel);
    }

    [Fact]
    public void Expand_CompleteAndCancel_ReturnToCollapsed()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());

        Assert.True(inline.Expand());
        inline.Complete();
        Assert.True(inline.IsCollapsed);

        Assert.True(inline.Expand());
        inline.Cancel();
        Assert.True(inline.IsCollapsed);
    }

    [Fact]
    public void Collapse_AlwaysAllowed()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());
        inline.Collapse();
        Assert.True(inline.IsCollapsed);
    }

    [Fact]
    public void Running_DisablesExpand_ForcesCollapseAndShowsHint()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());
        Assert.True(inline.Expand());

        // QA 场景：IsRunning = true → 内联展开入口被禁用（提示先取消任务）。
        inline.IsRunning = true;

        Assert.True(inline.IsCollapsed);
        Assert.False(inline.IsExpanded);
        Assert.False(inline.IsExpandable);
        Assert.False(inline.CanChangeProvider);
        Assert.False(inline.IsConfigEnabled);
        Assert.True(inline.ShowRunningHint);
        Assert.False(inline.Toggle());
        Assert.False(inline.Expand());
        Assert.True(inline.IsCollapsed);
        Assert.Contains("先取消任务", inline.RunningHintText);
    }

    [Fact]
    public void RunningEnds_AllowsExpandAgain()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());
        inline.IsRunning = true;
        inline.IsRunning = false;

        Assert.True(inline.IsExpandable);
        Assert.True(inline.CanChangeProvider);
        Assert.False(inline.ShowRunningHint);
        Assert.True(inline.Expand());
    }

    [Fact]
    public void FollowsReadout_WhenSharedProviderChanges()
    {
        var displayName = "DeepSeek";
        var model = "deepseek-chat";
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot("deepseek", displayName, model, "openai"));
        readout.Refresh();

        var inline = new TranslationProviderInlineViewModel(readout);
        Assert.Equal("DeepSeek · deepseek-chat", inline.HeaderLabel);

        displayName = "Kimi Code";
        model = "kimi-k3";
        readout.Refresh();

        Assert.Equal("Kimi Code · kimi-k3", inline.SummaryLabel);
        Assert.Equal("Kimi Code · kimi-k3", inline.HeaderLabel);
        Assert.Equal("Kimi Code", inline.ProviderLabel);
    }

    [Fact]
    public void ExpandedHeader_StaysTitle_WhenProviderChanges()
    {
        var displayName = "DeepSeek";
        var readout = new TranslationProviderReadout(
            () => new TranslationProviderSnapshot("deepseek", displayName, "deepseek-chat", "openai"));
        readout.Refresh();

        var inline = new TranslationProviderInlineViewModel(readout);
        inline.Expand();

        displayName = "Kimi Code";
        readout.Refresh();

        Assert.Equal(TranslationProviderInlineViewModel.ExpandedHeaderLabel, inline.HeaderLabel);
        Assert.Equal("Kimi Code", inline.ProviderLabel);
    }

    // ---------------------------------------------------------------- v1.2 T15 修复：本地地址 + 端口建议 + 操作提示

    [Fact]
    public void LocalEndpoint_DefaultsToOllamaLoopbackTemplate_AndValidates()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());

        Assert.Equal(TranslationProviderInlineViewModel.LocalDefaultEndpoint, inline.LocalEndpoint);
        Assert.Null(inline.LocalEndpointError);
        Assert.False(inline.HasLocalEndpointError);

        // 非 loopback（含明文 http 的局域网地址）→ UI 层报错，不允许以「本地」名义使用。
        inline.LocalEndpoint = "http://192.168.1.20:8000/v1";
        Assert.True(inline.HasLocalEndpointError);
        Assert.Contains("loopback", inline.LocalEndpointError, StringComparison.OrdinalIgnoreCase);

        // 格式错（无 scheme）→ 报错。
        inline.LocalEndpoint = "localhost:11434";
        Assert.True(inline.HasLocalEndpointError);

        // 合法 loopback → 无错误。
        inline.LocalEndpoint = "http://localhost:11434/v1";
        Assert.False(inline.HasLocalEndpointError);

        // 清空 → 不报错（提交动作再给「请填写」引导）。
        inline.LocalEndpoint = "";
        Assert.False(inline.HasLocalEndpointError);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", 8000, "http://127.0.0.1:8000/v1")]
    [InlineData("http://localhost:11434/llm", 1234, "http://localhost:1234/llm")]
    [InlineData("https://127.0.0.1:11434/v1/", 8080, "https://127.0.0.1:8080/v1")]
    [InlineData("", 11434, "http://127.0.0.1:11434/v1")]
    [InlineData("localhost:11434", 8000, "http://127.0.0.1:8000/v1")]
    public void ApplyPortToAddress_ReplacesPortOrFallsBackToTemplate(string current, int port, string expected)
        => Assert.Equal(expected, TranslationProviderInlineViewModel.ApplyPortToAddress(current, port));

    [Fact]
    public void ApplyPortSuggestion_UpdatesEndpoint_PreservingHostAndPath()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());
        inline.LocalEndpoint = "http://127.0.0.1:11434/v1";

        inline.ApplyPortSuggestion(1234);

        Assert.Equal("http://127.0.0.1:1234/v1", inline.LocalEndpoint);
        Assert.False(inline.HasLocalEndpointError);
    }

    [Fact]
    public void PortSuggestions_ExposeCommonLocalPorts()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());

        Assert.Equal(new[] { 11434, 1234, 8000, 8080 }, inline.PortSuggestions.Select(s => s.Port).ToArray());
        Assert.Equal("11434 Ollama", inline.PortSuggestions[0].Label);
    }

    [Fact]
    public void SetOperationMessage_TogglesFlags_AndClearResets()
    {
        var inline = new TranslationProviderInlineViewModel(NewReadout());
        Assert.False(inline.HasOperationMessage);

        inline.SetOperationMessage("已拉取 2 个模型。");
        Assert.True(inline.HasOperationMessage);
        Assert.False(inline.IsOperationError);
        Assert.Equal("已拉取 2 个模型。", inline.OperationMessage);

        inline.SetOperationMessage("测试连接失败：请求超时。", isError: true);
        Assert.True(inline.IsOperationError);

        inline.ClearOperationMessage();
        Assert.False(inline.HasOperationMessage);
        Assert.Equal("", inline.OperationMessage);
    }
}
