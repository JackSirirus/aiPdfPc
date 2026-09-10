// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using AIPDF.Windows.Models;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 标签宿主视图模型测试：同路径去重激活【D14】、标签集合增删与邻位选择、
/// 工具标签实例缓存【D16】（重复进入不重建内容）。纯 VM 逻辑，无 UI 依赖。
/// </summary>
public sealed class TabHostViewModelTests
{
    [Fact]
    public void OpenDocument_CreatesTabSelectsItAndSetsHasTabs()
    {
        var host = new TabHostViewModel();
        var raised = new List<string?>();
        host.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var tab = host.OpenDocument(Path.Combine(Path.GetTempPath(), "a.pdf"));

        Assert.Single(host.Tabs);
        Assert.Same(tab, host.SelectedTab);
        Assert.True(host.HasTabs);
        Assert.Equal("a.pdf", tab.Title);
        Assert.Contains(nameof(TabHostViewModel.HasTabs), raised);
    }

    [Fact]
    public void OpenDocument_SamePathActivatesExistingWithoutNewTabOrFactoryCall()
    {
        var host = new TabHostViewModel();
        var first = host.OpenDocument(Path.Combine(Path.GetTempPath(), "dup.pdf"), () => new object());
        var original = host.SelectedTab;

        var factoryCalls = 0;
        var second = host.OpenDocument(Path.Combine(Path.GetTempPath(), "dup.pdf"), () => { factoryCalls++; return new object(); });

        Assert.Single(host.Tabs);
        Assert.Same(first, second);
        Assert.Same(original, host.SelectedTab);
        Assert.Equal(0, factoryCalls); // 去重激活不重建内容【D14】
    }

    [Fact]
    public void OpenDocument_SamePathDifferentCaseActivatesExisting()
    {
        var host = new TabHostViewModel();
        var first = host.OpenDocument(Path.Combine(Path.GetTempPath(), "Report.pdf"));

        var second = host.OpenDocument(Path.Combine(Path.GetTempPath(), "report.pdf"));

        Assert.Single(host.Tabs); // Windows 路径大小写不敏感【D14】
        Assert.Same(first, second);
        Assert.Same(first, host.SelectedTab);
    }

    [Fact]
    public void OpenDocument_DifferentPathsCreateSeparateTabs()
    {
        var host = new TabHostViewModel();

        var a = host.OpenDocument(Path.Combine(Path.GetTempPath(), "a.pdf"));
        var b = host.OpenDocument(Path.Combine(Path.GetTempPath(), "b.pdf"));

        Assert.Equal(2, host.Tabs.Count);
        Assert.NotSame(a, b);
        Assert.Same(b, host.SelectedTab);
    }

    [Fact]
    public void OpenDocument_NormalizesRelativePathForDedup()
    {
        var host = new TabHostViewModel();
        var relative = Path.Combine(Environment.CurrentDirectory, "spec.pdf");
        var first = host.OpenDocument(relative);

        var second = host.OpenDocument("spec.pdf");

        Assert.Single(host.Tabs); // 规范化到全路径后按路径去重
        Assert.Same(first, second);
        Assert.Equal(Path.GetFullPath("spec.pdf"), first.FilePath);
    }

    [Fact]
    public void CloseTab_RemovesTabAndSelectsNextNeighbor()
    {
        var host = new TabHostViewModel();
        var a = host.OpenDocument(Path.Combine(Path.GetTempPath(), "a.pdf"));
        var b = host.OpenDocument(Path.Combine(Path.GetTempPath(), "b.pdf"));
        var c = host.OpenDocument(Path.Combine(Path.GetTempPath(), "c.pdf"));

        var closed = host.CloseTab(b); // 关闭中间的选中标签 → 选中右邻

        Assert.True(closed);
        Assert.Equal(2, host.Tabs.Count);
        Assert.DoesNotContain(b, host.Tabs);
        Assert.Same(c, host.SelectedTab);
        Assert.Contains(a, host.Tabs);
    }

