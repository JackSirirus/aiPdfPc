// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4 批注保存服务（快速通道）：防抖 / 文件级互斥 / 30s 超时 / 保存成功后的键迁移与基线刷新。
//
// 关键约束（计划 Must-NOT）：
//  - 不入全局任务队列（FR-43）：直接调 EngineBridge.ExecuteAsync；同一文件串行、不同文件并行；
//  - 不实时逐次写盘：2s 防抖合并一次引擎请求；
//  - 超时在 WPF 侧计时（引擎无超时）：30s 到期取消 → EngineBridge 走 taskkill /T /F 终止子树；
//  - 失败保留叠加层、可重试；连续 3 次失败建议「另存为」（FR-16）；
//  - 唯一真相源：保存成功后清对应叠加层并触发 close-first 重载（回调由宿主提供）。
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;

namespace AIPDF.Windows.Annotations;

/// <summary>批注保存的引擎传输结果（桥层解析后的最小契约）。</summary>
public sealed record AnnotationSaveEngineResult(
    bool Ok,
    string? Error,
    string? ErrorCode,
    IReadOnlyDictionary<string, string> Handles)
{
    /// <summary>30s 无响应（WPF 侧计时）后的失败结果。</summary>
    public static AnnotationSaveEngineResult Timeout() => new(
        false, "保存超时（30 秒无响应），已终止引擎进程。", "timeout",
        new Dictionary<string, string>());

    /// <summary>桥传输失败。</summary>
    public static AnnotationSaveEngineResult TransportFailure(string message) => new(
        false, message, null, new Dictionary<string, string>());
}

/// <summary>批注保存的引擎抽象（生产实现走 EngineBridge；测试可桩替）。</summary>
public interface IAnnotationSaveEngine
{
    Task<AnnotationSaveEngineResult> SaveAnnotationsAsync(
        string path,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
        string? password,
        CancellationToken cancellationToken);
}

/// <summary>把批注动作序列化为 <c>save_annotations</c> 请求并调用 <see cref="EngineBridge"/>。</summary>
public sealed class EngineAnnotationSaveEngine : IAnnotationSaveEngine
{
    private static readonly IReadOnlyDictionary<string, string> NoHandles = new Dictionary<string, string>();

    private readonly EngineBridge _bridge;

