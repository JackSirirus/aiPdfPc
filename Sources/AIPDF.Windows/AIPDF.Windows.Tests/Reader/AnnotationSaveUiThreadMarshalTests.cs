// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 F3-BLK-1 回归测试：批注保存的结果应用必须 POST 回构造时捕获的 UI 上下文（跨线程修改
// ObservableCollection 会触发 CollectionView 线程亲缘异常 → pill 卡「保存中」/进程崩溃/工具静默不执行）。
//
// 本测试在构造 AnnotationSaveService 前安装一个“记录型 SynchronizationContext”（自泵线程，
// 不使用真实 WPF Dispatcher），引擎桩强制从线程池线程返回，从而在修复前复现“后台线程应用结果”，
// 并在修复后断言：成功/失败/取消三条路径的应用都发生在该 UI 上下文线程上，且 pill 到达终态。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class AnnotationSaveUiThreadMarshalTests : IDisposable
{
    private readonly string _temp;

    public AnnotationSaveUiThreadMarshalTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "aipdf-blk1-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(_temp, "AAAA");
    }

    public void Dispose()
    {
        try { File.Delete(_temp); } catch (IOException) { }
    }

    private static AnnotationRecord Highlight(string id)
        => AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 10, 10)) with { Id = id };

    private static AnnotationSaveOptions Options(IAnnotationSavePrompt? prompt = null)
        => new()
        {
            DebounceMs = 100_000,
            Timeout = TimeSpan.FromSeconds(5),
            Prompt = prompt,
            ReloadAsync = (_, _, _) => Task.CompletedTask,
        };

    /// <summary>在记录型 UI 上下文中构造服务，随后恢复调用线程的上下文（模拟 UI 线程构造、后台回调）。</summary>
    private AnnotationSaveService CreateServiceOn(RecordingSynchronizationContext uiContext,
        IAnnotationSaveEngine engine, AnnotationSaveOptions options)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            return new AnnotationSaveService(engine, options);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ---------------------------------------------------------------- 成功路径

    [Fact]
    public async Task SaveSuccess_AppliesOnCapturedUiContext_AndReachesSavedPill()
    {
        using var uiContext = new RecordingSynchronizationContext();
        var source = new RecordingSource(_temp);
        source.Records.Add(Highlight("r1"));
        using var service = CreateServiceOn(uiContext, new PooledFakeEngine(), Options());

        var states = new ConcurrentQueue<(SavePillState State, int ThreadId)>();
        service.StateChanged += (_, e) => states.Enqueue((e.State, Environment.CurrentManagedThreadId));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);

        Assert.True(outcome.Succeeded);
        Assert.True(uiContext.PostCount > 0, "保存结果应用必须被 POST 回构造时捕获的 UI 上下文");
        Assert.Equal(uiContext.ThreadId, source.ApplySuccessThreadId);
        Assert.True(ReferenceEquals(uiContext, source.ApplySuccessContext),
            "UI 绑定集合的移除（ApplySaveSuccess）必须发生在 UI 上下文线程");
        Assert.Equal(SavePillState.Saved, service.GetState(_temp));
        Assert.Contains(states, s => s.State == SavePillState.Saved && s.ThreadId == uiContext.ThreadId);
    }

    // ---------------------------------------------------------------- 失败路径

    [Fact]
    public async Task SaveFailure_AppliesOnCapturedUiContext_AndReachesFailedPill()
    {
        using var uiContext = new RecordingSynchronizationContext();
        var source = new RecordingSource(_temp);
        source.Records.Add(Highlight("r1"));
        using var service = CreateServiceOn(uiContext, new PooledFakeEngine { Result = PooledFakeEngine.Fail }, Options());

        var states = new ConcurrentQueue<(SavePillState State, int ThreadId)>();
        service.StateChanged += (_, e) => states.Enqueue((e.State, Environment.CurrentManagedThreadId));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);

        Assert.False(outcome.Succeeded);
        Assert.True(uiContext.PostCount > 0, "失败结果应用必须被 POST 回 UI 上下文");
        Assert.Equal(uiContext.ThreadId, source.ApplyFailureThreadId);
        Assert.True(ReferenceEquals(uiContext, source.ApplyFailureContext),
            "失败态应用必须发生在 UI 上下文线程");
        Assert.Equal(SavePillState.Failed, service.GetState(_temp));
        Assert.Contains(states, s => s.State == SavePillState.Failed && s.ThreadId == uiContext.ThreadId);
    }

    // ---------------------------------------------------------------- 取消（冲突对话框）路径

    [Fact]
    public async Task ConflictCancel_AppliesOnCapturedUiContext_AndReachesFailedPill()
    {
        using var uiContext = new RecordingSynchronizationContext();
        var source = new RecordingSource(_temp);
        source.Records.Add(Highlight("r1"));
        var prompt = new CancelPrompt();
        using var service = CreateServiceOn(uiContext, new PooledFakeEngine(), Options(prompt));

        var states = new ConcurrentQueue<(SavePillState State, int ThreadId)>();
        service.StateChanged += (_, e) => states.Enqueue((e.State, Environment.CurrentManagedThreadId));
        service.RegisterDocument(source);

        // 打开后由外部程序修改 → 触发冲突；宿主选择「取消」。
        File.WriteAllText(_temp, "MODIFIED-EXTERNALLY");
        File.SetLastWriteTimeUtc(_temp, DateTime.UtcNow.AddSeconds(30));

        var outcome = await service.FlushAsync(_temp);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, prompt.ConflictCalls);
        Assert.True(uiContext.PostCount > 0, "取消路径的状态应用必须被 POST 回 UI 上下文");
        Assert.Equal("r1", Assert.Single(source.Records).Id); // 叠加层保留
        Assert.Equal(SavePillState.Failed, service.GetState(_temp));
        Assert.Contains(states, s => s.State == SavePillState.Failed && s.ThreadId == uiContext.ThreadId);
    }

    // ---------------------------------------------------------------- 应用结果抛异常：不得静默、终态 Failed、保留叠加层

    [Fact]
    public async Task ApplySuccessThrowing_SurfacesFailedPill_AndKeepsOverlay()
    {
        using var uiContext = new RecordingSynchronizationContext();
        var source = new RecordingSource(_temp) { ThrowOnApplySuccess = true };
        source.Records.Add(Highlight("r1"));
        using var service = CreateServiceOn(uiContext, new PooledFakeEngine(), Options());
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SavePillState.Failed, service.GetState(_temp)); // 不卡「保存中」
        Assert.True(uiContext.PostCount > 0);
        Assert.Equal(uiContext.ThreadId, source.ApplyFailureThreadId); // 失败兜底仍在 UI 线程
        Assert.Equal("r1", Assert.Single(source.Records).Id);          // 叠加层保留可重试
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>自泵记录型上下文：Post 回调在专属线程执行，模拟 UI Dispatcher 的线程亲缘。</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _pump;
        private int _postCount;

        public RecordingSynchronizationContext()
        {
            _pump = new Thread(Pump) { IsBackground = true, Name = "aipdf-recording-ui-context" };
            _pump.Start();
        }

        public int PostCount => Volatile.Read(ref _postCount);

        public int ThreadId => _pump.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        private void Pump()
        {
            SynchronizationContext.SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception error)
                {
                    Debug.WriteLine($"[RecordingSynchronizationContext] {error}");
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _pump.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }
    }

    /// <summary>记录 ApplySaveSuccess/Failure 执行时的上下文与线程。</summary>
    private sealed class RecordingSource(string path) : IAnnotationSaveSource
    {
        public List<AnnotationRecord> Records { get; } = [];

        public string FilePath { get; } = path;

        public string? Password => null;

        public bool IsReadOnly => false;

        public int CurrentPageNumber => 1;

        public double CurrentZoomPercent => 100;

        public bool ThrowOnApplySuccess { get; set; }

        public int ApplySuccessThreadId { get; private set; } = -1;

        public SynchronizationContext? ApplySuccessContext { get; private set; }

        public int ApplyFailureThreadId { get; private set; } = -1;

        public SynchronizationContext? ApplyFailureContext { get; private set; }

        public IReadOnlyList<AnnotationRecord> GetDirtyRecords() => Records.ToList();

        public void ApplySaveSuccess(IReadOnlyList<string> clearedIds, IReadOnlyList<AnnotationRecord> retainedRecords)
        {
            ApplySuccessThreadId = Environment.CurrentManagedThreadId;
            ApplySuccessContext = SynchronizationContext.Current;
            if (ThrowOnApplySuccess)
            {
                throw new InvalidOperationException("模拟 UI 绑定集合移除失败");
            }

            Records.RemoveAll(r => clearedIds.Contains(r.Id));
        }

        public void ApplySaveFailure(IReadOnlyList<AnnotationRecord> submittedRecords)
        {
            ApplyFailureThreadId = Environment.CurrentManagedThreadId;
            ApplyFailureContext = SynchronizationContext.Current;
        }
    }

    /// <summary>引擎桩：强制从线程池线程返回，模拟真实引擎回调线程（非 UI）。</summary>
    private sealed class PooledFakeEngine : IAnnotationSaveEngine
    {
        public Func<AnnotationSaveEngineResult> Result { get; set; } = Ok;

        public async Task<AnnotationSaveEngineResult> SaveAnnotationsAsync(
            string path,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
            string? password,
            CancellationToken cancellationToken)
        {
            await Task.Run(static () => { }).ConfigureAwait(false);
            return Result();
        }

        public static AnnotationSaveEngineResult Ok() => new(true, null, null, new Dictionary<string, string>());

        public static AnnotationSaveEngineResult Fail()
            => new(false, "模拟保存失败", "target_in_use", new Dictionary<string, string>());
    }

    private sealed class CancelPrompt : IAnnotationSavePrompt
    {
        public int ConflictCalls { get; private set; }

        public Task<ConflictResolution> ResolveExternalModificationAsync(
            AnnotationConflict conflict, CancellationToken cancellationToken)
        {
            ConflictCalls++;
            return Task.FromResult(ConflictResolution.Cancel);
        }

        public Task<string?> RequestSaveAsPathAsync(string sourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }
}
