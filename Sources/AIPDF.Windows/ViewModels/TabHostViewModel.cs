// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>标签类别：文档标签（阅读）或临时工具标签（T7 通道承载现有工具页）。</summary>
public enum TabKind
{
    Document,
    Tool,
}

/// <summary>
/// 单个标签的视图模型：持有标题、路径/工具标识与页面实例【D16】。
/// 标签关闭前实例不重建——切换标签不丢工具页状态（PRD 4.6.2）。
/// </summary>
public partial class DocumentTabViewModel : ObservableObject
{
    /// <summary>标签唯一标识（T7 实例缓存字典的键预留）。</summary>
    public string TabId { get; } = Guid.NewGuid().ToString("N");

    public TabKind Kind { get; }

    /// <summary>标签页头显示名：文档为文件名，工具为工具名（FR-07）。</summary>
    public string Title { get; }

    /// <summary>文档标签的规范化全路径（去重键【D14】）；工具标签为 null。</summary>
    public string? FilePath { get; }

    /// <summary>工具标签对应的工具；文档标签为 null。</summary>
    public Tool? Tool { get; }

    /// <summary>标签页头悬停提示：文档为完整路径（FR-07 长文件名悬停完整显示）。</summary>
    public string? ToolTipText => FilePath ?? Title;

    /// <summary>
    /// 标签承载的页面实例。文档标签 = ReaderView 占位（T3/T7 接线）；工具标签 = ToolPageRegistry 页面。
    /// </summary>
    [ObservableProperty]
    private object? _content;

    /// <summary>标签页头图标（Segoe MDL2 Assets 字形）。</summary>
    public string KindGlyph => Kind == TabKind.Document ? "\uE8A5" : "\uE70F";

    /// <summary>文档标签构造：路径须为规范化全路径（由宿主统一处理）。</summary>
    public DocumentTabViewModel(string fullPath, string title, object? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        Kind = TabKind.Document;
        FilePath = fullPath;
        Title = title;
        Content = content;
    }

    /// <summary>工具标签构造。</summary>
    public DocumentTabViewModel(Tool tool, object? content)
    {
        Kind = TabKind.Tool;
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        Title = tool.Name;
        Content = content;
    }
}

/// <summary>
/// 标签宿主视图模型：标签集合、选中状态与多标签核心逻辑
/// （同路径去重激活【D14】、集合增删、邻位选择、工具实例缓存【D16】）。
/// 纯 VM，可脱离 UI 测试；页面内容经工厂委托惰性创建——重复打开不重建、不丢状态。
/// </summary>
public partial class TabHostViewModel : ObservableObject
{
    /// <summary>路径去重用比较器：Windows 路径大小写不敏感【D14】。</summary>
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private DocumentTabViewModel? _selectedTab;

    /// <summary>是否存在标签：驱动主区「标签内容 vs 起始页空态」与左栏显隐（PRD 2.3/2.4）。</summary>
    public bool HasTabs => Tabs.Count > 0;

    public TabHostViewModel()
    {
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>
    /// 打开文档标签。同一路径（规范化后忽略大小写）已有标签则仅激活现有标签【D14】，
    /// 不创建新标签也不调用内容工厂；否则新建并激活。返回激活的标签。
    /// </summary>
    public DocumentTabViewModel OpenDocument(string path, Func<object?>? contentFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        var existing = FindDocumentTab(fullPath);
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        var tab = new DocumentTabViewModel(fullPath, Path.GetFileName(fullPath), contentFactory?.Invoke());
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>
    /// 打开临时工具标签（T7 通道承载）。同 Id 工具已有标签则仅激活——
    /// 页面实例缓存【D16】：内容工厂只在新建时调用，切标签不重建、不丢状态。
    /// </summary>
    public DocumentTabViewModel EnsureToolTab(Tool tool, Func<object?>? contentFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var existing = Tabs.FirstOrDefault(t =>
            t.Kind == TabKind.Tool && string.Equals(t.Tool?.Id, tool.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        var tab = new DocumentTabViewModel(tool, contentFactory?.Invoke());
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>
    /// 关闭前异步拦截（V2 钩子，落实 :139）：返回 false 则取消本次关闭。
    /// 宿主在此强制 flush 未保存批注（跳过防抖）；flush 失败返回 false 阻止关闭并提示【ST-22】。
    /// </summary>
    public Func<DocumentTabViewModel, Task<bool>>? BeforeCloseAsync { get; set; }

    /// <summary>
    /// 关闭标签并选择邻位标签（优先右侧，末位则左邻）。
    /// V2：先经 <see cref="BeforeCloseAsync"/> 异步门禁（未保存批注强制保存），通过后再移除。
    /// </summary>
    /// <returns>标签存在且被移除时 true；被门禁拦截或标签不存在时 false。</returns>
    public async Task<bool> CloseTabAsync(DocumentTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return false;
        }

        if (BeforeCloseAsync is not null && !await BeforeCloseAsync(tab).ConfigureAwait(true))
        {
            return false;
        }

        return CloseTab(tab);
    }

    /// <summary>
    /// 关闭标签并选择邻位标签（优先右侧，末位则左邻）。
    /// V2 挂点已落实为 <see cref="CloseTabAsync"/> + <see cref="BeforeCloseAsync"/>；
    /// 本同步方法供已确认可关闭的路径与既有测试直接调用。
    /// </summary>
    /// <returns>标签存在且被移除时 true。</returns>
    public bool CloseTab(DocumentTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return false;
        }

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        return true;
    }

    private DocumentTabViewModel? FindDocumentTab(string fullPath)
        => Tabs.FirstOrDefault(t => t.Kind == TabKind.Document && PathComparer.Equals(t.FilePath, fullPath));
}
