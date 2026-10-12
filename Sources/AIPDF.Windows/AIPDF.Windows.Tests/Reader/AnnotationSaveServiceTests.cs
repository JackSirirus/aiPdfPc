// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4 批注保存服务测试：防抖合并/重置、失败保留、30s 超时、键迁移/基线刷新/卡片同步、只读、关闭决策。
using System.Diagnostics;
using System.IO;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class AnnotationSaveServiceTests : IDisposable
{
    private readonly string _temp;

    public AnnotationSaveServiceTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "aipdf-t4-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(_temp, "AAAA");
    }

    public void Dispose()
    {
        try { File.Delete(_temp); } catch (IOException) { }
    }

    private static AnnotationRecord Highlight(string id, int page = 0)
        => AnnotationRecord.CreateHighlight(page, new PdfRect(0, 0, 10, 10)) with { Id = id };

    private FakeSource MakeSource(params AnnotationRecord[] records)
    {
        var source = new FakeSource(_temp);
        source.Records.AddRange(records);
        return source;
    }

    private static AnnotationSaveOptions Options(
        IReadingStateMigrator? migrator = null,
        Action<string, long, DateTimeOffset>? baseline = null,
        Action<string, long, int, double>? card = null,
        Action<string>? reload = null,
        int debounceMs = 100_000,
        TimeSpan? timeout = null,
        Func<string, bool>? isReadOnly = null)
        => new()
        {
            DebounceMs = debounceMs,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            Migrator = migrator,
            RefreshConflictBaseline = baseline,
            SyncRecentCard = card,
            ReloadAsync = (path, _, _) =>
            {
                reload?.Invoke(path);
                return Task.CompletedTask;
            },
            IsReadOnly = isReadOnly,
        };

    // ---------------------------------------------------------------- 成功迁移 / 基线 / 卡片

    [Fact]
    public async Task SaveSuccess_MigratesKey_RefreshesBaseline_SyncsCard_AndReloads()
    {
        var migrator = new NullReadingStateMigrator();
        var baselines = new List<(string Path, long Size, DateTimeOffset Mtime)>();
        var cards = new List<(string Path, long Bytes, int Page, double Zoom)>();
        var reloads = new List<string>();
        var engine = new FakeEngine();

        using var service = new AnnotationSaveService(engine,
            Options(migrator, (p, s, m) => baselines.Add((p, s, m)), (p, b, pg, z) => cards.Add((p, b, pg, z)),
                p => reloads.Add(p)));
        var source = MakeSource(Highlight("r1"));
        source.CurrentPageNumber = 7;
        source.CurrentZoomPercent = 133;
        service.RegisterDocument(source);

        var before = ReadingStateKey.TryCapture(_temp)!.Value;

        // 模拟引擎就地保存：真正改变 size/mtime 发生在引擎调用内（T11 冲突检测要求提交前基线一致）。
        engine.Result = (_, _, _, _) =>
        {
            File.WriteAllText(_temp, "BBBBBBBB");
            File.SetLastWriteTimeUtc(_temp, DateTime.UtcNow.AddSeconds(5));
            return Task.FromResult(new AnnotationSaveEngineResult(true, null, null, new Dictionary<string, string>()));
        };

        var outcome = await service.FlushAsync(_temp);

        var after = ReadingStateKey.TryCapture(_temp)!.Value;
        Assert.NotEqual(before.Key, after.Key);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, engine.CallCount);
        Assert.NotNull(migrator.LastMigration);
        Assert.Equal(before.Key, migrator.LastMigration!.Value.OldKey);
        Assert.Equal(after.Key, migrator.LastMigration.Value.NewKey);

        var baseline = Assert.Single(baselines);
        Assert.Equal(after.Size, baseline.Size);
        Assert.Equal(new DateTimeOffset(after.ModifiedTicks, TimeSpan.Zero), baseline.Mtime);

        var card = Assert.Single(cards);
        Assert.Equal(after.Size, card.Bytes);
        Assert.Equal(7, card.Page);
        Assert.Equal(133, card.Zoom);

        Assert.Single(reloads);
        Assert.Empty(source.Records); // r1 已内嵌并清理
        Assert.Equal(SavePillState.Saved, service.GetState(_temp));
    }

    [Fact]
    public async Task SaveSuccess_KeepsAnnotationAddedDuringFlight()
    {
        var engine = new FakeEngine();
        var source = MakeSource(Highlight("r1"));
        engine.Result = (_, _, _, _) =>
        {
            source.Records.Add(Highlight("r2")); // 飞行期间新增
            return Task.FromResult(new AnnotationSaveEngineResult(true, null, null, new Dictionary<string, string>()));
        };

        using var service = new AnnotationSaveService(engine, Options(reload: _ => { }));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["r1"], outcome.ClearedIds);
        Assert.Equal("r2", Assert.Single(source.Records).Id);
        Assert.True(service.HasPending(_temp)); // 仍有未保存 → 重启防抖
        Assert.Equal(SavePillState.Saving, service.GetState(_temp));
    }

    // ---------------------------------------------------------------- 失败 / 超时 / 只读

    [Fact]
    public async Task SaveFailure_KeepsRecords_AndReportsFailed()
    {
        var engine = new FakeEngine
        {
            Result = (_, _, _, _) => Task.FromResult(
                new AnnotationSaveEngineResult(false, "文件被占用", "target_in_use", new Dictionary<string, string>())),
        };
        using var service = new AnnotationSaveService(engine, Options(reload: _ => { }));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        for (var i = 0; i < 3; i++)
        {
            var outcome = await service.FlushAsync(_temp);
            Assert.False(outcome.Succeeded);
            Assert.True(outcome.RequiresFollowUp);
        }

        Assert.Equal(3, engine.CallCount);
        Assert.Equal("r1", Assert.Single(source.Records).Id); // 失败保留叠加层/记录
        Assert.Equal(SavePillState.Failed, service.GetState(_temp));
    }

    [Fact]
    public async Task SaveTimeout_MarksFailed_AndDoesNotClearRecords()
    {
        var engine = new FakeEngine
        {
            Result = async (_, _, _, ct) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
                return new AnnotationSaveEngineResult(true, null, null, new Dictionary<string, string>());
            },
        };
        using var service = new AnnotationSaveService(engine,
            Options(reload: _ => { }, timeout: TimeSpan.FromMilliseconds(150)));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        var sw = Stopwatch.StartNew();
        var outcome = await service.FlushAsync(_temp);

        Assert.False(outcome.Succeeded);
        Assert.Contains("超时", outcome.Error ?? "", StringComparison.Ordinal);
        Assert.Equal(SavePillState.Failed, service.GetState(_temp));
        Assert.Single(source.Records);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadOnlyDocument_ReportsReadOnly_AndSkipsEngine()
    {
        var engine = new FakeEngine();
        var source = MakeSource(Highlight("r1"));
        using var service = new AnnotationSaveService(engine, Options(isReadOnly: _ => true));
        service.RegisterDocument(source);

        Assert.Equal(SavePillState.ReadOnly, service.GetState(_temp));
        var outcome = await service.FlushAsync(_temp);
        Assert.Equal(0, engine.CallCount);
        Assert.True(outcome.Succeeded);
    }

    // ---------------------------------------------------------------- T9 密码传递

    [Fact]
    public async Task Save_ForwardsSourcePasswordToEngine()
    {
        var engine = new FakeEngine();
        var source = MakeSource(Highlight("r1"));
        source.Password = "session-pw"; // T9 驻留密码（会话内存）；保存时逐请求传给引擎
        using var service = new AnnotationSaveService(engine, Options(reload: _ => { }));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);

        Assert.True(outcome.Succeeded);
        Assert.Equal("session-pw", engine.LastPassword);
    }

    // ---------------------------------------------------------------- 防抖

    [Fact]
    public async Task Debounce_CoalescesRapidChanges_AndResetsOnNewChange()
    {
        var engine = new FakeEngine();
        using var service = new AnnotationSaveService(engine, Options(reload: _ => { }, debounceMs: 150));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        service.NotifyAnnotationsChanged(_temp);
        await Task.Delay(40);
        source.Records.Add(Highlight("r2"));      // 防抖窗口内新增
        service.NotifyAnnotationsChanged(_temp);  // 重置窗口

        await WaitUntilAsync(() => engine.CallCount >= 1, "首次防抖保存应触发");
        await Task.Delay(120); // 观察是否被合并为一次
        Assert.Equal(1, engine.CallCount);
        Assert.Equal(2, engine.LastActionCount);  // r1 + r2 合并同批

        source.Records.Add(Highlight("r3"));
        service.NotifyAnnotationsChanged(_temp);
        await WaitUntilAsync(() => engine.CallCount >= 2, "第二次防抖保存应触发");
        Assert.Equal(2, engine.CallCount);
    }

    // ---------------------------------------------------------------- 关闭拦截决策

    [Theory]
    [InlineData(true, true, CloseFlushDecision.CloseNow)]
    [InlineData(false, false, CloseFlushDecision.CloseNow)]
    [InlineData(true, false, CloseFlushDecision.Blocked)]
    public void CloseFlushDecision_Evaluates(bool hasUnsaved, bool succeeded, CloseFlushDecision expected)
        => Assert.Equal(expected, CloseFlushDecisionLogic.Decide(hasUnsaved, succeeded));

    // ---------------------------------------------------------------- 工具

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Xunit.Sdk.XunitException($"{message}（等待 {timeoutMs}ms 超时）");
            await Task.Delay(25);
        }
    }

    // ---------------------------------------------------------------- V4 T2：未保存判据

    [Fact]
    public async Task PendingDelete_KeepsHasPendingTrue_EvenWhenClientRecordsAreEmpty()
    {
        // V4 T2（Oracle 高危 #6）：存在**挂起删除**时客户端记录数可能为 0——若标签红点判据只看
        // 记录数（如 GetAllRecords().Count）就永远看不到标记。判据必须是保存服务的 HasPending
        // （它覆盖「新增/修改/**删除**」三种未保存操作），标签红点与「关闭前 flush」都依赖它。
        //
        // 「已内嵌」的构造沿用 AnnotationSaveSessionTests 的语义：先提交一版，让记录在飞行期间
        // 被修改 → PDF 内为旧版、客户端保留新版，该 id 进入 _embeddedIds（待 delete+add），
        // 此时删除它才会排队成「挂起删除」。
        var engine = new FakeEngine();
        var source = MakeSource(Highlight("h1"));
        engine.Result = (_, _, _, _) =>
        {
            source.Records[0] = source.Records[0] with { Color = "#123456" }; // 飞行期间被修改
            return Task.FromResult(new AnnotationSaveEngineResult(true, null, null, new Dictionary<string, string>()));
        };

        using var service = new AnnotationSaveService(engine, Options());
        service.RegisterDocument(source);

        var first = await service.FlushAsync(_temp);
        Assert.True(first.Succeeded);
        Assert.True(first.RequiresFollowUp);   // 需补 delete+add
        Assert.Single(source.Records);         // 客户端保留新版
        Assert.True(service.HasPending(_temp));

        // 删除该批注：客户端记录归零，但必须仍报「未保存」——
        // 这正是标签红点判据不能用「记录数」的原因（挂起删除时记录数为 0）。
        service.NotifyAnnotationRemoved(_temp, source.Records[0]);
        source.Records.Clear();
        Assert.Empty(source.Records);
        Assert.True(service.HasPending(_temp), "客户端记录为空时仍须报「未保存」（否则标签无红点）");

        // 仅剩删除动作的批次仍可构建并下发（会话层语义已有独立用例覆盖，此处锁服务层可达性）
        Assert.NotEqual(SavePillState.Saved, service.GetState(_temp));
    }

    private sealed class FakeSource(string path) : IAnnotationSaveSource
    {
        public List<AnnotationRecord> Records { get; } = [];

        public string FilePath { get; } = path;

        public string? Password { get; set; }

        public bool IsReadOnly { get; set; }

        public int CurrentPageNumber { get; set; } = 1;

        public double CurrentZoomPercent { get; set; } = 100;

        public IReadOnlyList<AnnotationRecord> GetDirtyRecords() => Records.ToList();

        public void ApplySaveSuccess(IReadOnlyList<string> clearedIds, IReadOnlyList<AnnotationRecord> retainedRecords)
            => Records.RemoveAll(r => clearedIds.Contains(r.Id));

        public void ApplySaveFailure(IReadOnlyList<AnnotationRecord> submittedRecords)
        {
            // 失败保留
        }
    }

    private sealed class FakeEngine : IAnnotationSaveEngine
    {
        public int CallCount { get; private set; }

        public int LastActionCount { get; private set; }

        public string? LastPassword { get; private set; }

        public Func<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>, string?, CancellationToken,
            Task<AnnotationSaveEngineResult>> Result { get; set; } =
            (_, _, _, _) => Task.FromResult(
                new AnnotationSaveEngineResult(true, null, null, new Dictionary<string, string>()));

        public async Task<AnnotationSaveEngineResult> SaveAnnotationsAsync(
            string path,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
            string? password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastActionCount = actions.Count;
            LastPassword = password;
            return await Result(path, actions, password, cancellationToken);
        }
    }
}
