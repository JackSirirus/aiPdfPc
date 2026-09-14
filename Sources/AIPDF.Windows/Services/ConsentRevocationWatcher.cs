// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端同意撤回的运行期监视（F21）：
//   运行中的云端翻译任务在「同意被撤销」（TranslationConsentService.Generation 变化）时
//   立即取消运行令牌，停止继续向已撤回同意的服务商发送请求——而不是让撤销只对后续任务生效。
//
// 复用既有取消机器：取消令牌 → EngineBridge 终止子进程树，不采用半成品。
// 仅比较代次整数，不读取 / 不落盘任何密钥或正文。
namespace AIPDF.Windows.Services;

/// <summary>
/// 翻译任务运行期间的同意代次监视：代次变化即调用回调并取消运行令牌。监视是尽力而为的
/// 后台循环，在令牌取消 / 释放时安静退出，不产生未观测异常。调用方在任务结束时应
/// <c>await</c> <see cref="Task"/>，确保循环已停止。
/// </summary>
internal sealed class ConsentRevocationWatcher
{
    /// <summary>默认轮询间隔：足够快以在数秒内停止在途任务，又不造成可感知开销。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private ConsentRevocationWatcher(Task task) => Task = task;

    /// <summary>监视循环的完成任务。</summary>
    public Task Task { get; }

    /// <summary>
    /// 启动监视。<paramref name="generation"/> 读取当前同意代次；一旦与
    /// <paramref name="startGeneration"/> 不同，即调用 <paramref name="onRevoked"/> 并取消
    /// <paramref name="runToken"/>。取消 / 释放导致的异常被吞掉（监视是尽力而为）。
    /// </summary>
    public static ConsentRevocationWatcher Start(
        Func<int> generation,
        int startGeneration,
        CancellationTokenSource runToken,
        Action? onRevoked = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(runToken);
        var period = interval ?? DefaultInterval;

        async Task WatchAsync()
        {
            try
            {
                while (!runToken.IsCancellationRequested)
                {
                    await Task.Delay(period, runToken.Token).ConfigureAwait(false);
                    if (generation() != startGeneration)
                    {
                        onRevoked?.Invoke();
                        runToken.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 任务正常结束 / 用户取消：监视退出。
            }
            catch (ObjectDisposedException)
            {
                // 令牌已释放：监视退出。
            }
        }

        return new ConsentRevocationWatcher(WatchAsync());
    }
}
