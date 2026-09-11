// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.IO;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>最近文件卡片视图模型：包装存储条目并提供失效标注【ST-05】所需的可观察状态。</summary>
public partial class RecentFileItem : ObservableObject
{
    public RecentFileItem(RecentFileEntry entry)
    {
        Entry = entry;
    }

    public RecentFileEntry Entry { get; }

    public string Path => Entry.Path;

    /// <summary>卡片显示名：文件名。</summary>
    public string Name => Entry.Name;

    public string DisplayDate => Entry.LastOpenedAt.ToString("yyyy/M/d HH:mm");

    // ---- V2 进度元信息（FR-33 / AC-33.2）：页数 · 体积 · 上次阅读进度 ----

    /// <summary>文档页数（0 = 未知）。</summary>
    public int PageCount => Entry.PageCount;

    /// <summary>文件字节数（0 = 未知）。</summary>
    public long Bytes => Entry.Bytes;

    /// <summary>上次阅读页（1 基；0 = 未知）。</summary>
    public int LastPage => Entry.LastPage;

    /// <summary>上次缩放百分比（0 = 未知）。</summary>
    public double LastZoom => Entry.LastZoom;

    /// <summary>是否具备页数/体积元信息（控制卡片元信息行显隐）。</summary>
    public bool HasMeta => Entry.PageCount > 0 || Entry.Bytes > 0;

    /// <summary>是否具备阅读进度（控制进度行显隐）。</summary>
    public bool HasProgress => Entry.LastPage > 0;

    /// <summary>元信息行：页数 · 体积（如「120 页 · 2.4 MB」）。无数据返回空串。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>(2);
            if (Entry.PageCount > 0) parts.Add($"{Entry.PageCount} 页");
            if (Entry.Bytes > 0) parts.Add(FormatBytes(Entry.Bytes));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>阅读进度行：上次读到第 N 页 · Z%（如「上次读到第 47 页 · 150%」）。无数据返回空串。</summary>
    public string ProgressText
    {
        get
        {
            if (Entry.LastPage <= 0) return "";
            var text = $"上次读到第 {Entry.LastPage} 页";
            if (Entry.LastZoom > 0) text += $" · {Entry.LastZoom.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%";
            return text;
        }
    }

    /// <summary>字节数人性化（B/KB/MB/GB，保留 1 位小数）。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>文件是否仍存在。缺失 → 卡片置灰 + 划线 + 「文件不存在」标注（不自动删除，PRD FR-34）。</summary>
    [ObservableProperty]
    private bool _exists = true;
}

/// <summary>
/// 起始页视图模型（PRD 3.10，FR-33/34/35）：打开入口 + 最近文件卡片 + 常用工具网格 + 全部工具入口。
/// 纯 VM，可脱离 UI 测试；文件对话框、拖拽事件与 Toast 动效由视图/宿主接线。
/// </summary>
public partial class StartPageViewModel : ObservableObject
{
    /// <summary>常用工具网格静态清单【R19】——与 PRD 3.10 转录清单一致的 8 个工具 id，不做动态排序/频率统计。</summary>
    private static readonly string[] FavoriteToolIds =
    [
        "merge", // 合并 PDF
        "compress", // 压缩 PDF
        "split", // 拆分 PDF
        "ocr", // OCR 识别
        "pdf_to_word", // 转 Word
        "protect", // 加密
        "watermark", // 水印
        "workflow", // 工作流程
    ];

