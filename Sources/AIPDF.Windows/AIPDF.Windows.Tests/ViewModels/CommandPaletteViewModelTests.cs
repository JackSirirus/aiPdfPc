// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T5 命令面板测试：空查询排序、最近使用、标题/别名/拼音首字母/多关键词匹配、
// 无结果、执行门禁、选择移动，以及「拼音表覆盖所有注册命令用字」的守卫。
using System.Collections.Generic;
using System.Linq;
using AIPDF.Windows.Models;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

/// <summary>命令面板视图模型（纯逻辑，无 UI）。</summary>
public sealed class CommandPaletteViewModelTests
{
    /// <summary>计划注册的阅读操作标题（T6 将按此注册；用字必须被拼音表覆盖）。</summary>
    private static readonly string[] ReaderOpTitles =
    [
        "命令面板", "全文搜索", "跳页", "放大", "缩小", "适配宽度", "适配页面", "双页对开",
        "单页", "打印", "收藏当前页", "锁定文档", "关闭标签", "新建标签", "退出全屏", "全屏",
        "演示模式", "朗读", "高亮", "笔记", "下划线", "删除线", "图章", "手写", "矩形", "圆",
        "线", "箭头", "复制", "翻译选中文字", "设置", "关于许可",
    ];

    private static CommandPaletteItem Item(
        string id, string title, CommandPaletteGroup group,
        string? subtitle = null, IReadOnlyList<string>? aliases = null, List<string>? log = null)
        => new()
        {
            Id = id,
            Title = title,
            Group = group,
            Subtitle = subtitle,
            Aliases = aliases ?? [],
            Invoke = () => log?.Add(id),
        };

    private static CommandPaletteViewModel Build(params CommandPaletteItem[] items)
    {
        var vm = new CommandPaletteViewModel();
        vm.SetItems(items);
        return vm;
    }

    // ---------------------------------------------------------------- 排序

