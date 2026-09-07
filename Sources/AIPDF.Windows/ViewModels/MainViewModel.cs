// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>主窗口视图模型：侧栏工具目录（分组集合）、选中工具与底部状态栏。</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>全部工具，顺序与 catalog.json 一致；侧栏的分组视图也以此为准。</summary>
    public ObservableCollection<Tool> Tools { get; } = [];

    /// <summary>按 category 分组的集合，组序为目录中首次出现顺序。</summary>
    public ObservableCollection<ToolGroup> Groups { get; } = [];

    /// <summary>侧栏绑定用的分组视图（按 Category 分组，与 Groups 同源）。</summary>
    public ICollectionView ToolsView { get; }

    [ObservableProperty]
    private Tool? _selectedTool;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>加载真实工具目录（backend/catalog.json）。</summary>
    public MainViewModel() : this(ToolCatalog.Load())
    {
    }

    /// <summary>测试与预览用：从给定工具集合构建。</summary>
    public MainViewModel(IEnumerable<Tool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            Tools.Add(tool);
        }

        foreach (var group in ToolCatalog.GroupByCategory(Tools))
        {
            Groups.Add(group);
        }

        ToolsView = CollectionViewSource.GetDefaultView(Tools);
        ToolsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Tool.Category)));

        StatusText = DescribeReady();
    }

    partial void OnSelectedToolChanged(Tool? value)
    {
        StatusText = value is null
            ? DescribeReady()
            : $"已选择 {value.Name} — {value.Subtitle}";
    }

    private string DescribeReady() => $"{Tools.Count} 个工具 · {Groups.Count} 个分组 · 就绪";
}
