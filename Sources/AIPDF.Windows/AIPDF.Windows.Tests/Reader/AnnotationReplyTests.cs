// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T10 回复线程测试：模型工厂、读回时 /IRT → ParentId 还原（含父不在范围内的降级）、
// 属性面板的回复请求门禁、宿主创建脏记录且线程可按父 Id 过滤。
using System;
using System.Collections.Generic;
using System.Text.Json;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>批注回复线程（模型 + 读回 + VM + 宿主）。</summary>
public sealed class AnnotationReplyTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---------------------------------------------------------------- 模型

    [Fact]
    public void CreateReply_StoresParentAndText()
    {
        var reply = AnnotationRecord.CreateReply(1, new PdfRect(12, 0, 100, 40), "h1", "同意");

        Assert.Equal(AnnotationKind.Note, reply.Kind);   // PDF 里回复是 FreeText，复用 Note 渲染
        Assert.Equal("h1", reply.ParentId);
        Assert.Equal("同意", reply.Note);
        Assert.Equal(1, reply.PageIndex);

        // ParentId 参与内容等价（保存链路据此判断是否需要重写）
        Assert.False(reply.ContentEquals(reply with { ParentId = "other" }));

        Assert.Throws<ArgumentException>(
            () => AnnotationRecord.CreateReply(0, new PdfRect(0, 0, 1, 1), "  ", "x"));
        Assert.Throws<ArgumentException>(
            () => AnnotationRecord.CreateReply(0, new PdfRect(0, 0, 1, 1), "h1", "  "));
    }

    // ---------------------------------------------------------------- 读回映射

    [Fact]
    public void Readback_MapsIrtToParentId_AndDegradesWhenParentMissing()
    {
        var replyRow = Row(
            """{"page":0,"xref":9,"subtype":"/FreeText","privateName":"r1","irt":"7 0 R","content":"同意","rect":[12,0,100,40]}""");
        var nameByXref = new Dictionary<int, string> { [7] = "h1" };

        var reply = AnnotationReadbackService.ToRecord(replyRow, nameByXref);
        Assert.Equal("h1", reply!.ParentId);
        Assert.Equal("同意", reply.Note);
        Assert.Equal("r1", reply.Id);

        // 父批注不在本次读回范围内（或名字缺失）：退化为普通笔记，**内容不丢**
        var orphan = AnnotationReadbackService.ToRecord(replyRow, new Dictionary<int, string>());
        Assert.Null(orphan!.ParentId);
        Assert.Equal("同意", orphan.Note);

        // 无 /IRT 的普通笔记不应被当成回复
        var noteRow = Row(
            """{"page":0,"xref":11,"subtype":"/FreeText","privateName":"n1","content":"普通笔记","rect":[0,0,10,10]}""");
        Assert.Null(AnnotationReadbackService.ToRecord(noteRow, nameByXref)!.ParentId);
    }

    // ---------------------------------------------------------------- 属性面板

    [Fact]
    public void PropertiesViewModel_RequestReply_RequiresSelectionAndText()
    {
        var viewModel = new AnnotationPropertiesViewModel();
        var raised = new List<AnnotationReplyRequest>();
        viewModel.ReplyRequested += (_, request) => raised.Add(request);

        viewModel.RequestReply("没有选中");        // 无选中 → 不发
        Assert.Empty(raised);

        viewModel.Select(AnnotationRecord.CreateHighlight(0, new PdfRect(0, 0, 10, 10)) with { Id = "h1" });
        viewModel.RequestReply("   ");              // 空内容 → 不发
        Assert.Empty(raised);

        viewModel.RequestReply(" 同意 ");           // 去首尾空白后发出
        var request = Assert.Single(raised);
        Assert.Equal("h1", request.ParentId);
        Assert.Equal("同意", request.Text);
    }

    [Fact]
    public void PropertiesViewModel_SetReplies_TracksThread()
    {
        var viewModel = new AnnotationPropertiesViewModel();
        Assert.False(viewModel.HasReplies);

        viewModel.SetReplies([AnnotationRecord.CreateReply(0, new PdfRect(0, 0, 10, 10), "h1", "一")]);
        Assert.True(viewModel.HasReplies);
        Assert.Single(viewModel.Replies);

        viewModel.SetReplies(null);
        Assert.False(viewModel.HasReplies);
        Assert.Empty(viewModel.Replies);
    }

    // ---------------------------------------------------------------- 宿主

    [Fact]
    public void AddReply_CreatesDirtyRecord_AndThreadFiltersByParent()
    {
        using var viewModel = new ReaderViewModel(new PdfiumRenderService());
        viewModel.LoadDocument(Corpus.Mixed100);

        var parent = viewModel.AddHighlight(0, new PdfRect(10, 20, 110, 40));
        Assert.NotNull(parent);

        var reply = viewModel.AddReply(parent!.Id, "同意");
        Assert.NotNull(reply);
        Assert.Equal(parent.Id, reply!.ParentId);

        // 进**脏记录集合**（会被保存到 PDF），且线程按父 Id 过滤能找到它
        Assert.Contains(viewModel.GetAllRecords(), record => record.Id == reply.Id);
        var thread = viewModel.CollectReplies(parent.Id);
        var only = Assert.Single(thread);
        Assert.Equal("同意", only.Note);

        // 选中父批注 → 属性面板的线程被刷新
        viewModel.SelectAnnotationForProperties(parent);
        Assert.Single(viewModel.Properties.Replies);

        // 找不到父批注 / 空文本 → null（不抛）
        Assert.Null(viewModel.AddReply("no-such-annotation", "x"));
        Assert.Null(viewModel.AddReply(parent.Id, "   "));
    }
}