    [Fact]
    public void CloseTab_LastTabSelectsPreviousNeighbor()
    {
        var host = new TabHostViewModel();
        host.OpenDocument(Path.Combine(Path.GetTempPath(), "a.pdf"));
        var b = host.OpenDocument(Path.Combine(Path.GetTempPath(), "b.pdf"));
        var c = host.OpenDocument(Path.Combine(Path.GetTempPath(), "c.pdf"));

        var closed = host.CloseTab(c);

        Assert.True(closed);
        Assert.Same(b, host.SelectedTab); // 末位关闭 → 左邻
        Assert.Equal(2, host.Tabs.Count);
    }

    [Fact]
    public void CloseTab_NonSelectedTabKeepsSelection()
    {
        var host = new TabHostViewModel();
        var a = host.OpenDocument(Path.Combine(Path.GetTempPath(), "a.pdf"));
        var b = host.OpenDocument(Path.Combine(Path.GetTempPath(), "b.pdf"));

        var closed = host.CloseTab(a);

        Assert.True(closed);
        Assert.Same(b, host.SelectedTab); // 关闭非选中标签不影响选中
        Assert.Single(host.Tabs);
    }

    [Fact]
    public void CloseTab_LastRemainingTabClearsSelectionAndHasTabs()
    {
        var host = new TabHostViewModel();
        var raised = new List<string?>();
        host.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var only = host.OpenDocument(Path.Combine(Path.GetTempPath(), "only.pdf"));

        var closed = host.CloseTab(only);

        Assert.True(closed);
        Assert.Empty(host.Tabs);
        Assert.False(host.HasTabs);
        Assert.Null(host.SelectedTab);
        Assert.Contains(nameof(TabHostViewModel.HasTabs), raised); // 空态联动信号
    }

    [Fact]
    public void CloseTab_UnknownOrNullTabReturnsFalse()
    {
        var host = new TabHostViewModel();
        var kept = host.OpenDocument(Path.Combine(Path.GetTempPath(), "kept.pdf"));
        var stranger = new DocumentTabViewModel(Path.Combine(Path.GetTempPath(), "other.pdf"), "other.pdf", null);

        Assert.False(host.CloseTab(null));
        Assert.False(host.CloseTab(stranger));
        Assert.Same(kept, host.SelectedTab);
        Assert.Single(host.Tabs);
    }

    [Fact]
    public void EnsureToolTab_SameToolActivatesExistingAndKeepsContentInstance()
    {
        var host = new TabHostViewModel();
        var tool = new Tool { Id = "merge", Name = "合并 PDF", Category = "页面整理" };
        var firstContent = new object();
        var first = host.EnsureToolTab(tool, () => firstContent);
        var selectedBefore = host.SelectedTab;

        var factoryCalls = 0;
        var second = host.EnsureToolTab(tool, () => { factoryCalls++; return new object(); });

        Assert.Single(host.Tabs);
        Assert.Same(first, second);
        Assert.Same(selectedBefore, host.SelectedTab);
        Assert.Same(firstContent, second.Content); // 页面实例缓存【D16】：不重建、不丢状态
        Assert.Equal("合并 PDF", second.Title);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void EnsureToolTab_DifferentToolsCreateSeparateTabs()
    {
        var host = new TabHostViewModel();
        var merge = new Tool { Id = "merge", Name = "合并 PDF" };
        var rotate = new Tool { Id = "rotate", Name = "旋转页面" };

        var t1 = host.EnsureToolTab(merge, () => new object());
        var t2 = host.EnsureToolTab(rotate, () => new object());

        Assert.Equal(2, host.Tabs.Count);
        Assert.NotSame(t1, t2);
        Assert.Same(t2, host.SelectedTab);
        Assert.Null(t1.FilePath);
        Assert.Same(rotate, t2.Tool);
    }

    [Fact]
    public void DocumentAndToolTabs_KeepSeparateDedupDomains()
    {
        var host = new TabHostViewModel();
        var tool = new Tool { Id = "a.pdf", Name = "同名工具" };

        var doc = host.OpenDocument(Path.Combine(Path.GetTempPath(), "a.pdf"));
        var toolTab = host.EnsureToolTab(tool, () => new object());

        Assert.Equal(2, host.Tabs.Count); // 文档按路径去重、工具按 Id 去重，互不串扰
        Assert.Same(toolTab, host.SelectedTab); // 新建工具标签即激活
        Assert.Same(toolTab, host.Tabs[1]);
        Assert.Equal(doc.FilePath, host.Tabs[0].FilePath);
    }
}
