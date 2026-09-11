// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T7 全局串行任务队列（FR-40/41/42）：替代各工具页 `if (IsRunning) return;` 的静默丢弃。
//
// 关键约束（计划 Must-NOT）：
//  - 同一时刻仅一个工具任务执行；FIFO；任务完成（成功/失败/取消）自动启动下一个；失败不自动重试（FR-40）。
//  - 取消排队项 = 从队列移除（无副作用，不启动工作）；取消执行中 = 取消令牌（EngineBridge 走 taskkill /T /F 子树），半成品保留（FR-41 AC-41.2）。
//  - 取消仅命中目标项，其余按序继续（FR-42）。
//  - 批注保存（T4 快速通道）直接调 EngineBridge，不经过本队列（FR-43）。
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.Services;

/// <summary>队列项生命周期状态：排队 → 执行 → 完成/失败/取消。</summary>
public enum ToolTaskState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 队列中的一个工具任务：展示名 + 工作委托 + 取消令牌 + 完成信号。
/// 状态与错误可观察，供状态栏与测试消费；位置仅用于「排队中·第 N 位」。
/// </summary>
public sealed class ToolTaskItem : ObservableObject
{
    private ToolTaskState _state = ToolTaskState.Queued;
    private string? _error;
    private int _position;

    internal ToolTaskItem(string name, Func<CancellationToken, Task> work, object? dedupKey)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "任务" : name;
        Work = work ?? throw new ArgumentNullException(nameof(work));
        DedupKey = dedupKey;
    }

    /// <summary>状态栏展示名（工具名）。</summary>
    public string Name { get; }

    /// <summary>去重键：同一键在队列/执行中只保留一项（同源视图模型用 <c>this</c> 作键）。</summary>
    public object? DedupKey { get; }

    internal Func<CancellationToken, Task> Work { get; }

    internal CancellationTokenSource Cancellation { get; } = new();

    internal CancellationToken Token => Cancellation.Token;

    internal TaskCompletionSource Completion { get; } = new();

    /// <summary>当前状态（变更通知 IsFinished/IsQueued/StateText）。</summary>
    public ToolTaskState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsQueued));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>失败原因（仅 <see cref="ToolTaskState.Failed"/> 有值）；取消不算失败。</summary>
    public string? Error
    {
        get => _error;
        internal set => SetProperty(ref _error, value);
    }

    /// <summary>排队位次（1 基；执行中/已结束为 0）。</summary>
    public int Position
    {
        get => _position;
        internal set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    /// <summary>是否已结束（完成/失败/取消）。</summary>
    public bool IsFinished => State is ToolTaskState.Completed or ToolTaskState.Failed or ToolTaskState.Cancelled;

    /// <summary>是否仍在排队（未开始执行）。</summary>
    public bool IsQueued => State == ToolTaskState.Queued;

    /// <summary>状态中文文案。</summary>
    public string StateText => State switch
    {
        ToolTaskState.Queued => "排队中",
        ToolTaskState.Running => "执行中",
        ToolTaskState.Completed => "已完成",
        ToolTaskState.Failed => "失败",
        ToolTaskState.Cancelled => "已取消",
        _ => "",
    };

    /// <summary>排队位次文案（非排队为空串）。</summary>
    public string PositionText => Position > 0 ? $"第 {Position} 位" : "";

    internal void Complete() => Completion.TrySetResult();
}

/// <summary>
/// 全局串行工具任务队列（FR-40）。工作委托在调用 <see cref="Submit"/> 的上下文启动（生产为 UI 线程），
/// 完成后续在相同上下文恢复并在该线程启动下一项——UI 绑定与可观察集合更新不需要额外 marshal。
/// </summary>
public sealed class ToolTaskQueue : ObservableObject
{
    /// <summary>进程内默认单例：所有工具页与主窗口共用同一串行队列。</summary>
    public static ToolTaskQueue Shared { get; } = new();

