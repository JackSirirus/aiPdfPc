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

        // 模拟就地保存改变 size/mtime
        File.WriteAllText(_temp, "BBBBBBBB");
        File.SetLastWriteTimeUtc(_temp, DateTime.UtcNow.AddSeconds(5));
        var after = ReadingStateKey.TryCapture(_temp)!.Value;
        Assert.NotEqual(before.Key, after.Key);

        var outcome = await service.FlushAsync(_temp);

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
            return await Result(path, actions, password, cancellationToken);
        }
    }
}
