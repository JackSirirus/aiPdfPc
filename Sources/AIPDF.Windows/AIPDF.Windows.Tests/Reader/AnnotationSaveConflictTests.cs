// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T11 外部修改冲突测试：size/mtime 判定、三分支（覆盖/另存为/取消）、基线刷新不误报、
// 目标占用另存为回退、未接线保守取消。
using System.IO;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class AnnotationSaveConflictTests : IDisposable
{
    private readonly string _dir;
    private readonly string _original;
    private readonly string _copy;

    public AnnotationSaveConflictTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aipdf-t11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _original = Path.Combine(_dir, "doc.pdf");
        _copy = Path.Combine(_dir, "doc-copy.pdf");
        File.WriteAllText(_original, "ORIGINAL");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- 纯逻辑：size/mtime 判定

    [Theory]
    [InlineData(10, 100, 20, 100, ExternalModificationKind.SizeChanged)]
    [InlineData(10, 100, 10, 200, ExternalModificationKind.ModifiedTimeChanged)]
    [InlineData(10, 100, 20, 200, ExternalModificationKind.SizeAndModifiedTimeChanged)]
    [InlineData(10, 100, 10, 100, ExternalModificationKind.None)]
    public void Detector_Classifies(
        long baselineSize, long baselineTicks, long currentSize, long currentTicks, ExternalModificationKind expected)
        => Assert.Equal(expected, AnnotationConflictDetector.Compare(baselineSize, baselineTicks, currentSize, currentTicks));

    // ---------------------------------------------------------------- 无冲突不弹

    [Fact]
    public async Task NoExternalModification_DoesNotPrompt_AndSavesInPlace()
    {
        var prompt = new FakePrompt { ConflictResult = _ => throw new Xunit.Sdk.XunitException("size/mtime 一致时不应弹冲突框") };
        var engine = new FakeEngine();
        using var service = new AnnotationSaveService(engine, Options(prompt));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, prompt.ConflictCalls);
        Assert.Equal(1, engine.CallCount);
        Assert.Equal(_original, engine.Paths[0]);
        Assert.Equal(SavePillState.Saved, service.GetState(_original));
    }

    // ---------------------------------------------------------------- 三分支

    [Fact]
    public async Task ExternalModification_Overwrite_ContinuesAtomicInPlaceWrite()
    {
        var prompt = new FakePrompt { ConflictResult = _ => ConflictResolution.Overwrite };
        var engine = new FakeEngine();
        using var service = new AnnotationSaveService(engine, Options(prompt));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);
        MutateOriginal(); // 打开后由外部程序修改

        var outcome = await service.FlushAsync(_original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, prompt.ConflictCalls);
        Assert.NotNull(prompt.LastConflict);
        Assert.NotEqual(ExternalModificationKind.None, prompt.LastConflict!.Kind);
        Assert.Equal(1, engine.CallCount);
        Assert.Equal(_original, engine.Paths[0]); // 覆盖 = 继续就地原子写入
    }

    [Fact]
    public async Task ExternalModification_Cancel_KeepsOverlay_AndDoesNotCallEngine()
    {
        var prompt = new FakePrompt { ConflictResult = _ => ConflictResolution.Cancel };
        var engine = new FakeEngine();
        using var service = new AnnotationSaveService(engine, Options(prompt));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);
        MutateOriginal();

        var outcome = await service.FlushAsync(_original);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.RequiresFollowUp);
        Assert.Equal(0, engine.CallCount);
        Assert.Equal("r1", Assert.Single(source.Records).Id); // 叠加层批注不丢
        Assert.Equal(SavePillState.Failed, service.GetState(_original));
        Assert.True(service.HasPending(_original));
    }

    [Fact]
    public async Task ExternalModification_SaveAs_WritesNewPath_OriginalUntouched_AndRebindsState()
    {
        var prompt = new FakePrompt { ConflictResult = _ => ConflictResolution.SaveAsTo(_copy) };
        var engine = new FakeEngine();
        var savedAsEvents = new List<(string Old, string New)>();
        using var service = new AnnotationSaveService(engine, Options(prompt, (o, n) => savedAsEvents.Add((o, n))));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);
        MutateOriginal();
        var externalContent = File.ReadAllText(_original);

        var outcome = await service.FlushAsync(_original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, engine.CallCount);
        Assert.Equal(_copy, engine.Paths[0]);                       // 引擎写新路径
        Assert.Equal(externalContent, File.ReadAllText(_original)); // 原文件未被触碰
        Assert.True(File.Exists(_copy));
        Assert.Equal(externalContent, File.ReadAllText(_copy));     // 副本是原文件复制

        var evt = Assert.Single(savedAsEvents);
        Assert.Equal(_original, evt.Old);
        Assert.Equal(_copy, evt.New);
        Assert.Equal(SavePillState.Saved, service.GetState(_copy)); // 状态/基线切到新路径
        Assert.False(service.HasPending(_copy));
    }

    // ---------------------------------------------------------------- BLK-1 基线刷新

    [Fact]
    public async Task SelfSaveThenSaveAgain_DoesNotFalselyReportConflict()
    {
        var prompt = new FakePrompt { ConflictResult = _ => throw new Xunit.Sdk.XunitException("AIPDF 自己保存后不应误报外部修改") };
        var engine = new FakeEngine();
        var call = 0;
        engine.Result = (path, _, _, _) =>
        {
            call++;
            if (call == 1)
            {
                // 模拟引擎原子保存：成功路径改变 size/mtime
                File.WriteAllText(path, "AFTER-SAVE");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(20));
            }
            return Task.FromResult(FakeEngine.Ok());
        };
        using var service = new AnnotationSaveService(engine, Options(prompt));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        Assert.True((await service.FlushAsync(_original)).Succeeded);
        Assert.Equal(0, prompt.ConflictCalls);

        source.Records.Add(Highlight("r2"));
        var second = await service.FlushAsync(_original);

        Assert.True(second.Succeeded);
        Assert.Equal(0, prompt.ConflictCalls); // 基线已由保存成功回调刷新
        Assert.Equal(2, engine.CallCount);
    }

    // ---------------------------------------------------------------- T3 target_in_use 另存为回退

    [Fact]
    public async Task TargetInUse_UserChoosesSaveAs_WritesNewPath()
    {
        var prompt = new FakePrompt { SaveAsResult = _ => _copy };
        var engine = new FakeEngine();
        var call = 0;
        engine.Result = (_, _, _, _) => Task.FromResult(++call == 1 ? FakeEngine.TargetInUse() : FakeEngine.Ok());
        var savedAsEvents = new List<(string Old, string New)>();
        using var service = new AnnotationSaveService(engine, Options(prompt, (o, n) => savedAsEvents.Add((o, n))));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(_original, engine.Paths[0]);
        Assert.Equal(_copy, engine.Paths[1]);
        Assert.Equal(1, prompt.SaveAsCalls);
        Assert.Equal(0, prompt.ConflictCalls); // 无 size/mtime 冲突
        Assert.Equal(_copy, Assert.Single(savedAsEvents).New);
        Assert.Equal(SavePillState.Saved, service.GetState(_copy));
    }

    [Fact]
    public async Task TargetInUse_UserCancels_AppliesFailure_KeepsRecords()
    {
        var prompt = new FakePrompt { SaveAsResult = _ => null };
        var engine = new FakeEngine { Result = (_, _, _, _) => Task.FromResult(FakeEngine.TargetInUse()) };
        using var service = new AnnotationSaveService(engine, Options(prompt));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_original);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, engine.CallCount);
        Assert.Equal(1, prompt.SaveAsCalls);
        Assert.Equal("r1", Assert.Single(source.Records).Id);
        Assert.Equal(SavePillState.Failed, service.GetState(_original));
    }

    // ---------------------------------------------------------------- 未接线保守取消

    [Fact]
    public async Task PromptNotWired_ExternalModification_FailSafeCancel_NoSilentOverwrite()
    {
        var engine = new FakeEngine();
        using var service = new AnnotationSaveService(engine, Options(prompt: null));
        var source = MakeSource(Highlight("r1"));
        service.RegisterDocument(source);
        MutateOriginal();

        var outcome = await service.FlushAsync(_original);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, engine.CallCount); // 绝不静默覆盖外部修改
        Assert.Equal("r1", Assert.Single(source.Records).Id);
        Assert.Equal(SavePillState.Failed, service.GetState(_original));
    }

    // ---------------------------------------------------------------- 工具

    private void MutateOriginal()
    {
        File.WriteAllText(_original, "ORIGINAL-EXTENDED");
        File.SetLastWriteTimeUtc(_original, DateTime.UtcNow.AddSeconds(30));
    }

    private static AnnotationRecord Highlight(string id, int page = 0)
        => AnnotationRecord.CreateHighlight(page, new PdfRect(0, 0, 10, 10)) with { Id = id };

    private FakeSource MakeSource(params AnnotationRecord[] records)
    {
        var source = new FakeSource(_original);
        source.Records.AddRange(records);
        return source;
    }

    private static AnnotationSaveOptions Options(
        IAnnotationSavePrompt? prompt,
        Action<string, string>? onSavedAs = null,
        int debounceMs = 100_000)
        => new()
        {
            DebounceMs = debounceMs,
            Timeout = TimeSpan.FromSeconds(5),
            Prompt = prompt,
            OnSavedAs = onSavedAs,
            ReloadAsync = (_, _, _) => Task.CompletedTask,
        };

    private sealed class FakePrompt : IAnnotationSavePrompt
    {
        public Func<AnnotationConflict, ConflictResolution> ConflictResult { get; set; }
            = _ => ConflictResolution.Cancel;

        public Func<string, string?> SaveAsResult { get; set; } = _ => null;

        public int ConflictCalls { get; private set; }

        public int SaveAsCalls { get; private set; }

        public AnnotationConflict? LastConflict { get; private set; }

        public Task<ConflictResolution> ResolveExternalModificationAsync(
            AnnotationConflict conflict, CancellationToken cancellationToken)
        {
            ConflictCalls++;
            LastConflict = conflict;
            return Task.FromResult(ConflictResult(conflict));
        }

        public Task<string?> RequestSaveAsPathAsync(string sourcePath, CancellationToken cancellationToken)
        {
            SaveAsCalls++;
            return Task.FromResult(SaveAsResult(sourcePath));
        }
    }

    private sealed class FakeSource(string path) : IAnnotationSaveSource
    {
        public List<AnnotationRecord> Records { get; } = [];

        public string FilePath { get; } = path;

        public string? Password => null;

        public bool IsReadOnly => false;

        public int CurrentPageNumber => 1;

        public double CurrentZoomPercent => 100;

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

        public List<string> Paths { get; } = [];

        public Func<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>, string?, CancellationToken,
            Task<AnnotationSaveEngineResult>> Result { get; set; }
            = (_, _, _, _) => Task.FromResult(Ok());

        public async Task<AnnotationSaveEngineResult> SaveAnnotationsAsync(
            string path,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
            string? password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Paths.Add(path);
            return await Result(path, actions, password, cancellationToken);
        }

        public static AnnotationSaveEngineResult Ok()
            => new(true, null, null, new Dictionary<string, string>());

        public static AnnotationSaveEngineResult TargetInUse()
            => new(false, "目标文件正被其他程序占用", "target_in_use", new Dictionary<string, string>());
    }
}
