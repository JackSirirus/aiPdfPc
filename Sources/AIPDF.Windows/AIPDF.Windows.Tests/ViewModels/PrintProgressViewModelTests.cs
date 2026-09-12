// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T6 状态栏打印进度状态机测试（纯逻辑，不依赖真实打印机 / 桌面会话）。
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public sealed class PrintProgressViewModelTests
{
    [Fact]
    public void Begin_ActivatesAndShowsZeroOfTotal()
    {
        var vm = new PrintProgressViewModel();
        vm.Begin(12);
        Assert.True(vm.IsActive);
        Assert.Equal("正在准备打印 0 / 12 页…", vm.Text);
    }

    [Fact]
    public void Report_UpdatesCompletedCount()
    {
        var vm = new PrintProgressViewModel();
        vm.Begin(12);
        vm.Report(new PrintProgress(3, 12, 2));
        Assert.Equal("正在准备打印 3 / 12 页…", vm.Text);
    }

    [Fact]
    public void Report_ClampsToTotal()
    {
        var vm = new PrintProgressViewModel();
        vm.Begin(2);
        vm.Report(new PrintProgress(99, 2, 1));
        Assert.Equal("正在准备打印 2 / 2 页…", vm.Text);
    }

    [Fact]
    public void Report_AfterEnd_IsIgnored()
    {
        var vm = new PrintProgressViewModel();
        vm.Begin(4);
        vm.End();
        vm.Report(new PrintProgress(2, 4, 1));
        Assert.False(vm.IsActive);
        Assert.Equal(string.Empty, vm.Text);
    }

    [Fact]
    public void End_DeactivatesAndClearsText()
    {
        var vm = new PrintProgressViewModel();
        vm.Begin(4);
        vm.End();
        Assert.False(vm.IsActive);
        Assert.Equal(string.Empty, vm.Text);
    }

    [Fact]
    public void Begin_NonPositiveTotal_StillProducesReadableText()
    {
        var vm = new PrintProgressViewModel();
        vm.Begin(0);
        Assert.Equal("正在准备打印 0 / 1 页…", vm.Text);
    }
}
