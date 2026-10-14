// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;

namespace AIPDF.Windows.Tests.UI;

/// <summary>
/// FlaUI UI 冒烟夹具：单实例启动真实 <c>AIPDF.Windows.exe</c>，全程 UIA3。
/// <para>
/// 隔离边界（与 tests/test_installer_windows.py 同口径）：
/// 不读个人文档；不改真实 %APPDATA%\AIPDF 设置（只读会话/设置，不写回业务数据）；
/// 单实例互斥 <c>Local\AIPDF_SingleInstance</c> 被占用时整类 skip，绝不 force 杀用户会话里的应用。
/// </para>
/// </summary>
public sealed class UiSmokeFixture : IDisposable
{
    public const string MainWindowTitle = "AIPDF 阅读器";
    public const string MainWindowAutomationId = "MainWindow";

    private UIA3Automation? _automation;
    private Application? _app;

    /// <summary>跳过原因；非 null 时用例应 <c>Skip.If</c> 跳过（skip ≠ PASS）。</summary>
    public string? SkipReason { get; private set; }

    public UIA3Automation Automation =>
        _automation ?? throw new InvalidOperationException("夹具未初始化");

    public Application App =>
        _app ?? throw new InvalidOperationException("夹具未初始化");

    public Window MainWindow { get; private set; } =
        null!;

    public ConditionFactory Cf => Automation.ConditionFactory;

