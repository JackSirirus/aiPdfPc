// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T10 / C2 夜间阅读模式：主题解析 + 运行时 token 注入。
//   - 解析设置 `Theme`（light/dark/system，裁决 Q6）：纯函数 `ResolveMode` 可单测；
//   - 注入 `Application.Resources` 中同名 `Brush.*` 键（亮色取启动快照 / 暗色取 `Brush.Dark.*`），
//     所有主题引用为 DynamicResource，故运行时切换无需重启；
//   - system 态订阅系统主题变化（SystemParameters.StaticPropertyChanged + SystemEvents.UserPreferenceChanged），
//     去抖 300ms 合并抖动、Dispose 时反订阅（无事件泄漏）；
//   - 资源变更按 F3-BLK-1 教训封送到 UI 线程；不使用无界 await，避免 082c7fd 的 dispatcher 死锁。
// 本文件不含任何密码字段（PRD 4.3.3）。
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AIPDF.Windows.Services;

/// <summary>解析后的实际外观（system 态在解析时折叠为亮/暗之一）。</summary>
public enum ThemeMode
{
    /// <summary>亮色（默认）。</summary>
    Light,

    /// <summary>暗色。</summary>
    Dark,
}

/// <summary>
/// 主题 token 名清单：`Brush.&lt;name&gt;`（亮色）/ `Brush.Dark.&lt;name&gt;`（暗色）一一对应。
/// App.xaml 是语义与色值的唯一来源；新增 token 必须同步亮/暗两份（由 token 完整性单测锁定）。
/// </summary>
public static class ThemeTokens
{
    /// <summary>全部主题 token 名（不含 `Brush.` 前缀）。顺序与 App.xaml 的 token 定义一致。</summary>
    public static readonly string[] All =
    [
        "Window", "Sidebar", "Hover", "Border", "BorderStrong",
        "TextPrimary", "TextSecondary", "TextTertiary",
        "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentText",
        "Error", "FieldBackground", "Surface",
        "SearchHighlight", "SearchHighlightBorder",
        "Success", "SuccessSoft", "Danger", "DangerSoft", "NeutralSoft",
        "ConverterWarnBackground", "ConverterWarnText",
        "PreviewBg", "Toolbar",
        "DropZoneBackground", "DropZoneHoverBackground",
        // A+C Phase 4：模态/锁定遮罩叠层 + 强调底上的前景色（亮/暗成对，见设计说明 §6）
        "OverlayMask", "OnAccent",
    ];

    /// <summary>亮色键前缀。</summary>
    public const string LightPrefix = "Brush.";

    /// <summary>暗色键前缀（暗色集只由 ThemeManager 读取，XAML 不直接引用）。</summary>
    public const string DarkPrefix = "Brush.Dark.";

    /// <summary>亮色资源键。</summary>
    public static string LightKey(string name) => LightPrefix + name;

    /// <summary>暗色资源键。</summary>
    public static string DarkKey(string name) => DarkPrefix + name;
}

/// <summary>
/// 主题管理器：把设置/系统主题解析为亮/暗，并把选定 token 集注入 <see cref="Application.Resources"/>。
/// <para>
/// system 态下订阅系统主题变化；事件去抖 300ms 后重新解析并应用。所有资源写入封送到 UI 线程。
/// 实例须在窗口关闭时 <see cref="Dispose"/>，以反订阅静态事件（无事件泄漏）。
/// </para>
/// </summary>
public sealed class ThemeManager : IDisposable
{
    /// <summary>系统主题变化去抖间隔（合并一次切换的多条系统通知）。</summary>
    private static readonly TimeSpan SystemChangeDebounce = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _store;
    private readonly Dispatcher? _dispatcher;

    /// <summary>启动时的亮色 token 快照：切回亮色时还原（暗色集只增不减，不能靠再次读 `Brush.*` 还原）。</summary>
    private readonly Dictionary<string, Brush> _lightSnapshot = new(StringComparer.Ordinal);

    private readonly DispatcherTimer? _debounceTimer;
    private readonly PropertyChangedEventHandler _systemParametersHandler;
    private readonly UserPreferenceChangedEventHandler _userPreferenceHandler;

    /// <summary>当前设置值（light/dark/system），供系统事件回调判断是否仍需跟随。</summary>
    private volatile string _currentSetting = AppSettings.ThemeSystem;

    private bool _listening;
    private bool _disposed;

    /// <summary>最近一次实际应用的解析结果。</summary>
    public ThemeMode CurrentMode { get; private set; } = ThemeMode.Light;

