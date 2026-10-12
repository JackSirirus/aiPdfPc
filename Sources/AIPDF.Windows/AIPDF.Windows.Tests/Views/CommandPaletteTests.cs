// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T6 命令面板测试：注册表装配（33 工具 / 阅读操作 / 设置）+ 宿主级键盘语义。
//
// 键盘语义走 HandleKey（非 UIA）：本机 FlaUI 冒烟会因 UIA 超时被 skip，不能作为唯一证据。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.CommandPalette;
using Xunit;

// 注意：命名空间不能叫 AIPDF.Windows.Tests.Views——那会遮蔽既有测试文件里的 Views.* 相对引用。
namespace AIPDF.Windows.Tests.CommandPalette;

/// <summary>命令面板注册表与覆盖层（宿主级，STA）。</summary>
public sealed class CommandPaletteTests
{
    private static CommandPaletteItem Item(
        string id, string title, CommandPaletteGroup group, List<string>? log = null)
        => new()
        {
            Id = id,
            Title = title,
            Group = group,
            Invoke = () => log?.Add(id),
        };

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    // ---------------------------------------------------------------- 注册表

    [Fact]
    public void BuildToolItems_OneItemPerTool_WithPrefixedIdAndEnglishAliases()
    {
        var tools = ToolCatalog.Load();
        var invoked = new List<string>();
        var items = CommandPaletteRegistry.BuildToolItems(tools, tool => invoked.Add(tool.Id));

        Assert.Equal(33, items.Count);
        Assert.Equal(33, items.Select(item => item.Id).Distinct().Count());
        Assert.All(items, item => Assert.StartsWith(CommandPaletteRegistry.ToolIdPrefix, item.Id));
        Assert.All(items, item => Assert.Equal(CommandPaletteGroup.Tool, item.Group));
        Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.Subtitle)));

        // 别名含英文 id：用户更常直接输入 ocr / merge / pdf_to_word
        var ocr = items.Single(item => item.Id == "tool.ocr");
        Assert.Contains("ocr", ocr.Aliases);

        // Invoke 必须把「同一个工具实例」交给宿主回调——宿主据此走与工具箱同一条通道
        ocr.Invoke();
        Assert.Equal(["ocr"], invoked);
    }

    [Fact]
    public void BuildItems_SetsGroupAndRoutesInvoke()
    {
        var invoked = new List<string>();
        CommandPaletteOperation[] operations =
        [
            new("reader.print", "打印", ["print", "dayin"], () => invoked.Add("print")),
            new("reader.search", "全文搜索", ["search"], () => invoked.Add("search")),
        ];

        var items = CommandPaletteRegistry.BuildItems(operations, CommandPaletteGroup.Reader);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(CommandPaletteGroup.Reader, item.Group));
        Assert.Equal("打印", items[0].Title);
        items[1].Invoke();
        Assert.Equal(["search"], invoked);
    }

    [Fact]
    public void Registry_ToolAndOperationIds_DoNotCollide()
    {
        var tools = ToolCatalog.Load();
        var ids = CommandPaletteRegistry.BuildToolItems(tools, _ => { })
            .Select(item => item.Id)
            .Concat(CommandPaletteRegistry
                .BuildItems([new CommandPaletteOperation("reader.print", "打印", [], () => { })],
                    CommandPaletteGroup.Reader)
                .Select(item => item.Id))
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ---------------------------------------------------------------- 覆盖层（STA）

    [Fact]
    public void View_ToggleOpensAndCloses_AndClearsQueryOnReopen()
    {
        RunOnSta(() =>
        {
            var viewModel = new CommandPaletteViewModel();
            viewModel.SetItems([Item("a", "甲", CommandPaletteGroup.Reader)]);
            var view = new CommandPaletteView { DataContext = viewModel };

            Assert.False(view.IsOpen);

            view.Toggle();
            Assert.True(view.IsOpen);

            viewModel.Query = "甲";
            view.Toggle();
            Assert.False(view.IsOpen);

            view.Toggle();
            Assert.True(view.IsOpen);
            Assert.Equal(string.Empty, viewModel.Query);   // 重开清空查询
        });
    }

    [Fact]
    public void View_HandleKey_MovesSelection_ExecutesOnEnter_ClosesOnEscape()
    {
        RunOnSta(() =>
        {
            var log = new List<string>();
            var viewModel = new CommandPaletteViewModel();
            viewModel.SetItems(
            [
                Item("a", "甲", CommandPaletteGroup.Reader, log),
                Item("b", "乙", CommandPaletteGroup.Reader, log),
            ]);
            var view = new CommandPaletteView { DataContext = viewModel };
            view.Open();

            Assert.True(view.HandleKey(Key.Down));       // 甲 → 乙
            Assert.Equal("b", viewModel.Selected?.Id);

            Assert.True(view.HandleKey(Key.Up));         // 乙 → 甲
            Assert.Equal("a", viewModel.Selected?.Id);

            Assert.True(view.HandleKey(Key.Enter));      // 执行选中项并关闭
            Assert.Equal(["a"], log);
            Assert.False(view.IsOpen);

            view.Open();
            Assert.True(view.HandleKey(Key.Escape));
            Assert.False(view.IsOpen);

            Assert.False(view.HandleKey(Key.F5));        // 无关按键不处理
        });
    }

    [Fact]
    public void View_Close_RaisesCloseRequested_OnlyWhenOpen()
    {
        RunOnSta(() =>
        {
            var viewModel = new CommandPaletteViewModel();
            viewModel.SetItems([Item("a", "甲", CommandPaletteGroup.Reader)]);
            var view = new CommandPaletteView { DataContext = viewModel };

            var raised = 0;
            view.CloseRequested += (_, _) => raised++;

            view.Close();                                // 未打开：不触发
            Assert.Equal(0, raised);

            view.Open();
            view.Close();
            Assert.Equal(1, raised);

            view.Close();                                // 幂等：再次关闭不触发
            Assert.Equal(1, raised);
        });
    }
}