    private readonly object _gate = new();
    private readonly ObservableCollection<ToolTaskItem> _pending = [];
    private ToolTaskItem? _running;

    public ToolTaskQueue()
    {
        Pending = new ReadOnlyObservableCollection<ToolTaskItem>(_pending);
        CancelTaskCommand = new RelayCommand<ToolTaskItem>(item => Cancel(item));
    }

    /// <summary>排队中的任务（不含执行中），供状态栏 ✕ 取消。</summary>
    public ReadOnlyObservableCollection<ToolTaskItem> Pending { get; }

    /// <summary>取消队列中（或执行中）的某一项；返回是否命中。</summary>
    public IRelayCommand<ToolTaskItem> CancelTaskCommand { get; }

    /// <summary>当前执行中的任务；空闲为 null。</summary>
    public ToolTaskItem? RunningItem
    {
        get => _running;
        private set
        {
            if (SetProperty(ref _running, value))
            {
                OnPropertyChanged(nameof(HasRunning));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>是否有任务在执行。</summary>
    public bool HasRunning => _running is not null;

    /// <summary>状态栏摘要：「正在执行：X」+「排队中·第 1 位 Y、第 2 位 Z、第 3 位 W…还有 N 个」（仅列前 3）。空闲为空串。</summary>
    public string StatusText
    {
        get
        {
            ToolTaskItem? running = RunningItem;
            ToolTaskItem[] pending;
            lock (_gate)
            {
                pending = [.. _pending];
            }

            if (running is null && pending.Length == 0)
            {
                return "";
            }

            var builder = new StringBuilder();
            if (running is not null)
            {
                builder.Append("正在执行：").Append(running.Name);
            }

            if (pending.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" · ");
                }

                builder.Append("排队中·");
                var shown = Math.Min(3, pending.Length);
                for (var i = 0; i < shown; i++)
                {
                    if (i > 0)
                    {
                        builder.Append('、');
                    }

                    builder.Append($"第 {i + 1} 位 {pending[i].Name}");
                }

                if (pending.Length > 3)
                {
                    builder.Append($"…还有 {pending.Length - 3} 个");
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>执行中任务被取消（排队项取消不触发）；宿主据此弹 Toast「任务已取消」。</summary>
    public event EventHandler<ToolTaskItem>? RunningTaskCancelled;

    /// <summary>
    /// V2 T10 FR-21：任务工作委托执行前的宿主门禁。返回 false 则不执行工作委托并把任务标记为失败
    /// （宿主负责先弹出「批注保存失败，请重试」+ 强制继续/取消）。门禁本身不入队、不新增队列项；
    /// 仅队列型工具页经此覆盖，编辑组（不经队列）由 <c>IToolStartGated.StartGate</c> 覆盖。
    /// </summary>
    public Func<ToolTaskItem, CancellationToken, Task<bool>>? BeforeStart { get; set; }

    /// <summary>仅创建任务项，不入队（测试与需要先绑定再入队的场景）。</summary>
    public ToolTaskItem Create(string name, Func<CancellationToken, Task> work, object? dedupKey = null)
        => new(name, work, dedupKey);

    /// <summary>
    /// 创建并入队一个任务；命中去重键时返回已在队/执行中的同一项（不重复执行传入工作）。
    /// 返回值即调用方应等待完成的项。
    /// </summary>
    public ToolTaskItem Submit(string name, Func<CancellationToken, Task> work, object? dedupKey = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        ToolTaskItem item;
        lock (_gate)
        {
            if (dedupKey is not null)
            {
                var existing = _running is { } running && Equals(running.DedupKey, dedupKey)
                    ? running
                    : _pending.FirstOrDefault(pending => Equals(pending.DedupKey, dedupKey));
                if (existing is not null)
                {
                    return existing;
                }
            }

            item = new ToolTaskItem(name, work, dedupKey);
            _pending.Add(item);
        }

        RefreshPositions();
        OnPropertyChanged(nameof(StatusText));
        Pump();
        return item;
    }

    /// <summary>入队一个已创建的任务项（未命中去重；用于 Create + Enqueue 两步场景）。</summary>
    public void Enqueue(ToolTaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (item.IsFinished || _pending.Contains(item) || ReferenceEquals(_running, item))
            {
                return;
            }

            _pending.Add(item);
        }

        RefreshPositions();
        OnPropertyChanged(nameof(StatusText));
        Pump();
    }

    /// <summary>
    /// 取消一个任务：排队项从队列移除并标记已取消（不执行工作、无副作用）；执行中项取消其令牌
    /// （引擎桥以 taskkill /T /F 终止子树，半成品保留）。其余任务不受影响，按序继续（FR-41/42）。
    /// </summary>
    public bool Cancel(ToolTaskItem? item)
    {
        if (item is null)
        {
            return false;
        }

        bool running;
        lock (_gate)
        {
            if (item.State == ToolTaskState.Queued)
            {
                if (!_pending.Remove(item))
                {
                    return false;
                }

                item.State = ToolTaskState.Cancelled;
                running = false;
            }
            else if (item.State == ToolTaskState.Running && ReferenceEquals(_running, item))
            {
                running = true;
            }
            else
            {
                return false;
            }
        }

        if (running)
        {
            try
            {
                item.Cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // 令牌回调异常不阻断取消语义；工作委托已收到取消信号。
            }

            return true;
        }

        // 排队项：直接完成（等待方返回），无引擎副作用。
        item.Complete();
        item.Cancellation.Dispose();
        RefreshPositions();
        OnPropertyChanged(nameof(StatusText));
        return true;
    }

    /// <summary>从队列首部取出下一项并执行（空闲时）。</summary>
    private void Pump()
    {
        ToolTaskItem next;
        lock (_gate)
        {
            if (_running is not null || _pending.Count == 0)
            {
                return;
            }

            next = _pending[0];
            _pending.RemoveAt(0);
        }

        next.State = ToolTaskState.Running;
        RunningItem = next;
        RefreshPositions();
        OnPropertyChanged(nameof(StatusText));
        _ = RunAsync(next);
    }

    private async Task RunAsync(ToolTaskItem item)
    {
        try
        {
            // V2 T10 FR-21：启动前门禁（强制 flush 未保存批注）。拦截时不执行工作委托。
            if (BeforeStart is { } gate && !await gate(item, item.Token))
            {
                item.Error = "批注保存失败，工具未启动。";
                item.State = ToolTaskState.Failed;
                return;
            }

            await item.Work(item.Token);
            // 工作委托可能吞掉 OperationCanceledException（既有 VM 行为）；以令牌为准判定取消。
            item.State = item.Token.IsCancellationRequested
                ? ToolTaskState.Cancelled
                : ToolTaskState.Completed;
        }
        catch (OperationCanceledException)
        {
            item.State = ToolTaskState.Cancelled;
        }
        catch (Exception error)
        {
            item.Error = error.Message;
            item.State = ToolTaskState.Failed;
        }
        finally
        {
            if (ReferenceEquals(RunningItem, item))
            {
                RunningItem = null;
            }

            item.Complete();
            item.Cancellation.Dispose();
            RefreshPositions();
            OnPropertyChanged(nameof(StatusText));
            Pump();

            if (item.State == ToolTaskState.Cancelled)
            {
                RunningTaskCancelled?.Invoke(this, item);
            }
        }
    }

    /// <summary>刷新排队项位次（执行中/已结束为 0）。</summary>
    private void RefreshPositions()
    {
        ToolTaskItem[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _pending];
        }

        for (var i = 0; i < snapshot.Length; i++)
        {
            snapshot[i].Position = i + 1;
        }

        if (RunningItem is { } running)
        {
            running.Position = 0;
        }
    }
}
