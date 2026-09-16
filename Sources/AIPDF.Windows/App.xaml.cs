// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using AIPDF.Windows.Services;
using AIPDF.Windows.Views;

namespace AIPDF.Windows;

/// <summary>
/// 应用入口：单实例互斥 + 命名管道路径转发 + 启动参数解析（FR-32 / PRD 4.7.1）。
/// <para>
/// <c>StartupUri</c> 已移除：主窗口在 <see cref="OnStartup"/> 内按单实例结果显式创建，
/// 第二实例可在任何窗口出现前退出，避免「闪一下第二窗口」。
/// </para>
/// <para>
/// 启动时序（防竞态，H-7）：首实例先建 pipe server 再建 MainWindow；第二实例对 Connect 做
/// 有界重试（<see cref="BoundedRetryPolicy"/>：10 次、指数退避封顶 2s）；仅当转发失败且重试
/// 耗尽后互斥体仍未持有，才允许本实例真正启动（绝不永久无法启动）。
/// 转发可能先于主窗口到达 → 先缓冲，窗口/TabHost 就绪后在 UI 线程派发，不丢路径。
/// 唤起（<see cref="WindowActivation"/>）失败只走回退，绝不新开实例。
/// </para>
/// </summary>
public partial class App : Application
{
    private SingleInstanceLock? _lock;
    private SingleInstancePipeServer? _pipeServer;
    private MainWindow? _mainWindow;
    private string? _pipeName;
    private bool _mainWindowReady;
    private readonly List<string> _pendingPaths = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // F24：启动即清扫上次硬崩溃残留在 %TEMP%\AIPDF-Sidecar 的划词 sidecar / 半成品 temp 文件。
        // 源目录旁的 sidecar 故意不清扫（任意用户目录），且新会话绝不自动加载，残留不影响功能与隐私。
        SelectionSidecarStore.SweepStaleTempFiles();

        var startupPaths = StartupPathParser.Collect(e.Args, File.Exists);
        _pipeName = SingleInstanceNames.CurrentUserPipeName();

        // 互斥体不可用（极端权限/命名冲突）时按独立实例启动，绝不因此卡死启动。
        try
        {
            _lock = new SingleInstanceLock(SingleInstanceNames.MutexName);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[App] 单实例互斥体不可用，按独立实例启动: {ex.Message}");
            _lock = null;
        }

        if (_lock is { IsFirstInstance: false })
        {
            // 已有实例：转发路径（空列表也发空帧 = 激活信号），成功即退出（不产生第二窗口）。
            if (TryForward(startupPaths))
            {
                _lock.Dispose();
                _lock = null;
                Shutdown();
                return;
            }

            // 重试耗尽再试一次互斥体：对方可能已退出，本实例接管。
            if (!_lock.TryAcquireOnce())
            {
                // 互斥体仍未持有 → 允许本实例启动；但管道名可能仍被占用，本实例不再做服务端。
                _lock.Dispose();
                _lock = null;
            }
        }

        if (_lock is { IsFirstInstance: true })
        {
            try
            {
                _pipeServer = new SingleInstancePipeServer(_pipeName!, OnForwardedPaths);
                _pipeServer.Start(); // 先于 MainWindow：第二实例连接时已有服务端可等
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[App] 单实例管道服务端启动失败（转发不可用，不影响本实例启动）: {ex.Message}");
                _pipeServer = null;
            }
        }

        // T8：本实例将创建主窗口 —— 先把 v1.1 按显示名寻址的翻译凭据 / 同意迁移到稳定身份键
        // （幂等；凭据管理器不可用时跳过，不阻塞启动 —— 后续读取会给出「凭据不可用」的既有提示）。
        // 放在第二实例转发出局之后：第二实例不做无谓迁移，也避免重复实例并发改写。
        MigrateTranslationIdentity();

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        // 主窗口/TabHost 就绪：派发启动参数；此前缓冲的转发路径一并派发（不丢路径）。
        _mainWindowReady = true;
        if (startupPaths.Count > 0)
        {
            _mainWindow.OpenExternalDocuments(startupPaths);
        }

        DrainPendingPaths();
    }

    /// <summary>
    /// T8 一次性身份迁移：凭据 target / 同意记录 → 稳定供应商 id。失败（凭据管理器不可用 / 设置不可写）
    /// 只记 Trace 不抛出：迁移是尽力而为，跳过不影响其余功能，也不会丢失任何密钥（旧 target 原样保留）。
    /// </summary>
    private static void MigrateTranslationIdentity()
    {
        try
        {
            var settings = new SettingsStore();
            var credentials = new TranslationCredentialStore();
            new TranslationIdentityMigrator(settings, credentials).Migrate();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[App] 翻译身份迁移跳过: {ex.Message}");
        }
    }

    /// <summary>
    /// 第二实例同步转发：有界重试（10 次、指数退避封顶 2s）。WPF <see cref="OnStartup"/> 在 UI 线程，
    /// 此处在退出/接管二选一前同步等待转发结果。客户端全链路 ConfigureAwait(false)，不会与 UI 线程互锁。
    /// </summary>
    private bool TryForward(IReadOnlyList<string> paths)
    {
        try
        {
            return SingleInstancePipeClient
                .SendWithRetryAsync(_pipeName!, paths, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>服务端收到消息（后台线程）：marshal 到 UI 线程；窗口未就绪则缓冲，就绪后派发。</summary>
    private void OnForwardedPaths(IReadOnlyList<string> paths)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!_mainWindowReady || _mainWindow is null)
                {
                    _pendingPaths.AddRange(paths);
                    return;
                }

                WindowActivation.TryActivate(_mainWindow);
                _mainWindow.OpenExternalDocuments(paths);
            }
            catch (Exception ex)
            {
                // 关闭中/窗口已释放等竞态：丢弃本次转发，绝不因此崩溃或再开实例
                Trace.WriteLine($"[App] 处理转发路径失败: {ex.Message}");
            }
        }));
    }

    /// <summary>窗口就绪后派发缓冲的转发路径（FR-32 窗口未就绪缓冲，防路径丢失）。</summary>
    private void DrainPendingPaths()
    {
        if (_mainWindow is null || _pendingPaths.Count == 0)
        {
            return;
        }

        var buffered = _pendingPaths.ToArray();
        _pendingPaths.Clear();
        WindowActivation.TryActivate(_mainWindow);
        _mainWindow.OpenExternalDocuments(buffered);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        _pipeServer = null;
        _lock?.Dispose();
        _lock = null;
        base.OnExit(e);
    }
}