    public EngineAnnotationSaveEngine(EngineBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    public async Task<AnnotationSaveEngineResult> SaveAnnotationsAsync(
        string path,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
        string? password,
        CancellationToken cancellationToken)
    {
        var request = new EngineRequest
        {
            Operation = "save_annotations",
            Files = [path],
        };
        request.Options["annotations"] = actions;
        if (!string.IsNullOrEmpty(password)) request.Options["password"] = password;

        EngineResult result;
        try
        {
            result = await _bridge.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 由服务判定超时
        }
        catch (EngineBridgeException error)
        {
            return AnnotationSaveEngineResult.TransportFailure(error.Message);
        }

        string? errorCode = null;
        if (result.Extra.TryGetValue("errorCode", out var code)
            && code.ValueKind == JsonValueKind.String)
        {
            errorCode = code.GetString();
        }

        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        ReadHandles(result.Extra, "annotationHandles", handles);
        ReadHandles(result.Extra, "annotationXrefs", handles);

        return new AnnotationSaveEngineResult(result.Ok, result.Error, errorCode, handles);
    }

    private static void ReadHandles(
        IReadOnlyDictionary<string, JsonElement> extra, string key, Dictionary<string, string> sink)
    {
        if (!extra.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            sink[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }
    }
}

/// <summary>保存服务可选接线（宿主提供；缺省即 no-op）。</summary>
public sealed class AnnotationSaveOptions
{
    /// <summary>防抖时长（ms），默认 2000（FR-14）。</summary>
    public int DebounceMs { get; init; } = 2000;

    /// <summary>引擎请求超时（WPF 侧），默认 30s（FR-16）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>保存成功后 close-first 重载（覆盖渲染/文本/panels/search 全部句柄域）。</summary>
    public Func<string, string?, CancellationToken, Task>? ReloadAsync { get; init; }

    /// <summary>阅读状态键迁移（T5 ReadingStateStore.MigrateAsync 的接线；未接线为 no-op）。</summary>
    public IReadingStateMigrator? Migrator { get; init; }

    /// <summary>刷新 T11 内存冲突基线为保存后的 size/mtime（BLK-1）。</summary>
    public Action<string, long, DateTimeOffset>? RefreshConflictBaseline { get; init; }

    /// <summary>同步最近卡片（path, bytes, lastPage(1 基), lastZoom%）到新值。</summary>
    public Action<string, long, int, double>? SyncRecentCard { get; init; }

    /// <summary>只读判定覆盖（默认查文件属性）。</summary>
    public Func<string, bool>? IsReadOnly { get; init; }
}

/// <summary>批注保存状态变化事件。</summary>
public sealed class AnnotationSaveStateChangedEventArgs : EventArgs
{
    public AnnotationSaveStateChangedEventArgs(string path, SavePillState state, bool shouldSuggestSaveAs, string? error)
    {
        Path = path;
        State = state;
        ShouldSuggestSaveAs = shouldSuggestSaveAs;
        Error = error;
    }

    public string Path { get; }
    public SavePillState State { get; }
    public bool ShouldSuggestSaveAs { get; }
    public string? Error { get; }
}

/// <summary>关闭标签前强制 flush 的决策（纯逻辑，可单测；AC-14.3/AC-8.2、ST-22）。</summary>
public enum CloseFlushDecision
{
    /// <summary>无未保存批注或 flush 成功 → 允许关闭。</summary>
    CloseNow,

    /// <summary>有未保存批注且 flush 失败 → 阻止关闭并提示（可重试/另存为）。</summary>
    Blocked,
}

/// <summary>关闭拦截决策的纯函数。</summary>
public static class CloseFlushDecisionLogic
{
    public static CloseFlushDecision Decide(bool hasUnsavedAnnotations, bool flushSucceeded)
        => !hasUnsavedAnnotations || flushSucceeded ? CloseFlushDecision.CloseNow : CloseFlushDecision.Blocked;
}

/// <summary>
/// 单窗口批注保存服务。每文件一个 <see cref="DocSession"/>（独立防抖计时器 + 文件级信号量），
/// 不同文件可并行、同文件串行。所有 FPDF_* 不在此类（引擎与渲染服务各自守卫）。
/// </summary>
public sealed class AnnotationSaveService : IDisposable
{
    private readonly IAnnotationSaveEngine _engine;
    private readonly AnnotationSaveOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, DocSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public AnnotationSaveService(IAnnotationSaveEngine engine, AnnotationSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options ?? new AnnotationSaveOptions();
    }

    /// <summary>引擎请求超时（测试可读）。</summary>
    public TimeSpan SaveTimeout => _options.Timeout;

    /// <summary>防抖时长（测试可读）。</summary>
    public int DebounceMs => _options.DebounceMs;

    /// <summary>savepill 状态变化（宿主 marshal 回 UI 线程更新控件）。</summary>
    public event EventHandler<AnnotationSaveStateChangedEventArgs>? StateChanged;

    // ------------------------------------------------------------------ 注册 / 注销

    /// <summary>注册文档（打开标签时调用）。重复注册同路径以新 source 为准并保留会话。</summary>
    public void RegisterDocument(IAnnotationSaveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = NormalizePath(source.FilePath);
        DocSession session;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(path, out session!))
            {
                session = new DocSession(path, new AnnotationSaveSession());
                session.Baseline = FileStateCapture(path);
                _sessions[path] = session;
            }
            session.Source = source;
        }

        var readOnly = TryGetReadOnly(path, source);
        if (readOnly)
        {
            session.Session.MarkReadOnly();
            RaiseStateChanged(path, SavePillState.ReadOnly, null);
        }
        else if (session.Baseline is { } stamp)
        {
            session.BaselineKey = stamp.Key;
        }
    }

    /// <summary>注销文档（关闭标签时调用）：停止计时器并释放信号量。</summary>
    public void UnregisterDocument(string path)
    {
        path = NormalizePath(path);
        DocSession? session;
        lock (_lock)
        {
            if (!_sessions.Remove(path, out session)) return;
        }
        session.Dispose();
    }

    /// <summary>当前 savepill 状态；未注册返回 Saved。</summary>
    public SavePillState GetState(string path)
    {
        path = NormalizePath(path);
        lock (_lock)
        {
            return _sessions.TryGetValue(path, out var session) ? session.Session.State : SavePillState.Saved;
        }
    }

