// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T7 全局串行任务队列测试（FR-40/41/42）：FIFO、排队取消、失败继续、去重、运行中取消、状态栏摘要。
// 每个用例使用独立 ToolTaskQueue 实例（不触 Shared 单例）避免跨用例干扰。
using System.Collections.Concurrent;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class ToolTaskQueueTests
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Submit_RunsFifo_OneAtATime()
    {
        var queue = new ToolTaskQueue();
        var order = new ConcurrentQueue<string>();
        var gateA = Gate();

        var a = queue.Submit("A", async _ => { order.Enqueue("A"); await gateA.Task; order.Enqueue("A-done"); });
        var b = queue.Submit("B", _ => { order.Enqueue("B"); return Task.CompletedTask; });

        // A 执行中、B 排队（一次只跑一个）
        Assert.Equal(ToolTaskState.Running, a.State);
        Assert.Equal(ToolTaskState.Queued, b.State);
        Assert.Equal(1, b.Position);
        Assert.DoesNotContain("B", order);

        gateA.SetResult();
        await a.Completion.Task;
        await b.Completion.Task;

        Assert.Equal(["A", "A-done", "B"], order.ToArray());
        Assert.Equal(ToolTaskState.Completed, a.State);
        Assert.Equal(ToolTaskState.Completed, b.State);
    }

    [Fact]
    public async Task Cancel_QueuedItem_RemovesWithoutRunning()
    {
        var queue = new ToolTaskQueue();
        var gateA = Gate();
        var ranB = false;

        var a = queue.Submit("A", async _ => await gateA.Task);
        var b = queue.Submit("B", _ => { ranB = true; return Task.CompletedTask; });

        Assert.True(queue.Cancel(b));
        Assert.Equal(ToolTaskState.Cancelled, b.State);
        await b.Completion.Task; // 取消的排队项直接完成，等待方不悬挂
        Assert.False(ranB);
        Assert.Empty(queue.Pending);

        gateA.SetResult();
        await a.Completion.Task;
        Assert.Equal(ToolTaskState.Completed, a.State);
    }

    [Fact]
    public async Task Failure_DoesNotStopQueue_NextRuns()
    {
        var queue = new ToolTaskQueue();
        var ranB = false;

        var a = queue.Submit("A", _ => throw new InvalidOperationException("boom"));
        var b = queue.Submit("B", _ => { ranB = true; return Task.CompletedTask; });

        await a.Completion.Task;
        await b.Completion.Task;

        Assert.Equal(ToolTaskState.Failed, a.State);
        Assert.Equal("boom", a.Error);
        Assert.True(ranB);
        Assert.Equal(ToolTaskState.Completed, b.State);
    }

    [Fact]
    public async Task Submit_SameDedupKey_ReturnsExisting_NoDuplicate()
    {
        var queue = new ToolTaskQueue();
        var gateA = Gate();
        var ranSecond = false;

        var a = queue.Submit("A", async _ => await gateA.Task, dedupKey: this);
        var b = queue.Submit("A-again", _ => { ranSecond = true; return Task.CompletedTask; }, dedupKey: this);

        Assert.Same(a, b); // 命中去重键 → 返回运行中的同一项，不重复执行
        Assert.False(ranSecond);
        Assert.Empty(queue.Pending); // A 在运行、未新增排队项

        gateA.SetResult();
        await a.Completion.Task;
    }

    [Fact]
    public async Task Cancel_RunningItem_FlipsToCancelled_AndRaisesEvent()
    {
        var queue = new ToolTaskQueue();
        var started = Gate();
        var cancelledTcs = new TaskCompletionSource<ToolTaskItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.RunningTaskCancelled += (_, item) => cancelledTcs.TrySetResult(item);

        var a = queue.Submit("A", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });

        await started.Task;
        Assert.True(queue.Cancel(a));
        var cancelled = await cancelledTcs.Task; // 等事件真正触发（事件在 Completion 之后）
        await a.Completion.Task;

        Assert.Equal(ToolTaskState.Cancelled, a.State);
        Assert.Same(a, cancelled); // 宿主据此弹「任务已取消」Toast
        Assert.False(queue.HasRunning);
    }

    [Fact]
    public async Task StatusText_ShowsRunningAndFirstThreeQueued()
    {
        var queue = new ToolTaskQueue();
        var gateA = Gate();

        var a = queue.Submit("压缩", async _ => await gateA.Task);
        _ = queue.Submit("拆分", _ => Task.CompletedTask);
        _ = queue.Submit("旋转", _ => Task.CompletedTask);
        _ = queue.Submit("转 Word", _ => Task.CompletedTask);
        _ = queue.Submit("更多", _ => Task.CompletedTask);

        Assert.Contains("正在执行：压缩", queue.StatusText);
        Assert.Contains("第 1 位 拆分", queue.StatusText);
        Assert.Contains("第 3 位 转 Word", queue.StatusText);
        Assert.Contains("还有 1 个", queue.StatusText); // 第 4 位（更多）折叠为「…还有 1 个」

        gateA.SetResult();
        await a.Completion.Task;
    }

    [Fact]
    public void CancellingUnknownItem_ReturnsFalse()
    {
        var queue = new ToolTaskQueue();
        var stray = queue.Create("x", _ => Task.CompletedTask);
        Assert.False(queue.Cancel(stray)); // 未入队：不命中
    }

    // ---------------------------------------------------------------- V2 T10 FR-21 启动前门禁

    [Fact]
    public async Task BeforeStart_ReturnsFalse_SkipsWork_MarksFailed()
    {
        var queue = new ToolTaskQueue { BeforeStart = (_, _) => Task.FromResult(false) };
        var ran = false;

        var a = queue.Submit("压缩", _ => { ran = true; return Task.CompletedTask; });
        await a.Completion.Task;

        Assert.False(ran); // 门禁拦截：工作委托不执行
        Assert.Equal(ToolTaskState.Failed, a.State);
        Assert.Contains("批注保存失败", a.Error);
        Assert.False(queue.HasRunning);
    }

    [Fact]
    public async Task BeforeStart_ReturnsTrue_RunsWorkOnce()
    {
        var gateCalls = 0;
        var queue = new ToolTaskQueue
        {
            BeforeStart = (_, _) => { gateCalls++; return Task.FromResult(true); },
        };
        var ran = false;

        var a = queue.Submit("压缩", _ => { ran = true; return Task.CompletedTask; });
        await a.Completion.Task;

        Assert.True(ran);
        Assert.Equal(1, gateCalls);
        Assert.Equal(ToolTaskState.Completed, a.State);
    }
}
