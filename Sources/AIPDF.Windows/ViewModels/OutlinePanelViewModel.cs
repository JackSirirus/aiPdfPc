// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T4 目录面板视图模型：消费 PdfiumOutlineService（只读解析），树形展示 + 点击跳页。
using System.Collections.ObjectModel;
using AIPDF.Windows.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>目录树节点：包一层以便 HierarchicalDataTemplate 绑定子级。</summary>
public class OutlineNodeViewModel
{
    public OutlineItem Item { get; }

    public string Title => Item.Title;

    /// <summary>页码显示（1 基）；目标无效（-1）显示「—」。</summary>
    public string PageLabel => Item.PageIndex >= 0 ? (Item.PageIndex + 1).ToString() : "—";

    /// <summary>目标有效才可点击跳页。</summary>
    public bool CanNavigate => Item.PageIndex >= 0;

    public ObservableCollection<OutlineNodeViewModel> Children { get; }

    public OutlineNodeViewModel(OutlineItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Children = [.. item.Children.Select(c => new OutlineNodeViewModel(c))];
    }
}

/// <summary>
/// 目录面板视图模型：加载文档大纲（后台线程解析，防止大目录阻塞 UI），
/// 无大纲 → <see cref="HasOutline"/> = false 触发空态【ST-17】；只读不编辑（V1 边界）。
/// </summary>
public partial class OutlinePanelViewModel : ObservableObject
{
    private readonly PdfiumRenderService _renderer;
    private readonly string _docId;

    public ObservableCollection<OutlineNodeViewModel> Items { get; } = [];

    /// <summary>点击目录请求跳页（宿主接线 T3 ReaderView 滚动 API；T7 前为事件桩）。</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>大纲解析完成且非空（false → 空态【ST-17】）。</summary>
    public bool HasOutline => Items.Count > 0;

    /// <summary>大纲解析中（大目录首次加载提示）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>解析失败提示（损坏大纲等；空字符串 = 无错误）。</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>是否有错误提示（XAML 空态触发用）。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    public OutlinePanelViewModel(PdfiumRenderService renderer, string docId)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _docId = docId;
    }

    /// <summary>加载大纲（幂等：重复调用不重载；后台线程解析，结果回 UI 线程）。</summary>
    public async Task LoadAsync()
    {
        if (Items.Count > 0 || IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            // PDFium 全局锁内解析；文档打开时已在打开阶段完成页结构解析，此处只走书签链
            var outline = await Task.Run(() => PdfiumOutlineService.ReadOutline(_renderer, _docId))
                .ConfigureAwait(true);

            if (outline is null)
            {
                ErrorMessage = "文档已关闭，无法读取目录。";
                return;
            }

            Items.Clear();
            foreach (var node in outline.Select(i => new OutlineNodeViewModel(i)))
            {
                Items.Add(node);
            }
            OnPropertyChanged(nameof(HasOutline));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"目录读取失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>点击目录节点（XAML 绑定命令）。</summary>
    public void NavigateTo(OutlineNodeViewModel? node)
    {
        if (node is { CanNavigate: true })
        {
            NavigateRequested?.Invoke(this, node.Item.PageIndex);
        }
    }
}
