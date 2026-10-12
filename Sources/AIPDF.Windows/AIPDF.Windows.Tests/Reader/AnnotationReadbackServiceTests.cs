// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T9a 批注读回测试：引擎行 → 客户端记录（subtype/颜色/矩形/名字优先级/不支持类型跳过），
// 以及「读回记录必须**不进脏记录集合**」——否则保存时会被当成新增批注重复写回。
using System;
using System.Text.Json;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>批注读回（解析为纯函数；叠加层与脏集合边界用真实 VM + 合成语料）。</summary>
public sealed class AnnotationReadbackServiceTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---------------------------------------------------------------- 行解析

    [Fact]
    public void ToRecord_MapsHighlightUnderlineStrikeoutAndNote()
    {
        var highlight = AnnotationReadbackService.ToRecord(Row(
            """{"page":1,"xref":7,"subtype":"/Highlight","name":"fitz-A0","privateName":"h1","color":"[1 0 0]","rect":[10,20,110,40],"content":null}"""));
        Assert.NotNull(highlight);
        Assert.Equal(AnnotationKind.Highlight, highlight!.Kind);
        Assert.Equal(1, highlight.PageIndex);
        Assert.Equal("h1", highlight.Id);                       // 私有 /Name 优先
        Assert.Equal("#FF0000", highlight.Color);
        Assert.Equal(new PdfRect(10, 20, 110, 40), highlight.Rect);

        var underline = AnnotationReadbackService.ToRecord(Row(
            """{"page":0,"xref":9,"subtype":"/Underline","privateName":"u1","color":"[0 1 0]","rect":[0,0,50,10]}"""));
        Assert.Equal(AnnotationKind.Underline, underline!.Kind);
        Assert.Equal("#00FF00", underline.Color);

        var strikeout = AnnotationReadbackService.ToRecord(Row(
            """{"page":0,"xref":11,"subtype":"/StrikeOut","privateName":"s1","rect":[0,0,50,10]}"""));
        Assert.Equal(AnnotationKind.Strikeout, strikeout!.Kind);

        var note = AnnotationReadbackService.ToRecord(Row(
            """{"page":2,"xref":13,"subtype":"/FreeText","privateName":"n1","content":"读回笔记","rect":[0,0,50,10]}"""));
        Assert.Equal(AnnotationKind.Note, note!.Kind);
        Assert.Equal("读回笔记", note.Note);
    }

    [Fact]
    public void ToRecord_FallsBackToStandardNameThenXref_AndSkipsUnsupported()
    {
        // 私有 /Name 缺失 → 退化到标准 /NM
        var byNm = AnnotationReadbackService.ToRecord(Row(
            """{"page":0,"xref":7,"subtype":"/Highlight","name":"STD-1","privateName":null,"rect":[0,0,10,10]}"""));
        Assert.Equal("STD-1", byNm!.Id);

        // 两个名字都没有 → 退化到 xref
        var byXref = AnnotationReadbackService.ToRecord(Row(
            """{"page":0,"xref":42,"subtype":"/Highlight","rect":[0,0,10,10]}"""));
        Assert.Equal("xref-42", byXref!.Id);

        // 不支持的 subtype / 缺矩形：跳过（返回 null），不抛
        Assert.Null(AnnotationReadbackService.ToRecord(Row(
            """{"page":0,"xref":5,"subtype":"/Ink","privateName":"i1","rect":[0,0,10,10]}""")));
        Assert.Null(AnnotationReadbackService.ToRecord(Row(
            """{"page":0,"xref":6,"subtype":"/Highlight","privateName":"h9"}""")));
        Assert.Null(AnnotationReadbackService.ToRecord(Row("""{"page":0,"subtype":"/Highlight"}""")));
    }

    [Fact]
    public void ToHexColor_ParsesPdfColorArrays_AndRejectsGarbage()
    {
        Assert.Equal("#FF0000", AnnotationReadbackService.ToHexColor("[1 0 0]"));
        Assert.Equal("#FFFF00", AnnotationReadbackService.ToHexColor("1 1 0"));
        Assert.Equal("#0000FF", AnnotationReadbackService.ToHexColor("[0 0 1]"));
        Assert.Equal("#804020", AnnotationReadbackService.ToHexColor("[0.5 0.25 0.125]"));
        Assert.Null(AnnotationReadbackService.ToHexColor(null));
        Assert.Null(AnnotationReadbackService.ToHexColor("[1 0]"));
        Assert.Null(AnnotationReadbackService.ToHexColor("[a b c]"));
    }

    // ---------------------------------------------------------------- 与脏记录集合的边界

    [Fact]
    public void SetEmbeddedRecords_ProjectsOverlay_ButStaysOutOfDirtyRecords()
    {
        using var vm = new ReaderViewModel(new PdfiumRenderService());
        vm.LoadDocument(Corpus.Mixed100);

        var imported = AnnotationRecord.CreateHighlight(0, new PdfRect(10, 20, 110, 40), "#FF0000") with { Id = "h1" };
        vm.SetEmbeddedRecords([imported]);

        // 叠加层已投影（用户看得到）
        Assert.Single(vm.EmbeddedRecords);
        Assert.Contains(vm.Pages[0].Annotations, overlay => overlay.Id == "h1");

        // **关键**：读回的批注不得进脏记录集合，否则保存时会被当成新增批注重复写回
        Assert.DoesNotContain(vm.GetAllRecords(), record => record.Id == "h1");
        Assert.Empty(vm.GetAllRecords());

        vm.ClearEmbeddedRecords();
        Assert.Empty(vm.EmbeddedRecords);
        Assert.DoesNotContain(vm.Pages[0].Annotations, overlay => overlay.Id == "h1");
    }
}
