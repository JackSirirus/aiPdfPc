// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4 批注保存状态机纯逻辑测试：防抖代际、状态迁移、H-4 粒度、删除队列、失败保留。
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class AnnotationSaveSessionTests
{
    private static AnnotationRecord Highlight(string id, int page = 0)
        => AnnotationRecord.CreateHighlight(page, new PdfRect(0, 0, 10, 10)) with { Id = id };

    [Fact]
    public void NotifyChanged_ResetsGeneration_AndMarksSaving()
    {
        var session = new AnnotationSaveSession();
        var before = session.Generation;

        session.NotifyChanged();

        Assert.True(session.Generation > before);
        Assert.False(session.IsGenerationCurrent(before));
        Assert.True(session.IsGenerationCurrent(session.Generation));
        Assert.Equal(SavePillState.Saving, session.State);
        Assert.True(session.HasPending);
    }

    [Fact]
    public void NonAnnotationOps_DoNotTouchGeneration()
    {
        var session = new AnnotationSaveSession();
        var generation = session.Generation;

        // 非批注操作不调用 Notify*：读状态 / 查询代际都不改变它
        _ = session.State;
        _ = session.IsGenerationCurrent(generation);
        _ = session.PendingDeleteIds;

        Assert.Equal(generation, session.Generation);
        Assert.Equal(SavePillState.Saved, session.State);
    }

    [Fact]
    public void BuildBatch_NewRecord_IsAddOnly()
    {
        var session = new AnnotationSaveSession();
        var record = Highlight("a");

        var batch = session.BuildBatch([record]);

        Assert.NotNull(batch);
        var action = Assert.Single(batch!.Actions);
        Assert.Equal(AnnotationActionKind.Add, action.Kind);
        Assert.Equal("a", action.Id);
        Assert.Empty(batch.DeletedIds);
    }

    [Fact]
    public void BuildBatch_EmbeddedRecord_EmitsDeleteThenAdd()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();

        // 第一次提交：记录在飞行期间被修改（内容不等）→ 标记为已内嵌
        var original = Highlight("a");
        var batch1 = session.BuildBatch([original])!;
        session.MarkSubmitting();
        var modified = original with { Color = "#00FF00" };
        var outcome = session.ApplySuccess(batch1, [modified]);
        Assert.Empty(outcome.ClearedIds);
        Assert.Contains("a", session.EmbeddedIds);

        var batch2 = session.BuildBatch([modified])!;
        Assert.Equal(2, batch2.Actions.Count);
        Assert.Equal(AnnotationActionKind.Delete, batch2.Actions[0].Kind);
        Assert.Equal(AnnotationActionKind.Add, batch2.Actions[1].Kind);
    }

    [Fact]
    public void NotifyRemoved_Embedded_QueuesDelete_NewRecordDoesNot()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();

        // 让 "a" 成为已内嵌
        var original = Highlight("a");
        var batch = session.BuildBatch([original])!;
        var modified = original with { Color = "#123456" };
        session.ApplySuccess(batch, [modified]);

        session.NotifyRemoved("a");
        Assert.Contains("a", session.PendingDeleteIds);

        session.NotifyRemoved("never-saved");
        Assert.DoesNotContain("never-saved", session.PendingDeleteIds);

        // 仅剩删除动作的批次也应可构建（无记录）
        var deleteBatch = session.BuildBatch([]);
        Assert.NotNull(deleteBatch);
        var deleteAction = Assert.Single(deleteBatch!.Actions);
        Assert.Equal(AnnotationActionKind.Delete, deleteAction.Kind);
    }

    [Fact]
    public void ApplySuccess_ClearsMatched_AndKeepsAddedDuringFlight_H4()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();
        var r1 = Highlight("a");
        var batch = session.BuildBatch([r1])!;
        session.MarkSubmitting();

        // 飞行期间用户又加了一条
        var r2 = Highlight("b", 1);
        var outcome = session.ApplySuccess(batch, [r1, r2]);

        Assert.Equal(["a"], outcome.ClearedIds);
        var retained = Assert.Single(outcome.RetainedRecords);
        Assert.Equal("b", retained.Id);
        Assert.True(outcome.RequiresFollowUp);
        Assert.True(session.HasPending);
    }

    [Fact]
    public void ApplySuccess_ModifiedDuringFlight_KeepsNewVersion()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();
        var r1 = Highlight("a");
        var batch = session.BuildBatch([r1])!;
        session.MarkSubmitting();

        var changed = r1 with { Color = "#FF0000" };
        var outcome = session.ApplySuccess(batch, [changed]);

        Assert.Empty(outcome.ClearedIds);
        Assert.Equal("a", Assert.Single(outcome.RetainedRecords).Id);
        Assert.Equal("#FF0000", retainedColor(outcome));
        Assert.True(outcome.RequiresFollowUp);

        static string? retainedColor(AnnotationSaveOutcome o) => o.RetainedRecords[0].Color;
    }

    [Fact]
    public void ApplySuccess_DeletedDuringFlight_QueuesCompensatingDelete()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();
        var r1 = Highlight("a");
        var batch = session.BuildBatch([r1])!;
        session.MarkSubmitting();

        var outcome = session.ApplySuccess(batch, []); // 飞行期间被删除

        Assert.Empty(outcome.ClearedIds);
        Assert.True(outcome.RequiresFollowUp);
        Assert.Contains("a", session.PendingDeleteIds);
    }

    [Fact]
    public void ApplyFailure_KeepsPending_AndSuggestsSaveAsAfterThree()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();

        for (var i = 0; i < 3; i++)
        {
            session.BuildBatch([Highlight("a")]);
            session.ApplyFailure();
        }

        Assert.Equal(3, session.ConsecutiveFailures);
        Assert.True(session.ShouldSuggestSaveAs);
        Assert.Equal(SavePillState.Failed, session.State);
        Assert.True(session.HasPending);
    }

    [Fact]
    public void ApplySuccess_ResetsFailureCounter()
    {
        var session = new AnnotationSaveSession();
        session.NotifyChanged();
        session.ApplyFailure();
        Assert.Equal(1, session.ConsecutiveFailures);

        var r1 = Highlight("a");
        var batch = session.BuildBatch([r1])!;
        session.ApplySuccess(batch, [r1]);

        Assert.Equal(0, session.ConsecutiveFailures);
        Assert.Equal(SavePillState.Saved, session.State);
    }

    [Fact]
    public void MarkReadOnly_IsSticky()
    {
        var session = new AnnotationSaveSession();
        session.MarkReadOnly();

        Assert.Equal(SavePillState.ReadOnly, session.State);
        session.NotifyChanged();
        Assert.Equal(SavePillState.ReadOnly, session.State);
    }
}
