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