    /// <summary>该文档是否存在未保存批注/删除。</summary>
    public bool HasPending(string path)
    {
        path = NormalizePath(path);
        lock (_lock)
        {
            return _sessions.TryGetValue(path, out var session) && session.Session.HasPending;
        }
    }

    // ------------------------------------------------------------------ 变化通知（重置防抖）

    /// <summary>批注新增/修改：重置 2s 防抖（新操作重置；非批注操作不调用本方法）。</summary>
    public void NotifyAnnotationsChanged(string path)
    {
        var session = FindSession(path);
        if (session is null) return;
        session.Session.NotifyChanged();
        ScheduleDebounce(session);
        RaiseStateChanged(session.Path, session.Session.State, null);
    }

    /// <summary>批注被移除：必要时排队引擎删除，并重置防抖。</summary>
    public void NotifyAnnotationRemoved(string path, AnnotationRecord removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var session = FindSession(path);
        if (session is null) return;
        session.Session.NotifyRemoved(removed.Id);
        ScheduleDebounce(session);
        RaiseStateChanged(session.Path, session.Session.State, null);
    }

    // ------------------------------------------------------------------ 提交

    /// <summary>
    /// 强制 flush（跳过防抖；关闭标签 / 工具执行前调用）。同文件串行（信号量）。
    /// 返回的 <see cref="AnnotationSaveOutcome"/> 供关闭拦截决策；失败时 <c>RequiresFollowUp=true</c>。
    /// </summary>
    public async Task<AnnotationSaveOutcome> FlushAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        DocSession session = GetSessionOrThrow(path);
        StopDebounce(session);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SubmitAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<AnnotationSaveOutcome> SubmitAsync(DocSession session, CancellationToken cancellationToken)
    {
        if (session.Source is null)
        {
            session.Session.MarkSavedIfClean();
            return AnnotationSaveOutcome.Empty;
        }

        if (session.Session.State == SavePillState.ReadOnly)
        {
            return AnnotationSaveOutcome.Empty; // 只读：不提交
        }

        var records = session.Source.GetDirtyRecords();
        var batch = session.Session.BuildBatch(records);
        if (batch is null)
        {
            session.Session.MarkSavedIfClean();
            RaiseStateChanged(session.Path, session.Session.State, null);
            return AnnotationSaveOutcome.Empty;
        }

        session.Session.MarkSubmitting();
        RaiseStateChanged(session.Path, SavePillState.Saving, null);

        var actions = BuildEngineActions(batch);
        AnnotationSaveEngineResult result;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(_options.Timeout);
            try
            {
                result = await _engine
                    .SaveAnnotationsAsync(session.Path, actions, session.Source.Password, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = AnnotationSaveEngineResult.Timeout(); // 30s 到期，引擎进程树已由桥终止
            }
            catch (OperationCanceledException)
            {
                // 外部取消（关闭窗口等）：不改变会话状态，交由调用方处理
                RaiseStateChanged(session.Path, session.Session.State, null);
                throw;
            }
            catch (Exception error)
            {
                result = AnnotationSaveEngineResult.TransportFailure(error.Message);
            }
        }

        if (!result.Ok)
        {
            session.Session.ApplyFailure();
            session.Source.ApplySaveFailure(batch.SubmittedRecords);
            RaiseStateChanged(session.Path, SavePillState.Failed, result.Error);
            return AnnotationSaveOutcome.Empty with { RequiresFollowUp = true, Succeeded = false, Error = result.Error };
        }

        // 成功：对飞行后的最新记录快照应用 H-4 粒度清理
        var outcome = session.Session.ApplySuccess(batch, session.Source.GetDirtyRecords());
        session.Source.ApplySaveSuccess(outcome.ClearedIds, outcome.RetainedRecords);

        await OnSaveSuccessAsync(session, cancellationToken).ConfigureAwait(false);

        if (outcome.RequiresFollowUp) ScheduleDebounce(session);
        RaiseStateChanged(session.Path, session.Session.State, null);
        return outcome;
    }

