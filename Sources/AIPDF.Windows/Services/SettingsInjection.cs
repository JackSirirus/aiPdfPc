// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T5 设置注入决策（纯逻辑，无 UI / 无 WPF 依赖）：
//   把 T1 的 7 项设置换算为「新建/新打开文档时的初始值」，供宿主 MainWindow / ReaderViewModel 消费。
//   决策口径（FR-38 边界：仅新打开文档生效；已打开文档不受设置变更影响）：
//     - DefaultZoom：reading-state 命中则用其记录（SoT），否则用设置默认（数值档 / 适配档）；
//     - DefaultPanelKey：reading-state 命中则用其面板（含 search），否则用设置默认（不含 search）；
//     - RecentFilesLimit / Theme / PrintDpi / DefaultRenderDpi / DefaultOutputDir：委托 SettingsStore 归一化；
//     - DefaultOutputDir 仅作工具 VM 的 OutputDir 初始种子（空 = 现状），经 SeedOutputDir 反射写入
//       （不改动 Tools/* 各异的 VM 基类——T8 域，此为本任务最小的宿主侧注入点）。
// 本文件不含任何密码字段（PRD 4.3.3）。
using System.Globalization;
using System.Reflection;

namespace AIPDF.Windows.Services;

/// <summary>初始缩放模式（数值档 / 适配宽度 / 适配页面）。</summary>
public enum InitialZoomMode
{
    /// <summary>九档阶梯的数值百分比。</summary>
    Percent,

    /// <summary>适配宽度（需视口测量后套用）。</summary>
    FitWidth,

    /// <summary>适配页面（需视口测量后套用）。</summary>
    FitPage,
}

/// <summary>
/// 初始缩放决策：<see cref="Mode"/> 为 <see cref="InitialZoomMode.Percent"/> 时用 <see cref="Percent"/>；
/// 适配档时由宿主排队到视口测量后套用（打开瞬间视口宽高可能尚为 0）。
/// </summary>
public readonly record struct InitialZoomDecision(InitialZoomMode Mode, double Percent)
{
    /// <summary>数值档决策。</summary>
    public static InitialZoomDecision ForPercent(double percent) => new(InitialZoomMode.Percent, percent);

    /// <summary>适配宽度。</summary>
    public static readonly InitialZoomDecision FitWidth = new(InitialZoomMode.FitWidth, 0);

    /// <summary>适配页面。</summary>
    public static readonly InitialZoomDecision FitPage = new(InitialZoomMode.FitPage, 0);
}

/// <summary>
/// 设置 → 初始值/注入的纯决策逻辑（xunit 直接覆盖）。读取侧只依赖 <see cref="SettingsStore"/> 的归一化，
/// 不触碰 UI；宿主负责把结果套用到 ReaderViewModel / 工具页 VM。
/// </summary>
public static class SettingsInjection
{
    /// <summary>合法左栏面板键（reading-state 允许 search；设置默认不允许——search 由 Ctrl+F 激活）。</summary>
    private static readonly HashSet<string> StatePanelKeys =
        new(StringComparer.Ordinal) { "thumbs", "outline", "bookmarks", "search" };

