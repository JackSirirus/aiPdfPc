// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4 批注保存状态机（纯逻辑，无 PDFium / 无 IO / 无计时器）。
//
// 职责：
//  - 防抖代际戳（新批注操作重置；非批注操作不触碰）；
//  - savepill 状态（Saved/Saving/Failed/ReadOnly，FR-19/ST-08/20/21/22）；
//  - per-annotation Saved 粒度（H-4）：保存期间新增/修改的批注不得被成功回调清掉；
//  - 已内嵌 id 账本 + 挂起删除队列：决定 add / delete+add / delete 动作。
//
// 计时与 IO 在 AnnotationSaveService；本类可 xunit 直测（见 AnnotationSaveSessionTests）。
using System.Linq;

namespace AIPDF.Windows.Annotations;

/// <summary>savepill 四态（PRD 3.5.7 FR-19；ST-08/20/21/22）。</summary>
public enum SavePillState
{
    /// <summary>已保存（无挂起操作）。</summary>
    Saved,

    /// <summary>保存中（含防抖等待期）。</summary>
    Saving,

    /// <summary>保存失败，可点击重试（FR-16）。</summary>
    Failed,

    /// <summary>文档只读，无法就地保存（ST-08）。</summary>
    ReadOnly,
}

/// <summary>批注变化类别（T2 操作 → T4 保存链路的事件载荷）。</summary>
public enum AnnotationChangeKind
{
    Added,
    Updated,
    Removed,
}

/// <summary>批注变化事件参数：携带变化类别与记录快照（Removed 为移除前的记录）。</summary>
public sealed class AnnotationChangedEventArgs : EventArgs
{
    public AnnotationChangedEventArgs(AnnotationChangeKind kind, AnnotationRecord record)
    {
        Kind = kind;
        Record = record;
    }

    public AnnotationChangeKind Kind { get; }

    public AnnotationRecord Record { get; }
}

/// <summary>提交给引擎的批注动作类别。</summary>
public enum AnnotationActionKind
{
    Add,
    Delete,
    SetColor,
}

/// <summary>单条引擎批注动作。<see cref="Record"/> 仅 Add/SetColor 携带。</summary>
public sealed record AnnotationSaveAction(AnnotationActionKind Kind, string Id, AnnotationRecord? Record = null);

/// <summary>一次提交的批注批次快照。</summary>
/// <param name="Actions">按顺序提交的引擎动作（delete+add 成对时 delete 在前）。</param>
/// <param name="SubmittedRecords">提交的客户端记录快照（成功回调据此判定 H-4 粒度）。</param>
/// <param name="DeletedIds">本批包含的删除（成功回调只清本批，保留飞行期间新排队者）。</param>
/// <param name="Generation">提交时的防抖代际戳。</param>
public sealed record AnnotationSaveBatch(
    IReadOnlyList<AnnotationSaveAction> Actions,
    IReadOnlyList<AnnotationRecord> SubmittedRecords,
    IReadOnlyList<string> DeletedIds,
    long Generation);

/// <summary>保存成功后的清理结果。</summary>
/// <param name="ClearedIds">应从客户端模型移除的 id（已写入 PDF，改由内嵌注释渲染）。</param>
/// <param name="RetainedRecords">保留的未保存记录（飞行期间新增/修改）。</param>
/// <param name="RequiresFollowUp">是否仍有挂起操作（需重启防抖）。</param>
public sealed record AnnotationSaveOutcome(
    IReadOnlyList<string> ClearedIds,
    IReadOnlyList<AnnotationRecord> RetainedRecords,
    bool RequiresFollowUp)
{
    /// <summary>引擎提交是否成功（false = 失败/超时；关闭拦截据此判定）。</summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>失败诊断（成功为 null）。</summary>
    public string? Error { get; init; }

    public static AnnotationSaveOutcome Empty { get; } = new([], [], false);
}

/// <summary>
/// 单文档批注保存状态机。线程安全（内部 <c>_gate</c>）。所有方法幂等、无阻塞 IO。
/// </summary>
public sealed class AnnotationSaveSession
{
    /// <summary>连续失败达到该次数后建议「另存为」（PRD 3.5.4 FR-16）。</summary>
    public const int SaveAsSuggestionThreshold = 3;