    /// <summary>
    /// 保存成功后的原子状态迁移（BLK-1）：计算新键 → 迁移阅读状态 → 刷新冲突基线 → 同步最近卡片
    /// → close-first 重载（唯一真相源）。任一回调抛错只记录诊断，不改写「保存成功」结论。
    /// </summary>
    private async Task OnSaveSuccessAsync(DocSession session, CancellationToken cancellationToken)
    {
        var oldKey = session.BaselineKey;
        var newStamp = FileStateCapture(session.Path);
        if (newStamp is { } stamp)
        {
            session.Baseline = stamp;
            session.BaselineKey = stamp.Key;
        }

        // (a) 键迁移（含书签；T5 实现）。旧键为空（首次保存前未采集）时跳过。
        if (_options.Migrator is not null && oldKey is not null && newStamp is { } ns && oldKey != ns.Key)
        {
            try
            {
                await _options.Migrator.MigrateAsync(oldKey, ns.Key).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Trace.WriteLine($"[AnnotationSaveService] 阅读状态键迁移失败: {session.Path} ({error.Message})");
            }
        }

        if (newStamp is { } current)
        {
            var mtime = new DateTimeOffset(current.ModifiedTicks, TimeSpan.Zero);

            // (b) 刷新 T11 内存冲突基线为保存后的 size/mtime，否则下次保存必误报「外部修改」。
            TryInvoke(() => _options.RefreshConflictBaseline?.Invoke(session.Path, current.Size, mtime),
                "刷新冲突基线");

            // (c) 同步最近卡片进度。
            var page = session.Source?.CurrentPageNumber ?? 0;
            var zoom = session.Source?.CurrentZoomPercent ?? 0;
            TryInvoke(() => _options.SyncRecentCard?.Invoke(session.Path, current.Size, page, zoom),
                "同步最近卡片");
        }

        // close-first 重载（覆盖渲染/文本/panels/search 全部句柄域，清 LRU）
        if (_options.ReloadAsync is not null)
        {
            try
            {
                await _options.ReloadAsync(session.Path, session.Source?.Password, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                Trace.WriteLine($"[AnnotationSaveService] 保存后重载失败: {session.Path} ({error.Message})");
            }
        }
    }

    // ------------------------------------------------------------------ 防抖调度

    private void ScheduleDebounce(DocSession session)
    {
        lock (session.DebounceLock)
        {
            session.DebounceGeneration = session.Session.Generation;
            session.DebounceTimer ??= new Timer(OnDebounceElapsed, session,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            session.DebounceTimer.Change(_options.DebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    private void StopDebounce(DocSession session)
    {
        lock (session.DebounceLock)
        {
            session.DebounceTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        var session = (DocSession)state!;
        lock (_lock)
        {
            if (_disposed) return;
        }
        lock (session.DebounceLock)
        {
            // 新操作已重置：过期代际不再触发（由新计时器负责）
            if (session.DebounceGeneration != session.Session.Generation) return;
        }

        try
        {
            session.LastDebounceTask = FlushAsync(session.Path);
        }
        catch (Exception error)
        {
            Trace.WriteLine($"[AnnotationSaveService] 防抖保存异常: {session.Path} ({error.Message})");
        }
    }

    /// <summary>等待某文档最近一次防抖触发的提交（测试用）。</summary>
    internal Task? GetLastDebounceTask(string path)
    {
        var session = FindSession(path);
        return session?.LastDebounceTask;
    }

    // ------------------------------------------------------------------ 引擎动作序列化

    private static List<IReadOnlyDictionary<string, object?>> BuildEngineActions(AnnotationSaveBatch batch)
    {
        var actions = new List<IReadOnlyDictionary<string, object?>>(batch.Actions.Count);
        foreach (var action in batch.Actions)
        {
            switch (action.Kind)
            {
                case AnnotationActionKind.Delete:
                    actions.Add(new Dictionary<string, object?>
                    {
                        ["action"] = "delete",
                        ["name"] = action.Id,
                    });
                    break;

                case AnnotationActionKind.SetColor when action.Record is { } setColor:
                    actions.Add(new Dictionary<string, object?>
                    {
                        ["action"] = "setColor",
                        ["name"] = action.Id,
                        ["color"] = setColor.Color,
                    });
                    break;

                case AnnotationActionKind.Add when action.Record is { } record:
                    actions.Add(BuildAddAction(record));
                    break;
            }
        }
        return actions;
    }

    private static Dictionary<string, object?> BuildAddAction(AnnotationRecord record)
    {
        var rect = new[] { record.Rect.Left, record.Rect.Top, record.Rect.Right, record.Rect.Bottom };
        var action = new Dictionary<string, object?>
        {
            ["action"] = "add",
            ["id"] = record.Id,
            ["page"] = record.PageIndex,
            ["color"] = record.Color,
            ["rect"] = rect,
            ["kind"] = record.Kind == AnnotationKind.Note ? "note" : "highlight",
        };
        if (record.Kind == AnnotationKind.Note)
        {
            action["text"] = record.Note ?? string.Empty;
            action["noteRect"] = rect;
        }
        return action;
    }

    // ------------------------------------------------------------------ 只读 / 路径

    private bool TryGetReadOnly(string path, IAnnotationSaveSource source)
    {
        if (_options.IsReadOnly is not null) return _options.IsReadOnly(path);
        if (source.IsReadOnly) return true;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileStateStamp? FileStateCapture(string path)
        => ReadingStateKey.TryCapture(path);

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private DocSession? FindSession(string path)
    {
        path = NormalizePath(path);
        lock (_lock)
        {
            return _sessions.TryGetValue(path, out var session) ? session : null;
        }
    }

    private DocSession GetSessionOrThrow(string path)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(path, out var session)) return session;
            // 未注册文档也允许 flush（空操作）以便关闭流程统一调用；建立一个无 source 的临时会话。
            session = new DocSession(path, new AnnotationSaveSession()) { Baseline = FileStateCapture(path) };
            session.BaselineKey = session.Baseline?.Key;
            _sessions[path] = session;
            return session;
        }
    }

    private void RaiseStateChanged(string path, SavePillState state, string? error)
    {
        bool suggest;
        lock (_lock)
        {
            suggest = _sessions.TryGetValue(path, out var session) && session.Session.ShouldSuggestSaveAs;
        }
        StateChanged?.Invoke(this, new AnnotationSaveStateChangedEventArgs(path, state, suggest, error));
    }

    private static void TryInvoke(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Trace.WriteLine($"[AnnotationSaveService] {what}失败: {error.Message}");
        }
    }

    public void Dispose()
    {
        List<DocSession> sessions;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in sessions) session.Dispose();
    }

    /// <summary>单文档保存会话状态（计时器 + 文件级互斥 + 基线 stamp）。</summary>
    private sealed class DocSession : IDisposable
    {
        public DocSession(string path, AnnotationSaveSession session)
        {
            Path = path;
            Session = session;
        }

        public string Path { get; }
        public AnnotationSaveSession Session { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object DebounceLock { get; } = new();
        public Timer? DebounceTimer;
        public long DebounceGeneration;
        public Task? LastDebounceTask;

        public IAnnotationSaveSource? Source;
        public FileStateStamp? Baseline;
        public string? BaselineKey;

        public void Dispose()
        {
            lock (DebounceLock)
            {
                DebounceTimer?.Dispose();
                DebounceTimer = null;
            }
            Gate.Dispose();
        }
    }
}

/// <summary>
/// 宿主提供的单文档批注来源（ReaderViewModel 适配器）：
/// 拉取未保存记录、应用保存结果、提供密码与当前阅读位置（最近卡片同步）。
/// </summary>
public interface IAnnotationSaveSource
{
    /// <summary>规范化全路径。</summary>
    string FilePath { get; }

    /// <summary>会话密码（T9 驻留；V2 未接线为 null）。绝不落盘/日志。</summary>
    string? Password { get; }

    /// <summary>文件是否只读（无法就地保存，ST-08）。</summary>
    bool IsReadOnly { get; }

    /// <summary>当前页码（1 基；最近卡片进度同步）。</summary>
    int CurrentPageNumber { get; }

    /// <summary>当前缩放百分比（最近卡片进度同步）。</summary>
    double CurrentZoomPercent { get; }

    /// <summary>当前所有未保存批注记录（批次构建输入）。</summary>
    IReadOnlyList<AnnotationRecord> GetDirtyRecords();

    /// <summary>保存成功：移除已内嵌记录（Id 在 clearedIds 中），保留飞行期间新增/修改的记录。</summary>
    void ApplySaveSuccess(IReadOnlyList<string> clearedIds, IReadOnlyList<AnnotationRecord> retainedRecords);

    /// <summary>保存失败：叠加层与记录保留（状态可转 Failed）。</summary>
    void ApplySaveFailure(IReadOnlyList<AnnotationRecord> submittedRecords);
}
