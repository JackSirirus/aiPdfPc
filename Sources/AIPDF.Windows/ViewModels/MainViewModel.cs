// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>主窗口视图模型：侧栏工具目录（分组集合）、选中工具与底部状态栏。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly EngineBridge? _bridge;

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

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>加载真实工具目录（backend/catalog.json）并接入真实引擎桥。</summary>
    public MainViewModel() : this(ToolCatalog.Load(), new EngineBridge())
    {
    }

    /// <summary>测试与预览用：从给定工具集合构建；bridge 为 null 时执行入口只提示引擎未就绪。</summary>
    public MainViewModel(IEnumerable<Tool> tools, EngineBridge? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _bridge = bridge;

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

    /// <summary>
    /// 执行工具的最小骨架：调桥 → 进度 → 完成/失败/取消状态。具体工具页在 Wave 5 填充。
    /// 引擎错误（Ok=false）按引擎原文展示；取消后状态复位。
    /// </summary>
    public async Task ExecuteToolAsync(Tool tool, IReadOnlyList<string> files, string outputDir,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (IsRunning)
        {
            return; // 防重入：一次只跑一个引擎任务
        }

        if (_bridge is null)
        {
            StatusText = "引擎未就绪：未配置执行桥。";
            return;
        }

        IsRunning = true;
        StatusText = $"正在执行 {tool.Name}…";

        void OnProgress(object? sender, EngineProgressEventArgs e)
        {
            // WPF 绑定要求 UI 线程；测试环境没有 Application，直接在当前线程更新。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                StatusText = e.Message;
            }
            else
            {
                dispatcher.InvokeAsync(() => StatusText = e.Message);
            }
        }

        _bridge.Progress += OnProgress;
        try
        {
            var result = await _bridge.ExecuteAsync(new EngineRequest
            {
                Operation = tool.Id,
                Files = [.. files],
                Options = options is null ? [] : new Dictionary<string, object?>(options),
                OutputDir = outputDir,
            }, cancellationToken);

            StatusText = result.Ok
                ? $"完成 {tool.Name}：{result.Outputs.Count} 个文件 → {result.OutputDir}"
                : $"失败：{result.Error}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
        }
        catch (EngineBridgeException error)
        {
            StatusText = $"引擎异常：{error.Message}";
        }
        finally
        {
            _bridge.Progress -= OnProgress;
            IsRunning = false;
        }
    }

    private string DescribeReady() => $"{Tools.Count} 个工具 · {Groups.Count} 个分组 · 就绪";
}