    public ThemeManager(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _systemParametersHandler = OnSystemParametersChanged;
        _userPreferenceHandler = OnUserPreferenceChanged;

        var dispatcher = Application.Current?.Dispatcher;
        _dispatcher = dispatcher is not null && dispatcher.Thread.IsAlive && !dispatcher.HasShutdownStarted
            ? dispatcher
            : null;

        CaptureLightSnapshot();

        if (_dispatcher is not null)
        {
            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = SystemChangeDebounce,
            };
            _debounceTimer.Tick += OnDebounceTick;
        }
    }

    // ------------------------------------------------------------------ 纯解析（可单测）

    /// <summary>
    /// 主题解析（纯函数）：light/dark 直接映射；system（含 null/未知/空白）按系统偏好折叠；
    /// 未知值经 <see cref="SettingsStore.NormalizeTheme"/> 归一化为 system 后再折叠。
    /// </summary>
    public static ThemeMode ResolveMode(string? settingTheme, bool systemPrefersDark)
    {
        var normalized = SettingsStore.NormalizeTheme(settingTheme);
        if (string.Equals(normalized, AppSettings.ThemeDark, StringComparison.Ordinal))
        {
            return ThemeMode.Dark;
        }

        if (string.Equals(normalized, AppSettings.ThemeLight, StringComparison.Ordinal))
        {
            return ThemeMode.Light;
        }

        return systemPrefersDark ? ThemeMode.Dark : ThemeMode.Light;
    }

    /// <summary>
    /// 读取系统偏好：Windows「应用使用浅色主题」= 0 视为暗色。注册表不可读时回退亮色（默认）。
    /// 不抛异常、不提示。
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读取系统偏好失败 → 回退亮色，不影响启动
        }

        return false;
    }

    // ------------------------------------------------------------------ 应用

    /// <summary>按持久化设置（<see cref="SettingsStore.Theme"/>）即时应用主题。</summary>
    public void ApplyFromSettings() => Apply(_store.Load().Theme);

    /// <summary>按指定设置值即时应用主题；同时按需启停系统主题订阅。</summary>
    public void Apply(string? settingTheme)
    {
        if (_disposed)
        {
            return;
        }

        var normalized = SettingsStore.NormalizeTheme(settingTheme);
        _currentSetting = normalized;
        ApplyMode(ResolveMode(normalized, IsSystemDark()));
        UpdateSystemSubscription(normalized);
    }

    private void ApplyMode(ThemeMode mode)
    {
        CurrentMode = mode;

        var application = Application.Current;
        if (application?.Resources is not { } resources)
        {
            return;
        }

        var dispatcher = _dispatcher ?? application.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // F3-BLK-1：资源变更封送到 UI 线程；Dispatcher 已关闭时放弃本次（下次事件/切换会重算）。
            if (!dispatcher.Thread.IsAlive || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                dispatcher.Invoke(() => ApplyTo(resources, mode));
            }
            catch (InvalidOperationException)
            {
                // 检查与 Invoke 之间 Dispatcher 关闭：放弃本次，不阻塞、不抛。
            }

            return;
        }

        ApplyTo(resources, mode);
    }

    /// <summary>把选定 token 集写入应用资源（必须在 UI 线程）。缺失资源跳过，绝不抛。</summary>
    private void ApplyTo(ResourceDictionary resources, ThemeMode mode)
    {
        foreach (var name in ThemeTokens.All)
        {
            Brush? brush;
            if (mode == ThemeMode.Dark)
            {
                brush = resources[ThemeTokens.DarkKey(name)] as Brush;
            }
            else
            {
                brush = _lightSnapshot.TryGetValue(name, out var light) ? light : null;
            }

            if (brush is not null)
            {
                resources[ThemeTokens.LightKey(name)] = brush;
            }
        }

        CurrentMode = mode;
    }

    private void CaptureLightSnapshot()
    {
        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        foreach (var name in ThemeTokens.All)
        {
            if (resources[ThemeTokens.LightKey(name)] is Brush brush)
            {
                _lightSnapshot[name] = brush;
            }
        }
    }

    // ------------------------------------------------------------------ 系统主题跟随（去抖 / 无泄漏）

    private void UpdateSystemSubscription(string normalized)
    {
        var shouldListen = string.Equals(normalized, AppSettings.ThemeSystem, StringComparison.Ordinal);
        if (shouldListen == _listening)
        {
            return;
        }

        if (shouldListen)
        {
            SystemParameters.StaticPropertyChanged += _systemParametersHandler;
            SystemEvents.UserPreferenceChanged += _userPreferenceHandler;
            _listening = true;
        }
        else
        {
            SystemParameters.StaticPropertyChanged -= _systemParametersHandler;
            SystemEvents.UserPreferenceChanged -= _userPreferenceHandler;
            _listening = false;
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e) => ScheduleReapply();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => ScheduleReapply();

    /// <summary>去抖：重启计时器把 300ms 内的多条系统通知合并为一次重算。</summary>
    private void ScheduleReapply()
    {
        if (_disposed || !_listening)
        {
            return;
        }

        if (_debounceTimer is null)
        {
            // 无 Dispatcher（如单测环境）：直接重算，保持有界。
            Reapply();
            return;
        }

        if (_dispatcher!.CheckAccess())
        {
            RestartDebounce();
            return;
        }

        if (!_dispatcher.Thread.IsAlive || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.Invoke(RestartDebounce);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 关闭竞态：放弃本次，后续事件会重算。
        }
    }

    private void RestartDebounce()
    {
        _debounceTimer!.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();
        Reapply();
    }

    private void Reapply()
    {
        if (_disposed || !_listening)
        {
            return;
        }

        if (!string.Equals(_currentSetting, AppSettings.ThemeSystem, StringComparison.Ordinal))
        {
            return;
        }

        ApplyMode(ResolveMode(AppSettings.ThemeSystem, IsSystemDark()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_listening)
        {
            SystemParameters.StaticPropertyChanged -= _systemParametersHandler;
            SystemEvents.UserPreferenceChanged -= _userPreferenceHandler;
            _listening = false;
        }

        if (_debounceTimer is not null)
        {
            _debounceTimer.Stop();
            _debounceTimer.Tick -= OnDebounceTick;
        }
    }
}
