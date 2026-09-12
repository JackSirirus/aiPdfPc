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

    /// <summary>
    /// T11 外部修改冲突 / 目标占用的宿主交互（模态对话框）。
    /// 未接线时冲突一律保守取消（<see cref="ConflictChoice.Cancel"/>）——绝不静默覆盖外部修改。
    /// </summary>
    public IAnnotationSavePrompt? Prompt { get; init; }

    /// <summary>T11 另存为成功后通知宿主把文档标签/状态切到新路径（oldPath, newPath）。</summary>
    public Action<string, string>? OnSavedAs { get; init; }

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

    /// <summary>
    /// 构造时捕获的 UI 同步上下文（F3-BLK-1）：保存结果的“应用阶段”——移除叠加层记录、
    /// 刷新冲突基线、同步最近卡片、迁移阅读状态键、close-first 重载——必须回到此线程执行，
    /// 否则后台线程修改 UI 绑定的 <c>ObservableCollection</c> 会抛 CollectionView 线程亲缘异常。
    /// </summary>
    private readonly SynchronizationContext? _uiContext;

    public AnnotationSaveService(IAnnotationSaveEngine engine, AnnotationSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options ?? new AnnotationSaveOptions();
        _uiContext = SynchronizationContext.Current;
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

            // T11：另存为成功后基线/状态已切到新路径；宿主重定向在门禁外执行，
            // 避免宿主在同一次提交内回调 UnregisterDocument 造成会话门禁重入。
            if (session.PendingSaveAs is { } savedAs)
            {
                session.PendingSaveAs = null;
                TryInvoke(() => _options.OnSavedAs?.Invoke(savedAs.OldPath, savedAs.NewPath), "另存为路径切换");
            }
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

        var originalPath = session.Path;
        var actions = BuildEngineActions(batch);

        // T11 FR-18：防抖后、发送引擎前比对打开/上次保存时记录的 size/mtime 基线。
        // 检测完全在应用侧（不耗引擎资源）；V2 仅 size/mtime，无 SHA-256（Q4）。
        var targetPath = session.Path;
        var conflict = DetectExternalModification(session);
        if (conflict is not null)
        {
            var resolution = await ResolveExternalModificationAsync(conflict, cancellationToken).ConfigureAwait(false);
            if (resolution.Choice == ConflictChoice.Cancel)
            {
                return await CancelConflictedSaveAsync(session).ConfigureAwait(false);
            }

            if (resolution.Choice == ConflictChoice.SaveAs)
            {
                if (string.IsNullOrWhiteSpace(resolution.SaveAsPath)
                    || IsSameFile(resolution.SaveAsPath, session.Path))
                {
                    // 未选路径/路径未变：保守取消，绝不静默覆盖
                    return await CancelConflictedSaveAsync(session).ConfigureAwait(false);
                }

                targetPath = Path.GetFullPath(resolution.SaveAsPath);
            }
            // Overwrite：targetPath 保持 session.Path，继续原子就地写入（接受覆盖外部修改）。
        }

        AnnotationSaveEngineResult result;
        string? savedAsPath = null;

        if (!IsSameFile(targetPath, session.Path))
        {
            // 另存为：先把磁盘上的原文件复制到新路径（原文件全程不变），再对新副本原子写入批注。
            if (!TryPrepareSaveAsCopy(session, targetPath, out var copyError))
            {
                return await FailSubmitAsync(session, batch, copyError).ConfigureAwait(false);
            }

            result = await InvokeEngineAsync(targetPath, actions, session.Source.Password, cancellationToken)
                .ConfigureAwait(false);
            if (result.Ok) savedAsPath = targetPath;
        }
        else
        {
            result = await InvokeEngineAsync(session.Path, actions, session.Source.Password, cancellationToken)
                .ConfigureAwait(false);

            // T3 原子替换被占用（errorCode=target_in_use）：复用冲突对话框的「另存为」回退。
            if (!result.Ok && string.Equals(result.ErrorCode, "target_in_use", StringComparison.Ordinal))
            {
                var chosen = await RequestSaveAsPathAsync(session.Path, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(chosen) && !IsSameFile(chosen, session.Path))
                {
                    targetPath = Path.GetFullPath(chosen);
                    if (!TryPrepareSaveAsCopy(session, targetPath, out var copyError))
                    {
                        return await FailSubmitAsync(session, batch, copyError).ConfigureAwait(false);
                    }

                    result = await InvokeEngineAsync(targetPath, actions, session.Source.Password, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Ok) savedAsPath = targetPath;
                }
            }
        }

        if (!result.Ok)
        {
            return await FailSubmitAsync(session, batch, result.Error).ConfigureAwait(false);
        }

        // 成功：对飞行后的最新记录快照应用 H-4 粒度清理。
        // F3-BLK-1：ApplySuccess 读取的是 UI 绑定记录，ApplySaveSuccess 会移除 UI 绑定叠加层
        // （ObservableCollection），后续基线刷新/卡片同步/键迁移/close-first 重载同样触碰 UI——
        // 全部必须回到 UI 线程执行，否则后台线程修改 UI 集合抛 CollectionView 线程亲缘异常，
        // 导致 pill 永久「保存中」、基线不刷新（下次误报外部修改）、重试路径崩溃、工具 flush 静默失败。
        AnnotationSaveOutcome outcome = AnnotationSaveOutcome.Empty;
        try
        {
            await RunOnUiThreadAsync(async () =>
            {
                outcome = session.Session.ApplySuccess(batch, session.Source!.GetDirtyRecords());
                session.Source.ApplySaveSuccess(outcome.ClearedIds, outcome.RetainedRecords);

                if (savedAsPath is not null)
                {
                    // 另存为：把会话基线/键切到新路径；宿主经 PendingSaveAs 重定向文档标签。
                    // newPath 见 savedAsPath（已在进入 UI 线程前确定）。
                    await OnSaveAsSuccessAsync(session, savedAsPath, cancellationToken);
                }
                else
                {
                    await OnSaveSuccessAsync(session, cancellationToken);
                }

                // 终态通知（Saved / 仍有后续的 Saving）必须在 UI 线程发出，保证 pill 不再卡「保存中」。
                RaiseStateChanged(session.Path, session.Session.State, null);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 外部取消（关闭窗口等）由调用方处理
        }
        catch (Exception error)
        {
            // 应用结果失败不得静默：记录诊断、转 Failed 终态、保留叠加层可重试（F3-BLK-1 要求）。
            Trace.WriteLine($"[AnnotationSaveService] 保存成功后的状态应用失败: {session.Path} ({error.Message})");
            return await FailSubmitAsync(session, batch, error.Message).ConfigureAwait(false);
        }

        if (savedAsPath is { } newPath)
        {
            if (outcome.RequiresFollowUp)
            {
                ScheduleDebounce(session); // 飞行期间仍有改动：下一轮直接写新路径
            }
            else
            {
                session.PendingSaveAs = (originalPath, newPath);
            }

            return outcome;
        }

        if (outcome.RequiresFollowUp) ScheduleDebounce(session);
        return outcome;
    }

    /// <summary>冲突对话框「取消」：不发送引擎，批注叠加层保留、可重试（AC-18.2）。应用在 UI 线程。</summary>
    private async Task<AnnotationSaveOutcome> CancelConflictedSaveAsync(DocSession session)
    {
        await RunOnUiThreadAsync(() =>
        {
            session.Session.ApplyConflictCancelled();
            RaiseStateChanged(session.Path, SavePillState.Failed, ConflictCancelledMessage);
        }).ConfigureAwait(false);

        return AnnotationSaveOutcome.Empty with
        {
            RequiresFollowUp = true,
            Succeeded = false,
            Error = ConflictCancelledMessage,
        };
    }

    /// <summary>保存失败：状态 Failed、记录保留（既有语义）。失败态应用在 UI 线程；异常不静默。</summary>
    private async Task<AnnotationSaveOutcome> FailSubmitAsync(DocSession session, AnnotationSaveBatch batch, string? error)
    {
        session.Session.ApplyFailure();
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                session.Source!.ApplySaveFailure(batch.SubmittedRecords);
                RaiseStateChanged(session.Path, SavePillState.Failed, error);
            }).ConfigureAwait(false);
        }
        catch (Exception applyError)
        {
            // 兜底：即使失败态回发 UI 也失败，仍要保证终态为 Failed 且通知发出（宿主自行 marshal）。
            Trace.WriteLine($"[AnnotationSaveService] 失败态应用异常: {session.Path} ({applyError.Message})");
            RaiseStateChanged(session.Path, SavePillState.Failed, error);
        }

        return AnnotationSaveOutcome.Empty with { RequiresFollowUp = true, Succeeded = false, Error = error };
    }

    /// <summary>带 30s 超时与桥异常归一化的引擎调用（就地保存与另存为路径共用）。</summary>
    private async Task<AnnotationSaveEngineResult> InvokeEngineAsync(
        string path,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
        string? password,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.Timeout);
        try
        {
            return await _engine.SaveAnnotationsAsync(path, actions, password, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AnnotationSaveEngineResult.Timeout(); // 30s 到期，引擎进程树已由桥终止
        }
        catch (OperationCanceledException)
        {
            // 外部取消（关闭窗口等）：不改变会话状态（提交中），交由调用方处理
            RaiseStateChanged(path, SavePillState.Saving, null);
            throw;
        }
        catch (Exception error)
        {
            return AnnotationSaveEngineResult.TransportFailure(error.Message);
        }
    }

    /// <summary>T11：读取基线并与当前 size/mtime 对比；一致或不可读返回 null。</summary>
    private static AnnotationConflict? DetectExternalModification(DocSession session)
    {
        if (session.Baseline is not { } baseline) return null;
        var current = FileStateCapture(session.Path);
        if (current is not { } now) return null; // 文件暂不可读：交由引擎报错，不误判为冲突

        var kind = AnnotationConflictDetector.Compare(baseline, now);
        if (kind == ExternalModificationKind.None) return null;

        return new AnnotationConflict(
            session.Path,
            baseline.Size,
            new DateTimeOffset(baseline.ModifiedTicks, TimeSpan.Zero),
            now.Size,
            new DateTimeOffset(now.ModifiedTicks, TimeSpan.Zero),
            kind);
    }

    /// <summary>取宿主冲突解决选择；未接线时保守取消（绝不静默覆盖外部修改）。</summary>
    private async Task<ConflictResolution> ResolveExternalModificationAsync(
        AnnotationConflict conflict, CancellationToken cancellationToken)
    {
        if (_options.Prompt is null) return ConflictResolution.Cancel;
        return await _options.Prompt
            .ResolveExternalModificationAsync(conflict, cancellationToken)
            .ConfigureAwait(false) ?? ConflictResolution.Cancel;
    }

    /// <summary>请求另存为目标路径；未接线或用户取消返回 null。</summary>
    private async Task<string?> RequestSaveAsPathAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (_options.Prompt is null) return null;
        return await _options.Prompt
            .RequestSaveAsPathAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>另存为前把当前磁盘原文件复制到新路径（不修改原文件）。</summary>
    private static bool TryPrepareSaveAsCopy(DocSession session, string targetPath, out string? error)
    {
        error = null;
        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(session.Path, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"另存为失败：{ex.Message}";
            return false;
        }
    }

    private static bool IsSameFile(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 另存为成功的状态迁移（T11 AC-18.2）：新路径 stamp 作为基线、键迁移（书签/页码随迁）、
    /// 刷新宿主基线并同步最近卡片，最后把会话从旧路径重绑到新路径——后续保存直接写新文件。
    /// </summary>
    private async Task OnSaveAsSuccessAsync(DocSession session, string newPath, CancellationToken cancellationToken)
    {
        var oldPath = session.Path;
        var oldKey = session.BaselineKey;
        var stamp = FileStateCapture(newPath);
        if (stamp is { } value)
        {
            session.Baseline = value;
            session.BaselineKey = value.Key;
            var mtime = new DateTimeOffset(value.ModifiedTicks, TimeSpan.Zero);

            TryInvoke(() => _options.RefreshConflictBaseline?.Invoke(newPath, value.Size, mtime), "刷新冲突基线（另存为）");

            var page = session.Source?.CurrentPageNumber ?? 0;
            var zoom = session.Source?.CurrentZoomPercent ?? 0;
            TryInvoke(() => _options.SyncRecentCard?.Invoke(newPath, value.Size, page, zoom), "同步最近卡片（另存为）");

            if (_options.Migrator is not null && oldKey is not null && oldKey != value.Key)
            {
                try
                {
                    // 本方法在 UI 上下文内被调用；保留上下文以确保会话重绑与后续宿主回调的线程语义。
                    await _options.Migrator.MigrateAsync(oldKey, value.Key).ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    Trace.WriteLine($"[AnnotationSaveService] 另存为后阅读状态键迁移失败: {newPath} ({error.Message})");
                }
            }
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(oldPath, out var existing) && ReferenceEquals(existing, session))
            {
                _sessions.Remove(oldPath);
            }

            session.Path = Path.GetFullPath(newPath);
            _sessions[session.Path] = session;
        }
    }

    private const string ConflictCancelledMessage = "文件已被外部程序修改，本次保存已取消；批注仍保留在叠加层，可稍后重试。";

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
                // 本方法在 UI 上下文内被调用；保留上下文确保其后 (b)(c) 的 UI 回调仍在 UI 线程。
                await _options.Migrator.MigrateAsync(oldKey, ns.Key).ConfigureAwait(true);
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
                    .ConfigureAwait(true);
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
            var task = FlushAsync(session.Path);
            session.LastDebounceTask = task;

            // F3-BLK-1：防抖任务不得静默失败。正常路径在 SubmitAsync 内已归一到 Failed 终态，
            // 此处仅兜底观测意外逃逸的异常（记录诊断，绝不吞掉终端状态）。
            _ = task.ContinueWith(
                faulted => Trace.WriteLine(
                    $"[AnnotationSaveService] 防抖保存任务异常: {session.Path} " +
                    $"({faulted.Exception?.GetBaseException().Message})"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
        };

        // V3 T11（C3）：按类型携带几何。highlight/note 的键与顺序保持不变（V2 契约）。
        switch (record.Kind)
        {
            case AnnotationKind.Note:
                action["kind"] = "note";
                action["text"] = record.Note ?? string.Empty;
                action["noteRect"] = rect;
                break;

            case AnnotationKind.Ink:
                action["kind"] = "ink";
                action["strokes"] = BuildStrokes(record.Strokes);
                action["width"] = record.LineWidth;
                break;

            case AnnotationKind.Shape:
                action["kind"] = "shape";
                action["shape"] = ShapeToken(record.Shape);
                action["width"] = record.LineWidth;
                if (record.LinePoints is { Count: 2 } points)
                {
                    // 线/箭头：两点、起点→终点（引擎据此走 add_line_annot + set_line_ends）。
                    action["points"] = new[]
                    {
                        new[] { points[0].X, points[0].Y },
                        new[] { points[1].X, points[1].Y },
                    };
                }
                break;

            default:
                action["kind"] = "highlight";
                break;
        }

        return action;
    }

    /// <summary>手写笔迹 → 引擎 JSON：[[[x,y], ...], ...]（PDF 点、左上原点）。</summary>
    private static List<object> BuildStrokes(IReadOnlyList<IReadOnlyList<PdfPoint>>? strokes)
    {
        var result = new List<object>();
        if (strokes is null) return result;
        foreach (var stroke in strokes)
        {
            var points = new List<object>(stroke.Count);
            foreach (var point in stroke) points.Add(new[] { point.X, point.Y });
            result.Add(points);
        }
        return result;
    }

    private static string ShapeToken(ShapeKind? shape) => shape switch
    {
        ShapeKind.Square => "square",
        ShapeKind.Circle => "circle",
        ShapeKind.Line => "line",
        ShapeKind.Arrow => "arrow",
        _ => "square",
    };

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

    /// <summary>
    /// F3-BLK-1：把“保存结果应用”调度回 UI 线程。可在 UI 线程内联执行（避免死锁与无谓调度），
    /// 无 UI 上下文（无头单测）时同样内联，保证既有单测可运行；否则 POST 到构造时捕获的
    /// <see cref="_uiContext"/>（WPF 为 DispatcherSynchronizationContext）并等待其完成。
    /// 无上下文但存在 WPF Application 时回退到其 Dispatcher。
    /// </summary>
    private async Task RunOnUiThreadAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var context = _uiContext;
        if (context is not null && !ReferenceEquals(context, SynchronizationContext.Current))
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(
                async _ =>
                {
                    try
                    {
                        await body().ConfigureAwait(true);
                        completion.TrySetResult(null);
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                },
                null);
            await completion.Task.ConfigureAwait(false);
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        // 仅在 Dispatcher 确实存活且未关闭时才走 BeginInvoke：若其归属线程已退出/正在关闭
        // （如测试中在短命 STA 线程上创建过的全局 Application），BeginInvoke 的委托永不执行，
        // completion 永不完成 → 调用方 await 永久挂起。此时直接内联执行，保证有界完成。
        if (dispatcher is not null
            && !dispatcher.CheckAccess()
            && dispatcher.Thread.IsAlive
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished)
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _ = dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await body().ConfigureAwait(true);
                        completion.TrySetResult(null);
                    }
                    catch (Exception error)
                    {
                        completion.TrySetException(error);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // 检查与 BeginInvoke 之间 Dispatcher 关闭：内联，避免永不完成的 await。
                await body().ConfigureAwait(false);
                return;
            }

            await completion.Task.ConfigureAwait(false);
            return;
        }

        await body().ConfigureAwait(false);
    }

    private Task RunOnUiThreadAsync(Action body)
        => RunOnUiThreadAsync(() =>
        {
            body();
            return Task.CompletedTask;
        });

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

        public string Path { get; set; }
        public AnnotationSaveSession Session { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object DebounceLock { get; } = new();
        public Timer? DebounceTimer;
        public long DebounceGeneration;
        public Task? LastDebounceTask;

        public IAnnotationSaveSource? Source;
        public FileStateStamp? Baseline;
        public string? BaselineKey;

        /// <summary>T11 另存为成功后待通知宿主的路径切换（在会话门禁外触发）。</summary>
        public (string OldPath, string NewPath)? PendingSaveAs;

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
