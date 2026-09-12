// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T5 设置注入决策测试（纯逻辑，无 UI）：覆盖
//   - DefaultZoom：有 reading-state 用状态（SoT），否则用设置默认（数值/适配档）；
//   - DefaultPanelKey：有状态用状态（含 search），否则用设置默认（不含 search）；
//   - RecentFilesLimit 钳制；Theme/PrintDpi/RenderBaseDpi/OutputDir 归一化取值；
//   - SeedOutputDir：空设置不动作、空属性才写、已设置不覆盖、不适用对象跳过；
//   - 与 T1 不变量一致：设置默认渲染 DPI 不改 DpiForZoom 阈值阶梯（默认 baseDpi 调用不变）。
using AIPDF.Windows.Services;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class SettingsInjectionTests
{
    // ---------------------------------------------------------------- DefaultZoom

    [Theory]
    [InlineData("25", 25.0)]
    [InlineData("100", 100.0)]
    [InlineData("150", 150.0)]
    [InlineData("400", 400.0)]
    public void ResolveInitialZoom_NoState_UsesSettingPercent(string setting, double expected)
    {
        var decision = SettingsInjection.ResolveInitialZoom(hasReadingState: false, 0, setting);

        Assert.Equal(InitialZoomMode.Percent, decision.Mode);
        Assert.Equal(expected, decision.Percent);
    }

    [Theory]
    [InlineData("fit-width", InitialZoomMode.FitWidth)]
    [InlineData("fit-page", InitialZoomMode.FitPage)]
    public void ResolveInitialZoom_NoState_FitModes(string setting, InitialZoomMode expected)
    {
        Assert.Equal(expected, SettingsInjection.ResolveInitialZoom(false, 0, setting).Mode);
    }

    [Theory]
    [InlineData("120")]      // 非阶梯档
    [InlineData("bogus")]    // 未知
    [InlineData("")]
    [InlineData(null)]
    public void ResolveInitialZoom_NoState_InvalidSetting_FallsBackTo100(string? setting)
    {
        var decision = SettingsInjection.ResolveInitialZoom(false, 0, setting);

        Assert.Equal(InitialZoomMode.Percent, decision.Mode);
        Assert.Equal(100.0, decision.Percent);
    }

    [Fact]
    public void ResolveInitialZoom_WithState_StateIsSourceOfTruth()
    {
        // 状态=150%，设置=fit-width：状态优先（SoT），不被设置覆盖。
        var decision = SettingsInjection.ResolveInitialZoom(hasReadingState: true, 150, "fit-width");

        Assert.Equal(InitialZoomMode.Percent, decision.Mode);
        Assert.Equal(150.0, decision.Percent);
    }

    [Fact]
    public void ResolveInitialZoom_WithStateButNoZoom_UsesSettingDefault()
    {
        var decision = SettingsInjection.ResolveInitialZoom(hasReadingState: true, 0, "200");

        Assert.Equal(InitialZoomMode.Percent, decision.Mode);
        Assert.Equal(200.0, decision.Percent);
    }

    // ---------------------------------------------------------------- DefaultPanelKey

    [Theory]
    [InlineData("thumbs")]
    [InlineData("outline")]
    [InlineData("bookmarks")]
    [InlineData("search")] // reading-state 允许 search（由 Ctrl+F 接管语义）
    public void ResolvePanelKey_WithState_UsesStateKey(string stateKey)
    {
        Assert.Equal(stateKey, SettingsInjection.ResolvePanelKey(true, stateKey, "outline"));
    }

    [Fact]
    public void ResolvePanelKey_WithState_InvalidStateKey_FallsBackToSetting()
    {
        Assert.Equal("outline", SettingsInjection.ResolvePanelKey(true, "bogus", "outline"));
    }

    [Theory]
    [InlineData("thumbs")]
    [InlineData("outline")]
    [InlineData("bookmarks")]
    public void ResolvePanelKey_NoState_UsesSettingKey(string settingKey)
    {
        Assert.Equal(settingKey, SettingsInjection.ResolvePanelKey(false, null, settingKey));
    }

    [Theory]
    [InlineData("search")] // 设置默认不允许 search
    [InlineData("nonsense")]
    [InlineData(null)]
    public void ResolvePanelKey_NoState_InvalidSetting_Thumbs(string? settingKey)
    {
        Assert.Equal("thumbs", SettingsInjection.ResolvePanelKey(false, "outline", settingKey));
    }

    // ---------------------------------------------------------------- 归一化取值

    [Theory]
    [InlineData(5, 10)]
    [InlineData(99, 50)]
    [InlineData(20, 20)]
    public void ResolveRecentFilesLimit_ClampsToRange(int stored, int expected)
    {
        Assert.Equal(expected, SettingsInjection.ResolveRecentFilesLimit(stored));
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("neon", "system")]
    [InlineData(null, "system")]
    public void ResolveTheme_UnknownFallsBackToSystem(string? stored, string expected)
    {
        Assert.Equal(expected, SettingsInjection.ResolveTheme(stored));
    }

    [Theory]
    [InlineData(1, 72)]
    [InlineData(9999, 600)]
    [InlineData(300, 300)]
    public void ResolvePrintDpi_ClampsToRange(int stored, int expected)
    {
        Assert.Equal(expected, SettingsInjection.ResolvePrintDpi(stored));
    }

    [Theory]
    [InlineData(-1.0, 96.0)]
    [InlineData(0.0, 96.0)]
    [InlineData(10.0, 36.0)]
    [InlineData(144.0, 144.0)]
    [InlineData(5000.0, 600.0)]
    public void ResolveRenderBaseDpi_FallsBackOrClamps(double stored, double expected)
    {
        Assert.Equal(expected, SettingsInjection.ResolveRenderBaseDpi(stored));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData(@"  C:\out  ", @"C:\out")]
    public void ResolveOutputDir_WhitespaceEmptyElseTrimmed(string? stored, string expected)
    {
        Assert.Equal(expected, SettingsInjection.ResolveOutputDir(stored));
    }

    [Fact]
    public void ResolveRenderBaseDpi_FeedsDpiForZoom_WithoutChangingThresholdLadder()
    {
        // 注入更大基准：低于阈值走注入基准；达到阈值不低于 HighDpi。
        Assert.Equal(144.0, PageLayoutCalculator.DpiForZoom(100, 144.0));
        Assert.Equal(150.0, PageLayoutCalculator.DpiForZoom(150, 144.0));
        Assert.Equal(200.0, PageLayoutCalculator.DpiForZoom(150, 200.0));
        // 默认调用（未注入）语义不变——T1 不变量。
        Assert.Equal(PageLayoutCalculator.BaseDpi, PageLayoutCalculator.DpiForZoom(100));
        Assert.Equal(PageLayoutCalculator.HighDpi, PageLayoutCalculator.DpiForZoom(150));
    }

    // ---------------------------------------------------------------- SeedOutputDir

    private sealed class FakeToolVm
    {
        public string OutputDir { get; set; } = "";
    }

    private sealed class NoOutputVm
    {
        public int Value { get; set; }
    }

    [Fact]
    public void SeedOutputDir_EmptyOrWhitespaceSetting_NoOp()
    {
        var vm = new FakeToolVm();

        Assert.False(SettingsInjection.SeedOutputDir(vm, ""));
        Assert.False(SettingsInjection.SeedOutputDir(vm, "   "));
        Assert.False(SettingsInjection.SeedOutputDir(vm, null));
        Assert.Equal("", vm.OutputDir);
    }

    [Fact]
    public void SeedOutputDir_SeedsOnlyWhenEmpty_AndDoesNotOverwrite()
    {
        var vm = new FakeToolVm();

        Assert.True(SettingsInjection.SeedOutputDir(vm, @"C:\out"));
        Assert.Equal(@"C:\out", vm.OutputDir);

        // 已有值：不覆盖（页面/用户选择优先）
        Assert.False(SettingsInjection.SeedOutputDir(vm, @"D:\other"));
        Assert.Equal(@"C:\out", vm.OutputDir);
    }

    [Fact]
    public void SeedOutputDir_TrimsSettingBeforeSeeding()
    {
        var vm = new FakeToolVm();

        Assert.True(SettingsInjection.SeedOutputDir(vm, @"  C:\out  "));
        Assert.Equal(@"C:\out", vm.OutputDir);
    }

    [Fact]
    public void SeedOutputDir_NonApplicableOrNull_NoOp()
    {
        Assert.False(SettingsInjection.SeedOutputDir(new NoOutputVm(), @"C:\out"));
        Assert.False(SettingsInjection.SeedOutputDir(null, @"C:\out"));
    }
}