    private readonly object _gate = new();
    private readonly HashSet<string> _embeddedIds = new(StringComparer.Ordinal);
    private readonly List<string> _pendingDeletes = [];
    private long _generation;
    private int _consecutiveFailures;
    private bool _hasPending;
    private SavePillState _state = SavePillState.Saved;

    /// <summary>当前 savepill 状态。</summary>
    public SavePillState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>连续失败次数（成功归零）。</summary>
    public int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutiveFailures; }
    }

    /// <summary>是否建议「另存为」（连续失败 ≥ 3）。</summary>
    public bool ShouldSuggestSaveAs => ConsecutiveFailures >= SaveAsSuggestionThreshold;

    /// <summary>是否存在未保存操作（新增/修改/删除）。</summary>
    public bool HasPending
    {
        get { lock (_gate) return _hasPending; }
    }

    /// <summary>防抖代际戳（Interlocked 读，供 Timer 判定是否过期）。</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>该代际戳是否仍为最新（新批注操作会使其过期）。</summary>
    public bool IsGenerationCurrent(long generation) => generation == Generation;

    /// <summary>
    /// 批注新增/修改：置挂起、状态 Saving、代际戳 +1（重置防抖窗口）。
    /// 只读文档不改变状态（仍只读）。
    /// </summary>
    public void NotifyChanged()
    {
        lock (_gate)
        {
            _generation++;
            _hasPending = true;
            if (_state != SavePillState.ReadOnly) _state = SavePillState.Saving;
        }
    }

    /// <summary>
    /// 批注被客户端移除：若该 id 已内嵌 PDF → 排队删除；否则仅取消未保存的新增（无引擎动作）。
    /// 随后与新增同样重置防抖。
    /// </summary>
    public void NotifyRemoved(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (_embeddedIds.Remove(id) && !_pendingDeletes.Contains(id))
            {
                _pendingDeletes.Add(id);
            }

            _generation++;
            _hasPending = true;
            if (_state != SavePillState.ReadOnly) _state = SavePillState.Saving;
        }
    }

    /// <summary>当前挂起的删除 id（诊断/测试）。</summary>
    public IReadOnlyList<string> PendingDeleteIds
    {
        get { lock (_gate) return [.. _pendingDeletes]; }
    }

    /// <summary>当前已内嵌 id（诊断/测试）。</summary>
    public IReadOnlyList<string> EmbeddedIds
    {
        get { lock (_gate) return [.. _embeddedIds]; }
    }

    /// <summary>标记只读（ST-08）；不覆盖已有挂起（只读文档本就不该有挂起）。</summary>
    public void MarkReadOnly()
    {
        lock (_gate) _state = SavePillState.ReadOnly;
    }

    /// <summary>无挂起时把状态复位为 Saved（服务在空批次 flush 后调用）。</summary>
    public void MarkSavedIfClean()
    {
        lock (_gate)
        {
            if (_hasPending || _state == SavePillState.ReadOnly) return;
            _state = SavePillState.Saved;
        }
    }

    /// <summary>
    /// 由当前客户端记录构建提交批次：
    ///  - 每条记录：若已内嵌（曾保存过）→ <c>delete</c> + <c>add</c>（覆盖旧版本）；否则仅 <c>add</c>；
    ///  - 追加挂起删除动作。
    /// 无任何动作返回 null。
    /// </summary>
    public AnnotationSaveBatch? BuildBatch(IReadOnlyList<AnnotationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var actions = new List<AnnotationSaveAction>();
            var submitted = new List<AnnotationRecord>(records.Count);
            foreach (var record in records)
            {
                if (_embeddedIds.Contains(record.Id))
                {
                    actions.Add(new AnnotationSaveAction(AnnotationActionKind.Delete, record.Id));
                }

                actions.Add(new AnnotationSaveAction(AnnotationActionKind.Add, record.Id, record));
                submitted.Add(record);
            }

            var deletedIds = new List<string>(_pendingDeletes);
            foreach (var id in _pendingDeletes)
            {
                actions.Add(new AnnotationSaveAction(AnnotationActionKind.Delete, id));
            }

            if (actions.Count == 0) return null;
            return new AnnotationSaveBatch(actions, submitted, deletedIds, _generation);
        }
    }

    /// <summary>提交在途：状态保持 Saving。</summary>
    public void MarkSubmitting()
    {
        lock (_gate)
        {
            if (_state != SavePillState.ReadOnly) _state = SavePillState.Saving;
        }
    }

    /// <summary>
    /// 保存成功回调（H-4 核心）：
    ///  - 提交快照中「仍存在且内容未变」的记录 → 清理（ClearedIds）；
    ///  - 「飞行期间被修改」的记录 → 保留（RetainedRecords，下轮 delete+add）；
    ///  - 「飞行期间被删除」的记录 → 记入内嵌账本并排队补删；
    ///  - 「飞行期间新增」的记录 → 保留；
    ///  - 仅清理本批包含的删除队列，飞行期间新排队者保留。
    /// 连续失败归零。仍有保留/挂起 → RequiresFollowUp=true。
    /// </summary>
    public AnnotationSaveOutcome ApplySuccess(AnnotationSaveBatch batch, IReadOnlyList<AnnotationRecord> currentRecords)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(currentRecords);

        lock (_gate)
        {
            var cleared = new List<string>();
            var retained = new List<AnnotationRecord>();
            var followUp = false;

            foreach (var id in batch.DeletedIds)
            {
                _pendingDeletes.Remove(id);
            }

            foreach (var submitted in batch.SubmittedRecords)
            {
                var current = currentRecords.FirstOrDefault(r => r.Id == submitted.Id);
                if (current is null)
                {
                    // 飞行期间被删除：提交的版本已写入 PDF，需补删。
                    _embeddedIds.Add(submitted.Id);
                    if (!_pendingDeletes.Contains(submitted.Id)) _pendingDeletes.Add(submitted.Id);
                    followUp = true;
                }
                else if (current.ContentEquals(submitted))
                {
                    // 与提交时一致：已内嵌，从客户端模型移除（改由 PDF 内嵌注释渲染）。
                    _embeddedIds.Remove(submitted.Id);
                    cleared.Add(submitted.Id);
                }
                else
                {
                    // 飞行期间被修改：PDF 内为旧版本，客户端保留新版本，下轮 delete+add。
                    _embeddedIds.Add(submitted.Id);
                    retained.Add(current);
                    followUp = true;
                }
            }

            var submittedIds = batch.SubmittedRecords.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var current in currentRecords)
            {
                if (submittedIds.Contains(current.Id)) continue;
                retained.Add(current); // 飞行期间新增
                followUp = true;
            }

            if (_pendingDeletes.Count > 0) followUp = true;

            _consecutiveFailures = 0;
            _hasPending = followUp;
            if (_state != SavePillState.ReadOnly) _state = followUp ? SavePillState.Saving : SavePillState.Saved;
            return new AnnotationSaveOutcome(cleared, retained, followUp);
        }
    }

    /// <summary>
    /// 保存失败回调：连续失败 +1；挂起保留（叠加层不清、删除队列不丢）；状态 Failed。
    /// 提交的记录仍留在客户端模型（服务不清理）。
    /// </summary>
    public void ApplyFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            _hasPending = true;
            if (_state != SavePillState.ReadOnly) _state = SavePillState.Failed;
        }
    }

    /// <summary>
    /// 冲突对话框「取消」（T11 FR-18）：不写盘，批注叠加层/挂起全部保留，状态 Failed（savepill 可点击重试）。
    /// 取消是用户选择而非失败，故不计入连续失败，避免误触发「建议另存为」。
    /// </summary>
    public void ApplyConflictCancelled()
    {
        lock (_gate)
        {
            _hasPending = true;
            if (_state != SavePillState.ReadOnly) _state = SavePillState.Failed;
        }
    }
}