    private readonly RecentFilesStore _store;

    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];

    /// <summary>目录中的全部工具（顺序与 catalog.json 一致），驱动「全部工具」完整入口。</summary>
    public IReadOnlyList<Tool> Tools { get; }

    /// <summary>按 category 分组（组序 = 目录首次出现顺序），数据源 = backend/catalog.json（只读消费）。</summary>
    public IReadOnlyList<ToolGroup> ToolGroups { get; }

    public IReadOnlyList<Tool> FavoriteTools { get; }

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public StartPageViewModel(IEnumerable<Tool> catalog, RecentFilesStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _store = store ?? new RecentFilesStore();

        var tools = catalog as IReadOnlyList<Tool> ?? [.. catalog];
        Tools = tools;
        ToolGroups = ToolCatalog.GroupByCategory(tools);
        FavoriteTools = [.. FavoriteToolIds
            .Select(id => tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal)))
            .Where(tool => tool is not null)
            .Select(tool => tool!)];

        ReloadRecentFiles();
    }

    /// <summary>
    /// 重新从存储加载最近列表（T5：标签关闭/退出时同步进度元信息后刷新起始页卡片，
    /// 使页数/体积/阅读进度即时反映；不动清空二次确认状态）。
    /// </summary>
    public void ReloadRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var entry in _store.Load().Items)
        {
            RecentFiles.Add(new RecentFileItem(entry));
        }

        RefreshExistence();
        OnPropertyChanged(nameof(HasRecentFiles));
    }
    /// <summary>点击打开按钮 / 拖入合法 PDF / 点击最近卡片时触发；宿主负责建标签（TabHostViewModel.OpenDocument）。</summary>
    public event EventHandler<string>? DocumentOpenRequested;

    /// <summary>点击工具（常用网格或完整入口）时触发；宿主走既有临时工具标签流（T5 ShowToolPage → EnsureToolTab）。</summary>
    public event EventHandler<Tool>? ToolRequested;

    [ObservableProperty]
    private bool _isAllToolsExpanded;

    /// <summary>「全部工具」展开/收起链接文案（PRD FR-35：「全部 32 个工具 →」）。</summary>
    public string AllToolsToggleText => IsAllToolsExpanded ? "收起全部工具" : $"全部 {Tools.Count} 个工具 →";

    partial void OnIsAllToolsExpandedChanged(bool value) => OnPropertyChanged(nameof(AllToolsToggleText));

    /// <summary>清空二次确认：请求后待确认，确认才真正清空（PRD FR-34）。</summary>
    [ObservableProperty]
    private bool _isClearPending;

    /// <summary>轻量提示（Toast 语义）：ST-07 拒绝文案 / ST-05 文件失效；动效由视图处理。</summary>
    [ObservableProperty]
    private string? _toastMessage;

    /// <summary>文件对话框选中文件（或顶栏 ＋ 打开）后由宿主调用：记录到最近列表头部并持久化。</summary>
    public void RegisterOpened(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var existing = FindByPath(fullPath);
        if (existing is not null)
        {
            // 重复文件移至头部【D15 去重语义】；同步为本次打开的路径拼写与文件名（Windows 大小写不敏感）
            RecentFiles.Remove(existing);
            existing.Entry.Path = fullPath;
            existing.Entry.Name = Path.GetFileName(fullPath);
            existing.Entry.LastOpenedAt = DateTime.Now;
            RecentFiles.Insert(0, existing);
        }
        else
        {
            RecentFiles.Insert(0, new RecentFileItem(new RecentFileEntry
            {
                Path = fullPath,
                Name = Path.GetFileName(fullPath),
                LastOpenedAt = DateTime.Now,
            }));
        }

        TrimToCap();
        _store.Save(ToDocument());
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>文件对话框选中文件（或顶栏 ＋ 打开）后由宿主调用：记录到最近列表头部并请求打开。</summary>
    public void OpenPickedFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            ShowToast($"文件不存在或已被移动：{Path.GetFileName(path)}");
            return;
        }

        RegisterOpened(path);
        DocumentOpenRequested?.Invoke(this, Path.GetFullPath(path));
    }

    /// <summary>点击最近卡片：文件存在 → 上浮 + 请求打开；失效 → Toast【ST-05】，不自动删除。</summary>
    public bool TryOpenRecent(RecentFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!File.Exists(item.Path))
        {
            ShowToast($"文件不存在或已被移动：{item.Name}");
            return false;
        }

        RegisterOpened(item.Path);
        DocumentOpenRequested?.Invoke(this, item.Path);
        return true;
    }

    /// <summary>拖拽落区入口：仅接受 .pdf 文件；文件夹与非 PDF → Toast 拒绝【ST-07】。</summary>
    public void HandleDroppedPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var accepted = 0;
        var rejected = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                accepted++;
                DocumentOpenRequested?.Invoke(this, Path.GetFullPath(path));
            }
            else
            {
                rejected++; // 文件夹 / 非 PDF
            }
        }

        if (rejected > 0)
        {
            ShowToast("仅支持 PDF 文件");
        }
    }

    /// <summary>启动时批量校验存在性：缺失条目置灰标注，不自动删除（可能是临时断开的网络盘，PRD FR-34）。</summary>
    public void RefreshExistence()
    {
        foreach (var item in RecentFiles)
        {
            item.Exists = File.Exists(item.Path);
        }
    }

    /// <summary>从最近列表移除单条（卡片 ✕ / 失效卡片的移除入口）。</summary>
    public void RemoveRecent(RecentFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!RecentFiles.Remove(item))
        {
            return;
        }

        _store.Save(ToDocument());
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>「清除记录」→ 进入待确认态（确认 UI 由视图提供）。</summary>
    public void RequestClear()
    {
        if (RecentFiles.Count > 0)
        {
            IsClearPending = true;
        }
    }

    /// <summary>确认清空：清空列表 + 持久化空文档（阅读进度 V2 落地后一并清除，PRD FR-34）。</summary>
    public void ConfirmClear()
    {
        IsClearPending = false;
        if (RecentFiles.Count == 0)
        {
            return;
        }

        RecentFiles.Clear();
        _store.Save(ToDocument());
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>取消清空确认。</summary>
    public void CancelClear() => IsClearPending = false;

    /// <summary>点击工具（常用网格 / 完整入口统一走此事件，T7 后续重路由单文件工具到右抽屉）。</summary>
    public void OpenTool(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ToolRequested?.Invoke(this, tool);
    }

    private RecentFileItem? FindByPath(string fullPath)
        => RecentFiles.FirstOrDefault(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase));

    private void TrimToCap()
    {
        while (RecentFiles.Count > RecentFilesStore.MaxEntries)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1); // 淘汰最旧
        }
    }

    private RecentFilesDocument ToDocument() => new()
    {
        Items = [.. RecentFiles.Select(item => item.Entry)],
    };

    private void ShowToast(string message) => ToastMessage = message;
}