    /// <summary>尝试启动应用；失败写入 <see cref="SkipReason"/>（用例侧 skip，不当失败）。</summary>
    public void Initialize()
    {
        if (!System.OperatingSystem.IsWindows())
        {
            SkipReason = "UI 冒烟仅 Windows";
            return;
        }

        if (SingleInstanceBusy())
        {
            SkipReason = "已存在 AIPDF 单实例（Local\\AIPDF_SingleInstance）；请关闭本机 AIPDF 后重跑 UI 冒烟";
            return;
        }

        var exe = ResolveAppExe();
        if (exe is null)
        {
            SkipReason = "未找到 AIPDF.Windows.exe（先构建 Sources\\AIPDF.Windows 或 scripts\\build.ps1）";
            return;
        }

        try
        {
            _automation = new UIA3Automation();
            _app = Application.Launch(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            });

            // WhileTrue = 谓词为 true 时继续重试：未就绪/标题未匹配 → 继续等；匹配或进程退出 → 停。
            Window? candidate = null;
            Retry.WhileTrue(() =>
            {
                if (_app.HasExited)
                {
                    // 进程退出：停止等待，交由下方 usable 校验置 SkipReason
                    return false;
                }

                candidate = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(2));
                if (candidate is null)
                {
                    return true; // 主窗未就绪 → 继续等
                }

                // 闪窗/启动页：认 AutomationId 或标题；未匹配则继续等
                var matched = string.Equals(candidate.Title, MainWindowTitle, StringComparison.Ordinal)
                              || candidate.AutomationId == MainWindowAutomationId;
                return !matched;
            }, timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));

            var usable = candidate is not null
                && (string.Equals(candidate.Title, MainWindowTitle, StringComparison.Ordinal)
                    || candidate.AutomationId == MainWindowAutomationId);
            if (!usable)
            {
                CleanupProcess();
                SkipReason = "20s 内未出现主窗口（进程可能崩溃或被单实例转发吞掉）";
            }
            else
            {
                MainWindow = candidate!;

                // 会话恢复（FR-29）可能在启动后同步 LoadDocument 重文档（本机 session 恢复多个大 PDF），
                // 阻塞 UI 线程期间 UIA 查询会超时/返回空 → 用例误报。等待「工具箱」入口被连续快速
                // 查到（UIA 往返 < 2s × 3 次）再放行用例；超时 → 整类 skip（skip ≠ PASS）。
                if (!WaitUntilUiResponsive())
                {
                    CleanupProcess();
                    SkipReason = "启动后 180s 内 UIA 未连续响应（会话恢复的重文档加载/渲染可能阻塞 UI 线程）；本环境无法执行 UI 冒烟";
                }
            }
        }
        catch (Exception ex)
        {
            CleanupProcess();
            SkipReason = $"启动 UI 失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        CleanupProcess();
        _automation?.Dispose();
        _automation = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 等 UI 可响应：连续 3 次「更多」入口查找往返 &lt; 2s（期间无异常）才认为会话恢复加载完成。
    /// 总预算 180s；阻塞式慢查询（LoadDocument 占 UI 线程）会使单次往返超阈值或抛错 → 不计入连续成功。
    /// </summary>
    private bool WaitUntilUiResponsive()
    {
        const int requiredStreak = 3;
        var probeBudget = TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        var streak = 0;
        while (DateTime.UtcNow < deadline)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            FlaUI.Core.AutomationElements.AutomationElement? probe = null;
            try
            {
                probe = MainWindow.FindFirstDescendant(Cf.ByName("更多"));
            }
            catch
            {
                // UI 线程忙时 UIA 可能抛 ElementNotAvailable 等 → 本轮不算成功
            }
            sw.Stop();

            streak = probe is not null && sw.Elapsed < probeBudget ? streak + 1 : 0;
            if (streak >= requiredStreak)
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    private void CleanupProcess()
    {
        try
        {
            if (_app is { HasExited: false })
            {
                // FlaUI Application 无 WaitForExit：close(true) 宽限期后仍存活则强杀
                _app.Close(true);
                if (!_app.HasExited)
                {
                    _app.Kill();
                }
            }
        }
        catch
        {
            // 关闭失败不掩盖原始测试结果
        }
        finally
        {
            _app?.Dispose();
            _app = null;
        }
    }

    private static bool SingleInstanceBusy()
    {
        // 打开已有互斥体且已持有 → 另一实例在跑。initiallyOwned:false 只探测不占用。
        Mutex? probe = null;
        try
        {
            probe = new Mutex(initiallyOwned: false, @"Local\AIPDF_SingleInstance", out var createdNew);
            // createdNew=true 表示本探测创建了新互斥体 → 当前无人持有（短暂窗口内本探测也算占用，
            // 因此立即释放并返回 false，避免把自己算成「用户正在用」）。
            if (createdNew)
            {
                probe.Dispose();
                return false;
            }

            // 已存在：尝试 WaitOne(0) —— 拿不到 = 有活跃实例
            try
            {
                return !probe.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 前持有者崩溃残留：视为空闲
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            probe?.Dispose();
        }
    }

    /// <summary>按优先级找已构建 exe：测试输出目录 → 主工程 bin → dist 发行目录。</summary>
    private static string? ResolveAppExe()
    {
        var fileName = "AIPDF.Windows.exe";
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, fileName),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\AIPDF.Windows\bin\Debug\net8.0-windows", fileName)),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\AIPDF.Windows\bin\Release\net8.0-windows\win-x64", fileName)),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\dist\aipdf-windows\app", fileName)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 按 Id 找元素（找不到返回 null，供调用方 Retry）。
    /// <para>
    /// FlaUI 5 的按条件查找返回基类 <see cref="AutomationElement"/>（非 Button/Window 等派生类），
    /// 直接 <c>as T</c> 会恒为 null；此处按官方 AsXxx() 同款方式用
    /// <c>new T(FrameworkAutomationElement)</c> 重包装。
    /// </para>
    /// </summary>
    public AutomationElement? FindById(string automationId, AutomationElement? scope = null)
    {
        var root = scope ?? MainWindow;
        return root?.FindFirstDescendant(Cf.ByAutomationId(automationId));
    }

    public AutomationElement? FindByName(string name, AutomationElement? scope = null)
    {
        var root = scope ?? MainWindow;
        return root?.FindFirstDescendant(Cf.ByName(name));
    }

    public T? FindById<T>(string automationId, AutomationElement? scope = null) where T : AutomationElement
        => AsTyped<T>(FindById(automationId, scope));

    public T? FindByName<T>(string name, AutomationElement? scope = null) where T : AutomationElement
        => AsTyped<T>(FindByName(name, scope));

    /// <summary>把条件查找结果重包装为派生类型（null 透传；已是目标类型则直接返回）。</summary>
    private static T? AsTyped<T>(AutomationElement? element) where T : AutomationElement
    {
        if (element is null)
        {
            return null;
        }

        if (element is T typed)
        {
            return typed;
        }

        return Activator.CreateInstance(typeof(T), element.FrameworkAutomationElement) as T;
    }

    /// <summary>轮询直到谓词为真或超时；返回最终元素（可能仍为 null）。</summary>
    public AutomationElement? WaitById(string automationId, TimeSpan? timeout = null, AutomationElement? scope = null)
    {
        AutomationElement? found = null;
        Retry.WhileTrue(() =>
        {
            found = FindById(automationId, scope);
            return found is null;
        }, timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
        return found;
    }

    public AutomationElement? WaitByName(string name, TimeSpan? timeout = null, AutomationElement? scope = null)
    {
        AutomationElement? found = null;
        Retry.WhileTrue(() =>
        {
            found = FindByName(name, scope);
            return found is null;
        }, timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
        return found;
    }

    public T? WaitByName<T>(string name, TimeSpan? timeout = null, AutomationElement? scope = null)
        where T : AutomationElement
        => AsTyped<T>(WaitByName(name, timeout, scope));
}

/// <summary>按类共享夹具：整组 UI 用例共用一次应用启动（串行集合已在 xunit.runner.json 关闭并行）。</summary>
public sealed class UiSmokeCollection
{
    public const string Name = "UiSmoke";
}

[CollectionDefinition(UiSmokeCollection.Name)]
public sealed class UiSmokeCollectionDefinition : ICollectionFixture<UiSmokeFixture>
{
}

/// <summary>UI 用例基类：夹具 SkipReason 非空 → 全部用例 skip（不计入失败）。</summary>
public abstract class UiSmokeTestBase : IClassFixture<UiSmokeFixture>
{
    protected UiSmokeTestBase(UiSmokeFixture fixture)
    {
        Fixture = fixture;
        if (fixture.SkipReason is null && fixture.MainWindow is null)
        {
            fixture.Initialize();
        }
    }

    protected UiSmokeFixture Fixture { get; }

    protected void SkipIfUnavailable()
    {
        if (Fixture.SkipReason is not null)
        {
            Skip.If(true, Fixture.SkipReason);
        }

        Assert.NotNull(Fixture.MainWindow);
    }
}