    [Fact]
    public void EmptyQuery_OrdersByGroup_ReaderThenToolThenSettings()
    {
        var vm = Build(
            Item("settings.theme", "外观设置", CommandPaletteGroup.Settings),
            Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件"),
            Item("reader.fullscreen", "全屏", CommandPaletteGroup.Reader),
            Item("tool.merge", "合并 PDF", CommandPaletteGroup.Tool, "整理页面"));

        Assert.Equal(
            ["reader.fullscreen", "tool.ocr", "tool.merge", "settings.theme"],
            vm.Results.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void EmptyQuery_PutsRecentlyUsedOnTop_InMruOrder()
    {
        var vm = Build(
            Item("reader.a", "甲", CommandPaletteGroup.Reader),
            Item("reader.b", "乙", CommandPaletteGroup.Reader),
            Item("tool.c", "丙", CommandPaletteGroup.Tool));

        vm.RecordUsage("tool.c");
        vm.RecordUsage("reader.b");

        Assert.Equal(["reader.b", "tool.c", "reader.a"], vm.Results.Select(r => r.Id).ToArray());
        Assert.Equal(["reader.b", "tool.c"], vm.RecentIds);

        // 重复使用只前移，不重复记录；超出上限截断
        vm.RecordUsage("reader.b");
        Assert.Equal(["reader.b", "tool.c"], vm.RecentIds);
        for (var i = 0; i < CommandPaletteViewModel.MaxRecent + 3; i++)
        {
            vm.RecordUsage($"extra.{i}");
        }

        Assert.Equal(CommandPaletteViewModel.MaxRecent, vm.RecentIds.Count);
    }

    // ---------------------------------------------------------------- 匹配

    [Fact]
    public void Query_MatchesTitle_SubstringCaseInsensitive()
    {
        var vm = Build(
            Item("tool.pdf_to_word", "PDF 转 Word", CommandPaletteGroup.Tool, "格式转换"),
            Item("tool.word_to_pdf", "Word 转 PDF", CommandPaletteGroup.Tool, "格式转换"),
            Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件"));

        vm.Query = "word";

        Assert.Equal(2, vm.Results.Count);
        Assert.DoesNotContain(vm.Results, r => r.Id == "tool.ocr");
    }

    [Fact]
    public void Query_MatchesPinyinInitials_ExactPrefixAndSubsequence()
    {
        var vm = Build(
            Item("reader.search", "全文搜索", CommandPaletteGroup.Reader),
            Item("reader.print", "打印", CommandPaletteGroup.Reader));

        vm.Query = "qwss";                       // 全拼首字母全等
        Assert.Equal(["reader.search"], vm.Results.Select(r => r.Id).ToArray());

        vm.Query = "qw";                         // 前缀
        Assert.Equal(["reader.search"], vm.Results.Select(r => r.Id).ToArray());

        vm.Query = "qs";                         // 子序列（q…s…）
        Assert.Equal(["reader.search"], vm.Results.Select(r => r.Id).ToArray());

        vm.Query = "dy";                         // 打印 = dy
        Assert.Equal(["reader.print"], vm.Results.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Query_MatchesAliasesAndId()
    {
        var vm = Build(
            Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件", ["ocr", "识别"]),
            Item("tool.merge", "合并 PDF", CommandPaletteGroup.Tool, "整理页面", ["merge", "hebing"]));

        vm.Query = "hebing";                     // 别名
        Assert.Equal(["tool.merge"], vm.Results.Select(r => r.Id).ToArray());

        vm.Query = "ocr";                        // 标题里就有 OCR（标题包含优先）
        Assert.Equal(["tool.ocr"], vm.Results.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Query_MultipleTokens_AreAnded()
    {
        var vm = Build(
            Item("tool.pdf_to_word", "PDF 转 Word", CommandPaletteGroup.Tool, "格式转换"),
            Item("tool.pdf_to_excel", "PDF 转 Excel", CommandPaletteGroup.Tool, "格式转换"),
            Item("tool.word_to_pdf", "Word 转 PDF", CommandPaletteGroup.Tool, "格式转换"));

        vm.Query = "pdf word";

        Assert.Equal(
            ["tool.pdf_to_word", "tool.word_to_pdf"],
            vm.Results.Select(r => r.Id).OrderBy(id => id).ToArray());
        Assert.DoesNotContain(vm.Results, r => r.Id == "tool.pdf_to_excel");
    }

    [Fact]
    public void Query_MatchesSubtitle_AsWeakestTier()
    {
        var vm = Build(
            Item("tool.merge", "合并 PDF", CommandPaletteGroup.Tool, "整理页面"),
            Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件"));

        vm.Query = "整理页面";                    // 只命中副标题（分类）

        Assert.Equal(["tool.merge"], vm.Results.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Query_NoMatch_YieldsEmptyResults_AndSelectionCleared()
    {
        var vm = Build(Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件"));

        vm.Query = "zzzz-not-a-command";

        Assert.Empty(vm.Results);
        Assert.False(vm.HasResults);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public void Query_Blank_BehavesLikeEmpty()
    {
        var vm = Build(Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件"));

        vm.Query = "   ";

        Assert.Single(vm.Results);
    }

    // ---------------------------------------------------------------- 执行与选择

    [Fact]
    public void Execute_RunsOnlyItemsPresentInCurrentResults_AndRecordsUsage()
    {
        var log = new List<string>();
        var ocr = Item("tool.ocr", "OCR 文字识别", CommandPaletteGroup.Tool, "优化文件", log: log);
        var merge = Item("tool.merge", "合并 PDF", CommandPaletteGroup.Tool, "整理页面", log: log);
        var vm = Build(ocr, merge);

        vm.Query = "ocr";
        Assert.False(vm.Execute(merge));          // 已被过滤掉：不得执行
        Assert.Empty(log);

        Assert.True(vm.Execute(ocr));
        Assert.Equal(["tool.ocr"], log);
        Assert.Equal(["tool.ocr"], vm.RecentIds);

        Assert.False(vm.Execute(null));
    }

    [Fact]
    public void MoveSelection_WrapsAround_AndKeepsSelectionOnRebuildWhenStillPresent()
    {
        var vm = Build(
            Item("reader.a", "甲", CommandPaletteGroup.Reader),
            Item("reader.b", "乙", CommandPaletteGroup.Reader),
            Item("reader.c", "丙", CommandPaletteGroup.Reader));

        // 初始高亮即第一条（Rebuild 收敛到 Results[0]）
        Assert.Equal("reader.a", vm.Selected?.Id);

        vm.MoveSelection(1);
        Assert.Equal("reader.b", vm.Selected?.Id);

        vm.MoveSelection(1);
        Assert.Equal("reader.c", vm.Selected?.Id);

        vm.MoveSelection(1);                      // 末项再下移 → 环绕到首项
        Assert.Equal("reader.a", vm.Selected?.Id);

        vm.MoveSelection(-1);                     // 首项再上移 → 环绕到末项
        Assert.Equal("reader.c", vm.Selected?.Id);

        // 查询把选中项过滤掉后，选择收敛到第一条
        vm.Query = "甲";
        Assert.Equal("reader.a", vm.Selected?.Id);

        vm.Query = "乙";
        Assert.Equal("reader.b", vm.Selected?.Id);
    }

    // ---------------------------------------------------------------- 拼音表守卫

    [Fact]
    public void PinyinTable_CoversEveryRegisteredToolNameAndCategory()
    {
        var tools = ToolCatalog.Load();
        Assert.Equal(33, tools.Count);

        var uncovered = new List<char>();
        foreach (var tool in tools)
        {
            foreach (var text in new[] { tool.Name, tool.Category })
            {
                foreach (var ch in text)
                {
                    if (ch is >= '\u4e00' and <= '\u9fff' && !PinyinInitials.Covers(ch))
                    {
                        uncovered.Add(ch);
                    }
                }
            }

            Assert.False(string.IsNullOrEmpty(PinyinInitials.Of(tool.Name)),
                $"工具 {tool.Id} 的名称无法生成拼音首字母");
        }

        Assert.Empty(uncovered.Distinct());
    }

    [Fact]
    public void PinyinTable_CoversEveryPlannedReaderOperationTitle()
    {
        var uncovered = new List<char>();
        foreach (var title in ReaderOpTitles)
        {
            Assert.False(string.IsNullOrEmpty(PinyinInitials.Of(title)), $"「{title}」无法生成拼音首字母");
            uncovered.AddRange(title.Where(ch => ch is >= '\u4e00' and <= '\u9fff' && !PinyinInitials.Covers(ch)));
        }

        Assert.Empty(uncovered.Distinct());
    }

    [Fact]
    public void PinyinTable_IsNonEmpty_AndSkipsNonChinese()
    {
        Assert.True(PinyinInitials.CoveredCharacterCount >= 130);
        Assert.Equal("", PinyinInitials.Of("OCR 123"));
        Assert.Equal("qwss", PinyinInitials.Of("全文搜索"));
        Assert.Equal("", PinyinInitials.Of(null));
    }
}
