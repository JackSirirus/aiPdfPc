// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T11（C3）批注保存序列化测试：ink/shape 动作携带几何 + color + 线宽；
// highlight/note 动作键保持 V2 契约不变。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class AnnotationSaveSerializationTests : IDisposable
{
    private readonly string _temp;

    public AnnotationSaveSerializationTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "aipdf-t11-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(_temp, "AAAA");
    }

    public void Dispose()
    {
        try { File.Delete(_temp); } catch (IOException) { }
    }

    [Fact]
    public async Task BuildActions_CarryInkAndShapeGeometry_AndKeepHighlightNoteContract()
    {
        var engine = new CapturingEngine();
        var source = new FakeSource(_temp);
        source.Records.AddRange(
        [
            AnnotationRecord.CreateInk(
                0,
                new IReadOnlyList<PdfPoint>[]
                {
                    new[] { new PdfPoint(10, 20), new PdfPoint(30, 40) },
                    new[] { new PdfPoint(50, 60), new PdfPoint(70, 80) },
                },
                "#0000FF", 3) with { Id = "ink" },
            AnnotationRecord.CreateShape(1, ShapeKind.Circle, new PdfRect(1, 2, 30, 40), "#00FF00", 2) with { Id = "circle" },
            AnnotationRecord.CreateShape(2, ShapeKind.Arrow, new PdfPoint(5, 6), new PdfPoint(50, 60), "#E11D48", 4) with { Id = "arrow" },
            AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 10, 10)) with { Id = "hl" },
            AnnotationRecord.CreateNote(0, new PdfRect(0, 0, 10, 10), "hello") with { Id = "note" },
        ]);

        using var service = new AnnotationSaveService(engine, new AnnotationSaveOptions { DebounceMs = 100_000 });
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);
        Assert.True(outcome.Succeeded);

        var adds = engine.Actions
            .Where(a => (string?)a["action"] == "add")
            .ToDictionary(a => (string)a["id"]!);

        // ink：多段笔迹 + 颜色 + 线宽
        var ink = adds["ink"];
        Assert.Equal("ink", ink["kind"]);
        Assert.Equal("#0000FF", ink["color"]);
        Assert.Equal(3.0, ink["width"]);
        var strokes = Assert.IsType<List<object>>(ink["strokes"]);
        Assert.Equal(2, strokes.Count);
        var firstStroke = Assert.IsType<List<object>>(strokes[0]);
        Assert.Equal(new[] { 10.0, 20.0 }, Assert.IsType<double[]>(firstStroke[0]));
        Assert.Equal(new[] { 30.0, 40.0 }, Assert.IsType<double[]>(firstStroke[1]));

        // 圆：rect 几何 + shape token
        var circle = adds["circle"];
        Assert.Equal("shape", circle["kind"]);
        Assert.Equal("circle", circle["shape"]);
        Assert.Equal(2.0, circle["width"]);
        Assert.Equal(new[] { 1.0, 2.0, 30.0, 40.0 }, Assert.IsType<double[]>(circle["rect"]));
        Assert.False(circle.ContainsKey("points"));

        // 箭头：两点（方向 = 起点→终点）+ shape token
        var arrow = adds["arrow"];
        Assert.Equal("shape", arrow["kind"]);
        Assert.Equal("arrow", arrow["shape"]);
        Assert.Equal(4.0, arrow["width"]);
        var points = Assert.IsType<double[][]>(arrow["points"]);
        Assert.Equal(new[] { 5.0, 6.0 }, points[0]);
        Assert.Equal(new[] { 50.0, 60.0 }, points[1]);

        // V2 契约不回归
        Assert.Equal("highlight", adds["hl"]["kind"]);
        Assert.False(adds["hl"].ContainsKey("strokes"));
        Assert.Equal("note", adds["note"]["kind"]);
        Assert.Equal("hello", adds["note"]["text"]);
        Assert.Equal(new[] { 0.0, 0.0, 10.0, 10.0 }, Assert.IsType<double[]>(adds["note"]["noteRect"]));
    }

    [Fact]
    public async Task BuildActions_CarryPerLineQuads_AndTextMarkupKinds()
    {
        var engine = new CapturingEngine();
        var source = new FakeSource(_temp);
        source.Records.AddRange(
        [
            AnnotationRecord.CreateTextMarkup(AnnotationKind.Underline, 0,
            [
                new PdfRect(72, 100, 300, 114),
                new PdfRect(72, 180, 300, 194),
            ]) with { Id = "ul" },
            AnnotationRecord.CreateTextMarkup(AnnotationKind.Strikeout, 1,
            [
                new PdfRect(72, 260, 300, 274),
                new PdfRect(72, 300, 300, 314),
            ]) with { Id = "so" },
            AnnotationRecord.CreateHighlight(2, new PdfRect(10, 20, 110, 40)) with { Id = "hl-single" },
        ]);

        using var service = new AnnotationSaveService(engine, new AnnotationSaveOptions { DebounceMs = 100_000 });
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);
        Assert.True(outcome.Succeeded);

        var adds = engine.Actions
            .Where(a => (string?)a["action"] == "add")
            .ToDictionary(a => (string)a["id"]!);

        // 下划线：kind token + 逐行 quads（这是修复「多行退化成跨行实心带」的关键载荷）
        var underline = adds["ul"];
        Assert.Equal("underline", underline["kind"]);
        Assert.Equal("#FFFF00", underline["color"]);
        var quads = Assert.IsType<double[][]>(underline["quads"]);
        Assert.Equal(2, quads.Length);
        Assert.Equal(new[] { 72.0, 100.0, 300.0, 114.0 }, quads[0]);
        Assert.Equal(new[] { 72.0, 180.0, 300.0, 194.0 }, quads[1]);
        // rect 仍下发（并集），保持既有键与语义
        Assert.Equal(new[] { 72.0, 100.0, 300.0, 194.0 }, Assert.IsType<double[]>(underline["rect"]));

        // 删除线：kind token + 逐行 quads
        var strikeout = adds["so"];
        Assert.Equal("strikeout", strikeout["kind"]);
        Assert.Equal(2, Assert.IsType<double[][]>(strikeout["quads"]).Length);
        // 单矩形批注不携带 quads（V2 载荷不变，几何退化为 rect）
        Assert.False(adds["hl-single"].ContainsKey("quads"));
        Assert.Equal(new[] { 10.0, 20.0, 110.0, 40.0 }, Assert.IsType<double[]>(adds["hl-single"]["rect"]));
    }

    [Fact]
    public async Task BuildActions_UnknownKind_FailsInsteadOfSilentlyBecomingHighlight()
    {
        var engine = new CapturingEngine();
        var source = new FakeSource(_temp);
        source.Records.Add(
            AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 10, 10)) with
            {
                Id = "weird",
                Kind = (AnnotationKind)999,
            });

        using var service = new AnnotationSaveService(engine, new AnnotationSaveOptions { DebounceMs = 100_000 });
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);

        // 原实现把任何未知 Kind 静默写成 highlight 并返回成功；这里要求显式失败。
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain(engine.Actions, a => (string?)a["kind"] == "highlight");
    }

    [Fact]
    public async Task BuildActions_CarryStampTokenOrImage()
    {
        var engine = new CapturingEngine();
        var source = new FakeSource(_temp);
        source.Records.AddRange(
        [
            AnnotationRecord.CreateStamp(0, new PdfRect(10, 20, 210, 80), "approved") with { Id = "stamp-preset" },
            AnnotationRecord.CreateStamp(1, new PdfRect(10, 20, 210, 80), image: [0x89, 0x50, 0x4E, 0x47])
                with { Id = "stamp-image" },
        ]);

        using var service = new AnnotationSaveService(engine, new AnnotationSaveOptions { DebounceMs = 100_000 });
        service.RegisterDocument(source);

        var outcome = await service.FlushAsync(_temp);
        Assert.True(outcome.Succeeded);

        var adds = engine.Actions
            .Where(a => (string?)a["action"] == "add")
            .ToDictionary(a => (string)a["id"]!);

        // 预置图章：kind token + token（不带 image）
        var preset = adds["stamp-preset"];
        Assert.Equal("stamp", preset["kind"]);
        Assert.Equal("approved", preset["stamp"]);
        Assert.False(preset.ContainsKey("image"));
        Assert.Equal(new[] { 10.0, 20.0, 210.0, 80.0 }, Assert.IsType<double[]>(preset["rect"]));

        // 图片图章：base64 走 JSON 协议（不带 token）
        var withImage = adds["stamp-image"];
        Assert.Equal("stamp", withImage["kind"]);
        Assert.Equal(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]), withImage["image"]);
        Assert.False(withImage.ContainsKey("stamp"));
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
        }
    }

    private sealed class CapturingEngine : IAnnotationSaveEngine
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Actions { get; private set; } = [];

        public Task<AnnotationSaveEngineResult> SaveAnnotationsAsync(
            string path,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> actions,
            string? password,
            CancellationToken cancellationToken)
        {
            Actions = actions;
            return Task.FromResult(new AnnotationSaveEngineResult(true, null, null, new Dictionary<string, string>()));
        }
    }
}
