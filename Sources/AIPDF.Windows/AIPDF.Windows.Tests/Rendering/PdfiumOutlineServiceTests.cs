// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 大纲解析测试：带书签合成 PDF（tmp/t4-corpus，PyMuPDF set_toc 派生自 spike 语料，不入库）
// 断言层级/标题/目标页码（1 基 toc → 0 基PageIndex）与无大纲/未打开分支。
using System.IO;
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Rendering;

public sealed class PdfiumOutlineServiceTests
{
    /// <summary>合成带大纲语料（缺失时用 .venv PyMuPDF 从 spike mixed100 派生）。</summary>
    private static string EnsureOutlinedCorpus()
    {
        // 测试输出目录 → 仓库根（tmp/t4-corpus/gen_outline.py 的祖先）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tmp", "t4-corpus", "gen_outline.py")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent!;
        }
        Assert.NotNull(repoRoot);

        var corpus = Path.Combine(repoRoot!, "tmp", "t4-corpus", "outlined100.pdf");
        if (!File.Exists(corpus))
        {
            var python = Path.Combine(repoRoot!, ".venv", "Scripts", "python.exe");
            Assert.True(File.Exists(python), ".venv python 缺失");
            var psi = new System.Diagnostics.ProcessStartInfo(python,
                $"\"{Path.Combine(repoRoot!, "tmp", "t4-corpus", "gen_outline.py")}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(120_000);
            Assert.True(File.Exists(corpus), "T4 语料生成失败");
        }
        return corpus;
    }

    [Fact]
    public void ReadOutline_ReturnsTreeWithLevelsTitlesAndTargetPages()
    {
        using var svc = new PdfiumRenderService();
        var docId = svc.OpenDocument(EnsureOutlinedCorpus());

        var outline = PdfiumOutlineService.ReadOutline(svc, docId);

        Assert.NotNull(outline);

        // 诊断（失败时输出实际树；ASCII 避免 PowerShell 重定向丢字）
        var dump = string.Join(" | ", outline!.Select((t, i) =>
            $"#{i} page={t.PageIndex} children={t.Children.Count} chars={t.Title.Length}"));
        Assert.True(outline.Count == 3, $"topCount {outline.Count} != 3 :: {dump}");

        Assert.Equal(3, outline!.Count); // 3 个顶层条目

        // 第一章 总论（toc 1 基第 1 页 → 0 基 0），两个二级子条目
        var ch1 = outline[0];
        Assert.Equal("第一章 总论", ch1.Title);
        Assert.Equal(0, ch1.PageIndex);
        Assert.Equal(2, ch1.Children.Count);
        Assert.Equal("1.1 背景", ch1.Children[0].Title);
        Assert.Equal(2, ch1.Children[0].PageIndex);
        Assert.Equal("1.2 方法", ch1.Children[1].Title);
        Assert.Equal(7, ch1.Children[1].PageIndex);

        // 第二章 数据（页 20 → 0 基 19），2.1 采集（页 21 → 20），2.1.1 细分口径三级（页 45 → 44）
        var ch2 = outline[1];
        Assert.Equal("第二章 数据", ch2.Title);
        Assert.Equal(19, ch2.PageIndex);
        var sub = Assert.Single(ch2.Children);
        Assert.Equal("2.1 采集", sub.Title);
        Assert.Equal(20, sub.PageIndex);
        var leaf = Assert.Single(sub.Children);
        Assert.Equal("2.1.1 细分口径", leaf.Title);
        Assert.Equal(44, leaf.PageIndex);
        Assert.Empty(leaf.Children); // 三级即叶子
    }

    [Fact]
    public void ReadOutline_NoOutlinePdf_ReturnsEmptyList()
    {
        // 无大纲文档：手工构造（无 Outlines 对象）
        var bytes = TestPdfFactory.Create((595, 842), (595, 842));
        var path = Path.Combine(Path.GetTempPath(), $"t4-nooutline-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var svc = new PdfiumRenderService();
            var docId = svc.OpenDocument(path);

            var outline = PdfiumOutlineService.ReadOutline(svc, docId);

            Assert.NotNull(outline);
            Assert.Empty(outline!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadOutline_DocumentNotOpen_ReturnsNull()
    {
        using var svc = new PdfiumRenderService();
        var outline = PdfiumOutlineService.ReadOutline(svc, @"C:\definitely\not\open.pdf");
        Assert.Null(outline);
    }
}