    /// <summary>
    /// 初始缩放（FR-38 项 1）：有 reading-state（且记录有效）→ 用其缩放；否则用设置默认。
    /// 设置默认经 <see cref="SettingsStore.NormalizeZoom"/> 归一化（未知档回退 100）。
    /// </summary>
    public static InitialZoomDecision ResolveInitialZoom(bool hasReadingState, double readingStateZoom, string? settingZoom)
    {
        if (hasReadingState && readingStateZoom > 0)
        {
            return InitialZoomDecision.ForPercent(readingStateZoom);
        }

        var normalized = SettingsStore.NormalizeZoom(settingZoom);
        if (string.Equals(normalized, AppSettings.ZoomFitWidth, StringComparison.Ordinal))
        {
            return InitialZoomDecision.FitWidth;
        }

        if (string.Equals(normalized, AppSettings.ZoomFitPage, StringComparison.Ordinal))
        {
            return InitialZoomDecision.FitPage;
        }

        return InitialZoomDecision.ForPercent(double.Parse(normalized, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 初始左栏面板（FR-38 项 2 / BA-6）：有 reading-state（键合法，含 search）→ 用其面板；
    /// 否则用设置默认（经归一化，仅 thumbs/outline/bookmarks）。
    /// </summary>
    public static string ResolvePanelKey(bool hasReadingState, string? readingStatePanelKey, string? settingPanelKey)
        => hasReadingState && readingStatePanelKey is not null && StatePanelKeys.Contains(readingStatePanelKey)
            ? readingStatePanelKey
            : ResolveDefaultPanelKey(settingPanelKey);

    /// <summary>设置默认面板键归一化（不含 search，未知 → thumbs）。</summary>
    public static string ResolveDefaultPanelKey(string? settingPanelKey)
        => SettingsStore.NormalizePanelKey(settingPanelKey);

    /// <summary>最近文件上限（FR-38 项 3）：钳制到 [10,50]。</summary>
    public static int ResolveRecentFilesLimit(int settingLimit)
        => SettingsStore.NormalizeRecentFilesLimit(settingLimit);

    /// <summary>主题（Q6/C2）：仅 light/dark/system，未知回退 system（视觉切换由 T10 消费）。</summary>
    public static string ResolveTheme(string? settingTheme)
        => SettingsStore.NormalizeTheme(settingTheme);

    /// <summary>打印 DPI（Q6/Q11）：钳制到 [72,600]；打印路径（PrintService）读取。</summary>
    public static int ResolvePrintDpi(int settingDpi)
        => SettingsStore.NormalizePrintDpi(settingDpi);

    /// <summary>渲染基准 DPI（Q6）：非正回退 96、正数钳制到 [36,600]；仅覆盖渲染基准，不改阈值阶梯。</summary>
    public static double ResolveRenderBaseDpi(double settingDpi)
        => SettingsStore.NormalizeRenderDpi(settingDpi);

    /// <summary>默认输出目录（Q6）：空/空白 → 空串（= 现状）；否则去首尾空白。</summary>
    public static string ResolveOutputDir(string? settingDir)
        => SettingsStore.NormalizeOutputDir(settingDir);

    /// <summary>
    /// 把设置 <c>DefaultOutputDir</c> 作为工具页 VM 的 <c>OutputDir</c> 初始种子（仅当当前为空）。
    /// <para>
    /// 采用反射是有意为之：各工具组 VM 基类分散在 5 个 Tools/* 文件（T8 域），本任务只被允许最小注入，
    /// 故不在基类上新增接口/抽象。属性名为 CommunityToolkit <c>[ObservableProperty]</c> 生成的
    /// <c>OutputDir</c>（public string，可写）；找不到 / 类型不符 / 已有非空值 → 不动作（空 = 现状语义）。
    /// 不改变引擎「输出任务目录 + 不覆盖源文件」语义，页面 <c>OpenFolderDialog</c> 仍可覆盖。
    /// </para>
    /// </summary>
    /// <returns>实际写入返回 true；空设置 / 不适用 / 已有值返回 false。</returns>
    public static bool SeedOutputDir(object? viewModel, string? defaultOutputDir)
    {
        if (viewModel is null) return false;
        var directory = ResolveOutputDir(defaultOutputDir);
        if (directory.Length == 0) return false;

        var property = viewModel.GetType().GetProperty(
            "OutputDir", BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(string) || !property.CanWrite)
        {
            return false;
        }

        if (property.GetValue(viewModel) is string current && current.Length > 0)
        {
            return false; // 页面/用户已设置：不覆盖
        }

        property.SetValue(viewModel, directory);
        return true;
    }
}
