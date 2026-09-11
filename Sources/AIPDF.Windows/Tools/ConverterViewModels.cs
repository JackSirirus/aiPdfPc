// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AIPDF.Windows.Tools;

/// <summary>
/// 转换组依赖探测：镜像 backend/engine.py 的 find_office() win32 探测链与 Playwright
/// 默认浏览器目录布局。UI 只做执行前的明确提示，不在界面层安装任何组件；
/// 引擎仍是依赖判定的权威（真实执行时引擎报错原文优先展示）。
/// </summary>
public static class ConverterDependencyChecks
{
    /// <summary>测试接缝：返回 soffice 可执行文件路径或 null。</summary>
    public static Func<string?> OfficeLocator { get; set; } = LocateOffice;

    /// <summary>测试接缝：返回 Playwright Chromium 浏览器目录或 null。</summary>
    public static Func<string?> ChromiumLocator { get; set; } = LocateChromium;

    public const string OfficeMissingMessage =
        "此工具需要 LibreOffice。当前未检测到 LibreOffice，请安装后重试" +
        "（例如安装到 C:\\Program Files\\LibreOffice；本应用不会自动安装组件）。";

    public const string ChromiumMissingMessage =
        "网址转 PDF 需要 Playwright 的 Chromium 浏览器。当前未检测到，" +
        "请在项目 .venv 中执行 python -m playwright install chromium 后重试（本应用不会自动下载浏览器）。";

    /// <summary>Office 组（Word/PPT/Excel→PDF、PDF/A）依赖提示；null 表示检测到 LibreOffice。</summary>
    public static string? CheckOffice() => OfficeLocator() is null ? OfficeMissingMessage : null;

    /// <summary>
    /// 网址转 PDF 依赖提示；null 表示检测到 Chromium。
    /// PLAYWRIGHT_BROWSERS_PATH=0 表示浏览器随包安装，UI 侧无法可靠探测：不拦截，交给引擎权威判定。
    /// </summary>
    public static string? CheckChromium()
    {
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") == "0")
        {
            return null;
        }

        return ChromiumLocator() is null ? ChromiumMissingMessage : null;
    }

    /// <summary>镜像 engine.find_office() 的 win32 优先级：AIPDF_OFFICE → Program Files → Program Files (x86) → PATH。</summary>
    internal static string? LocateOffice()
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("AIPDF_OFFICE"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Which("soffice.exe");
    }

    /// <summary>镜像 shutil.which：按 PATH 顺序探测（忽略非法条目）。</summary>
    internal static string? Which(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var dir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中的非法条目：跳过
            }
        }

        return null;
    }

    /// <summary>Playwright 默认注册目录（%LOCALAPPDATA%\ms-playwright）下的 chromium-* 子目录。</summary>
    internal static string? LocateChromium()
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(root, "chromium-*").FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>工具页文件条目。只保存路径，不读取内容（大文件纪律：内容一律由引擎子进程按需读取）。</summary>
public sealed class ConverterFileItem
{
    public ConverterFileItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>结果列表条目：引擎输出文件路径（位于新建任务子目录内）。</summary>
public sealed class ConverterResultItem
{
    public ConverterResultItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 转换组工具页视图模型基类。与 PageOrganizerViewModelBase 同构的组内基类（刻意不跨组继承，
/// 避免并行任务间的文件耦合）：文件输入（拖拽/浏览统一入口 AddFiles）→ 参数表单（子类）→
/// 输出目录 → 执行/取消 → 进度与结果。引擎调用直接走注入的 EngineBridge；
/// 依赖缺失在构造时探测为 DependencyWarning，执行前校验拦截，引擎仍是权威。
/// </summary>
public abstract partial class ConverterToolViewModelBase : ObservableObject, IToolDocumentReceiver, ICurrentDocumentPinnable
{
    private static readonly Regex PagesItemPattern = new(@"^\s*\d+\s*(?:-\s*\d+\s*)?$", RegexOptions.Compiled);

    private readonly EngineBridge? _bridge;
    private CancellationTokenSource? _cancellation;

    protected ConverterToolViewModelBase(EngineBridge? bridge, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _bridge = bridge;
        Tool = tool;
        FileDialogFilter = BuildFilter(tool);

        // 子类实现只做静态探测，不访问实例状态：构造期调用安全。
        DependencyWarning = CheckDependency();
        Files.CollectionChanged += OnFilesCollectionChanged;
    }

    public Tool Tool { get; }

    /// <summary>输入文件（顺序即传参顺序）。</summary>
    public ObservableCollection<ConverterFileItem> Files { get; } = [];

    /// <summary>引擎输出文件（位于新建任务子目录内）。</summary>
    public ObservableCollection<ConverterResultItem> Results { get; } = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>是否支持多文件（决定文件选择语义）。</summary>
    public bool SupportsMultipleFiles => Tool.Multiple;

    // ---------- 浮菜单「用工具处理」置顶/预填（V2 T2） ----------

    /// <summary>被浮菜单固定的当前文档路径（null = 无）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedDocument))]
    [NotifyPropertyChangedFor(nameof(PinnedDocumentLabel))]
    private string? _pinnedDocumentPath;

    /// <summary>是否有「当前文档」标记（XAML 徽标显隐）。</summary>
    public bool HasPinnedDocument => !string.IsNullOrEmpty(PinnedDocumentPath);

    /// <summary>「当前文档：文件名」标记文案。</summary>
    public string? PinnedDocumentLabel => HasPinnedDocument
        ? $"当前文档：{System.IO.Path.GetFileName(PinnedDocumentPath!)}"
        : null;

    /// <summary>是否有页范围字段可预填（基类默认无；pdf_to_word 无页范围）。</summary>
    public virtual bool SupportsPageRangePrefill => false;

    /// <summary>预填页范围（基类默认空实现）。</summary>
    public virtual void PrefillPageRange(int startPage, int endPage)
    {
    }

    /// <summary>把当前文档加入并置顶、标记「当前文档」（浮菜单子菜单入口）。</summary>
    public void PinCurrentDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return; }

        AddFiles([fullPath]);

        var item = Files.FirstOrDefault(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            var index = Files.IndexOf(item);
            if (index > 0) Files.Move(index, 0);
        }

        PinnedDocumentPath = fullPath;
    }

    public string FileDialogFilter { get; }

    public string ExtensionsHint => "支持格式：" + string.Join("、", Tool.Extensions);

    public string FileSummary => Files.Count == 0 ? "尚未添加文件" : $"已添加 {Files.Count} 个文件";

    /// <summary>依赖缺失提示（构造时探测一次）；null 表示本机依赖可用。</summary>
    public string? DependencyWarning { get; }

    [ObservableProperty]
    private string _outputDir = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _progressMessage = "";

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>最近一次引擎结果（测试断言用；页面状态以 Results/StatusMessage 为准）。</summary>
    public EngineResult? LastResult { get; private set; }

    public bool CanExecuteNow => !IsRunning && Validate() is null;

    /// <summary>当前校验消息（页面即时反馈）；null 表示可以执行。</summary>
    public string? ValidationMessage => Validate();

    // ---------- 命令 ----------

    [RelayCommand(CanExecute = nameof(CanExecuteNow))]
    public async Task ExecuteAsync()
    {
        if (IsRunning)
        {
            return; // 防重入：一次只跑一个引擎任务
        }

        var error = Validate();
        if (error is not null)
        {
            StatusMessage = error;
            return;
        }

        if (_bridge is null)
        {
            StatusMessage = "引擎未就绪：未配置执行桥。";
            return;
        }

        var request = new EngineRequest
        {
            Operation = Tool.Id,
            Files = [.. Files.Select(f => f.Path)],
            Options = new Dictionary<string, object?>(BuildOptions()),
            OutputDir = OutputDir.Trim(),
        };

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        ProgressFraction = 0;
        ProgressMessage = "";
        StatusMessage = $"正在执行 {Tool.Name}…";
        Results.Clear();
        _bridge.Progress += OnBridgeProgress;
        try
        {
            var result = await _bridge.ExecuteAsync(request, _cancellation.Token);
            LastResult = result;
            if (result.Ok)
            {
                foreach (var output in result.Outputs)
                {
                    Results.Add(new ConverterResultItem(output));
                }

                StatusMessage = $"完成 {Tool.Name}：{result.Outputs.Count} 个文件 → {result.OutputDir}";
            }
            else
            {
                StatusMessage = $"失败：{result.Error}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消。";
        }
        catch (EngineBridgeException bridgeError)
        {
            StatusMessage = $"引擎异常：{bridgeError.Message}";
        }
        finally
        {
            _bridge.Progress -= OnBridgeProgress;
            IsRunning = false;
            ProgressMessage = "";
            ProgressFraction = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"为{Tool.Name}选择文件",
            Filter = FileDialogFilter,
            Multiselect = SupportsMultipleFiles,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    [RelayCommand]
    private void BrowseOutputDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择输出文件夹",
        };
        if (dialog.ShowDialog() == true)
        {
            OutputDir = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void RemoveFile(ConverterFileItem? item)
    {
        if (item is not null && !IsRunning && Files.Remove(item))
        {
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(FileSummary));
        }
    }

    [RelayCommand]
    private void ClearFiles()
    {
        if (IsRunning)
        {
            return;
        }

        Files.Clear();
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileSummary));
    }

    // ---------- 文件输入 ----------

    /// <summary>拖拽 / 浏览的统一入口。按工具扩展名过滤、去重；单文件工具以新文件替换旧文件。</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsRunning)
        {
            return;
        }

        var allowed = Tool.Extensions.Select(e => e.ToLowerInvariant()).ToHashSet();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path;
            try
            {
                path = System.IO.Path.GetFullPath(raw);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                continue; // 路径字符非法：忽略，交由用户重新选择
            }

            var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!allowed.Contains(extension))
            {
                continue; // 与引擎扩展名校验同源，越界文件不入列
            }

            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!SupportsMultipleFiles)
            {
                Files.Clear();
            }

            Files.Add(new ConverterFileItem(path));
            if (OutputDir.Trim().Length == 0)
            {
                // 默认输出目录 = 源文件旁（引擎在其下新建任务子目录，不覆盖源文件）
                OutputDir = System.IO.Path.GetDirectoryName(path) ?? "";
            }
        }

        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileSummary));
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(ValidationMessage));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputDirChanged(string value)
    {
        OnPropertyChanged(nameof(ValidationMessage));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>子类在选项变化时调用，刷新校验消息与执行按钮状态。</summary>
    protected void RefreshValidation()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void OnBridgeProgress(object? sender, EngineProgressEventArgs e)
    {
        // 进度事件来自引擎子进程的读循环线程；WPF 绑定要求 UI 线程，测试环境没有 Application。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ProgressMessage = e.Message;
            ProgressFraction = e.Fraction;
        }
        else
        {
            dispatcher.InvokeAsync(() =>
            {
                ProgressMessage = e.Message;
                ProgressFraction = e.Fraction;
            });
        }
    }

    // ---------- 参数与校验（子类实现） ----------

    /// <summary>本组依赖探测（Office/浏览器）；null 表示可用。默认无依赖。</summary>
    protected virtual string? CheckDependency() => null;

    /// <summary>允许在没有文件时执行（网址转 PDF 的 URL 模式）。默认必须有文件。</summary>
    protected virtual bool CanRunWithoutFiles => false;

    /// <summary>选项级校验（在输入与输出目录校验之后）。</summary>
    protected virtual string? ValidateOptions() => null;

    /// <summary>输入级校验（文件或等价输入）；子类可改写提示文案。</summary>
    protected virtual string? ValidateInput()
        => Files.Count == 0 && !CanRunWithoutFiles ? "请先选择文件。" : null;

    /// <summary>执行前校验。返回错误文案（不发请求）；null 放行。文案与引擎语义对齐，提前拦截省一次引擎往返。</summary>
    protected virtual string? Validate()
    {
        // 依赖缺失优先提示：这是用户最先要解决的事。
        if (DependencyWarning is not null)
        {
            return DependencyWarning;
        }

        return ValidateInput()
               ?? ((!Tool.Multiple && Files.Count > 1) ? "此工具一次处理一份文件。" : null)
               ?? ValidateOutputDir()
               ?? ValidateOptions();
    }

    private string? ValidateOutputDir()
    {
        var output = OutputDir.Trim();
        return output.Length == 0 || !Path.IsPathRooted(output) || !Directory.Exists(output)
            ? "请选择有效的输出文件夹。"
            : null;
    }

    /// <summary>表单状态 → 引擎 options。值一律为字符串（引擎把目录默认值与请求选项按字符串合并）。</summary>
    protected abstract IReadOnlyDictionary<string, object?> BuildOptions();

    // ---------- catalog 辅助（字段值与选项列表的唯一来源是 backend/catalog.json） ----------

    /// <summary>取目录字段默认值（catalog 权威）。</summary>
    protected static string FieldValue(Tool tool, string key)
        => tool.Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";

    /// <summary>取 choice 字段候选（catalog 权威）；目录缺失时回退到 fallback。</summary>
    protected static IReadOnlyList<string> FieldChoices(Tool tool, string key, IReadOnlyList<string> fallback)
        => tool.Fields.FirstOrDefault(f => f.Key == key)?.Choices is { Count: > 0 } choices ? choices : fallback;

    /// <summary>校验页面范围：, 或 ，分隔的 "数字" 或 "a-b"，镜像 parse_pages 的单项正则。</summary>
    protected static string? ValidatePagesSpec(string spec)
    {
        var normalized = spec.Replace('，', ',');
        foreach (var item in normalized.Split(','))
        {
            if (item.Length > 0 && !PagesItemPattern.IsMatch(item))
            {
                return "页码格式不正确，请输入类似 1-3,5 的范围。";
            }
        }

        return null;
    }

    private static string BuildFilter(Tool tool)
    {
        var extensions = tool.Extensions;
        var pattern = string.Join(";", extensions.Select(e => "*." + e.ToLowerInvariant()));
        var label = extensions.Contains("pdf", StringComparer.OrdinalIgnoreCase) ? "PDF 文件" : "图片与文档";
        return $"{label}|{pattern}|所有文件|*.*";
    }
}

// ---------- 具体工具（每工具一个 VM；参数语义以 backend/catalog.py 与 engine.py 为准） ----------

/// <summary>图片转 PDF：多选图片装订成册；页面尺寸 / 方向 / 页边距（点）。</summary>
public sealed partial class ImagesToPdfViewModel : ConverterToolViewModelBase
{
    public ImagesToPdfViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        PageSizeChoices = FieldChoices(tool, "pageSize", ["A4", "Letter", "image"]);
        OrientationChoices = FieldChoices(tool, "orientation", ["portrait", "landscape"]);
        _pageSize = FieldValue(tool, "pageSize") is { Length: > 0 } size ? size : PageSizeChoices[0];
        _orientation = FieldValue(tool, "orientation") is { Length: > 0 } orientation ? orientation : OrientationChoices[0];
        _marginText = FieldValue(tool, "margin") is { Length: > 0 } margin ? margin : "24";
    }

    public IReadOnlyList<string> PageSizeChoices { get; }

    public IReadOnlyList<string> OrientationChoices { get; }

    [ObservableProperty]
    private string _pageSize;

    [ObservableProperty]
    private string _orientation;

    [ObservableProperty]
    private string _marginText;

    partial void OnPageSizeChanged(string value) => RefreshValidation();

    partial void OnOrientationChanged(string value) => RefreshValidation();

    partial void OnMarginTextChanged(string value) => RefreshValidation();

    protected override string? ValidateOptions()
    {
        // 引擎 number(options,"margin",24, minimum:0)：非数字或负数都会被引擎拒绝，这里提前拦截。
        return double.TryParse(MarginText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var margin) && margin >= 0
            ? null
            : "页边距必须是 0 以上的数字（单位：点）。";
    }

    protected override IReadOnlyDictionary<string, object?> BuildOptions()
    {
        var margin = double.Parse(MarginText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return new Dictionary<string, object?>
        {
            ["pageSize"] = PageSize,
            ["orientation"] = Orientation,
            ["margin"] = margin.ToString("0.##", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>PDF 转图片：逐页导出或提取嵌入图片；页面范围 / 导出内容 / 格式 / 分辨率。</summary>
public sealed partial class PdfToImagesViewModel : ConverterToolViewModelBase
{
    public PdfToImagesViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        ModeChoices = FieldChoices(tool, "mode", ["pages", "embedded"]);
        FormatChoices = FieldChoices(tool, "format", ["jpg", "png"]);
        DpiChoices = FieldChoices(tool, "dpi", ["72", "144", "216", "300"]);
        _pagesText = FieldValue(tool, "pages");
        _mode = FieldValue(tool, "mode") is { Length: > 0 } mode ? mode : ModeChoices[0];
        _format = FieldValue(tool, "format") is { Length: > 0 } format ? format : FormatChoices[0];
        _dpiText = FieldValue(tool, "dpi") is { Length: > 0 } dpi ? dpi : DpiChoices[1];
    }

    public IReadOnlyList<string> ModeChoices { get; }

    public IReadOnlyList<string> FormatChoices { get; }

    public IReadOnlyList<string> DpiChoices { get; }

    [ObservableProperty]
    private string _pagesText;

    [ObservableProperty]
    private string _mode;

    [ObservableProperty]
    private string _format;

    [ObservableProperty]
    private string _dpiText;

    /// <summary>页面范围提示（catalog 帮助文案）。</summary>
    public string PagesHelp => Tool.Fields.FirstOrDefault(f => f.Key == "pages")?.Help ?? "";

    partial void OnPagesTextChanged(string value) => RefreshValidation();

    partial void OnModeChanged(string value) => RefreshValidation();

    partial void OnFormatChanged(string value) => RefreshValidation();

    partial void OnDpiTextChanged(string value) => RefreshValidation();

    protected override string? ValidateOptions()
        => PagesText.Trim().Length == 0 ? null : ValidatePagesSpec(PagesText);

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>
    {
        ["pages"] = PagesText.Trim(),
        ["mode"] = Mode,
        ["format"] = Format,
        ["dpi"] = DpiText,
    };
}

/// <summary>Word 转 PDF：依赖 LibreOffice，无参数。</summary>
public sealed partial class WordToPdfViewModel : ConverterToolViewModelBase
{
    public WordToPdfViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    protected override string? CheckDependency() => ConverterDependencyChecks.CheckOffice();

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>();
}

/// <summary>PowerPoint 转 PDF：依赖 LibreOffice，无参数。</summary>
public sealed partial class PptToPdfViewModel : ConverterToolViewModelBase
{
    public PptToPdfViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    protected override string? CheckDependency() => ConverterDependencyChecks.CheckOffice();

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>();
}

/// <summary>Excel 转 PDF：依赖 LibreOffice，遵循源工作簿打印设置，无参数。</summary>
public sealed partial class ExcelToPdfViewModel : ConverterToolViewModelBase
{
    public ExcelToPdfViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    protected override string? CheckDependency() => ConverterDependencyChecks.CheckOffice();

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>();
}

/// <summary>PDF 转 Word：可编辑重排或外观嵌入；一次处理一份。</summary>
public sealed partial class PdfToWordViewModel : ConverterToolViewModelBase
{
    public PdfToWordViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        ModeChoices = FieldChoices(tool, "mode", ["editable", "visual"]);
        _mode = FieldValue(tool, "mode") is { Length: > 0 } mode ? mode : ModeChoices[0];
    }

    public IReadOnlyList<string> ModeChoices { get; }

    [ObservableProperty]
    private string _mode;

    partial void OnModeChanged(string value) => RefreshValidation();

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>
    {
        ["mode"] = Mode,
    };
}

/// <summary>PDF 转 PowerPoint：外观幻灯片或可编辑重建。</summary>
public sealed partial class PdfToPptViewModel : ConverterToolViewModelBase
{
    public PdfToPptViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        ModeChoices = FieldChoices(tool, "mode", ["visual", "editable"]);
        _mode = FieldValue(tool, "mode") is { Length: > 0 } mode ? mode : ModeChoices[0];
    }

    public IReadOnlyList<string> ModeChoices { get; }

    [ObservableProperty]
    private string _mode;

    partial void OnModeChanged(string value) => RefreshValidation();

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>
    {
        ["mode"] = Mode,
    };
}

/// <summary>PDF 转 Excel：按检测到的表格建表，无参数。</summary>
public sealed partial class PdfToExcelViewModel : ConverterToolViewModelBase
{
    public PdfToExcelViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>();
}

/// <summary>
/// HTML 转 PDF：本地 HTML 文件或网址（二选一，网址优先）。网址格式与 engine.dispatch
/// 同一条规则（^https?://\S+$）；依赖 Playwright Chromium。
/// </summary>
public sealed partial class HtmlToPdfViewModel : ConverterToolViewModelBase
{
    private static readonly Regex UrlPattern = new("^https?://\\S+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public HtmlToPdfViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _urlText = FieldValue(tool, "url");
    }

    [ObservableProperty]
    private string _urlText;

    /// <summary>网址帮助文案（catalog 权威）。</summary>
    public string UrlHelp => Tool.Fields.FirstOrDefault(f => f.Key == "url")?.Help ?? "";

    partial void OnUrlTextChanged(string value) => RefreshValidation();

    /// <summary>填写了格式正确的网址：允许没有文件（引擎以网址为输入）。</summary>
    public bool HasUsableUrl => !string.IsNullOrWhiteSpace(UrlText) && UrlPattern.IsMatch(UrlText.Trim());

    /// <summary>填写了网址但格式不对时的行内错误提示（与引擎文案一致）。</summary>
    public string? UrlError
        => string.IsNullOrWhiteSpace(UrlText) || HasUsableUrl
            ? null
            : "网页地址必须以 https:// 或 http:// 开头。";

    protected override bool CanRunWithoutFiles => HasUsableUrl;

    protected override string? CheckDependency() => ConverterDependencyChecks.CheckChromium();

    protected override string? ValidateInput()
    {
        if (Files.Count > 0)
        {
            return null;
        }

        return UrlText.Trim().Length == 0 ? "请先选择 HTML 文件，或填写网页地址。" : UrlError;
    }

    protected override string? ValidateOptions() => UrlError;

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>
    {
        // 只发送修剪后的网址；空串等价于未填写（引擎按空 url 处理，走本地文件分支）。
        ["url"] = UrlText.Trim(),
    };
}

/// <summary>PDF 转 PDF/A：经 LibreOffice Draw 导出，可选 1b/2b/3b。</summary>
public sealed partial class PdfaViewModel : ConverterToolViewModelBase
{
    public PdfaViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        LevelChoices = FieldChoices(tool, "pdfaLevel", ["1b", "2b", "3b"]);
        _level = FieldValue(tool, "pdfaLevel") is { Length: > 0 } level ? level : LevelChoices[1];
    }

    public IReadOnlyList<string> LevelChoices { get; }

    [ObservableProperty]
    private string _level;

    partial void OnLevelChanged(string value) => RefreshValidation();

    protected override string? CheckDependency() => ConverterDependencyChecks.CheckOffice();

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>
    {
        ["pdfaLevel"] = Level,
    };
}

/// <summary>PDF 转 Markdown：提取文字、标题、链接、表格与图片；页面范围可选。</summary>
public sealed partial class MarkdownViewModel : ConverterToolViewModelBase
{
    public MarkdownViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _pagesText = FieldValue(tool, "pages");
    }

    [ObservableProperty]
    private string _pagesText;

    /// <summary>页面范围提示（catalog 帮助文案）。</summary>
    public string PagesHelp => Tool.Fields.FirstOrDefault(f => f.Key == "pages")?.Help ?? "";

    partial void OnPagesTextChanged(string value) => RefreshValidation();

    protected override string? ValidateOptions()
        => PagesText.Trim().Length == 0 ? null : ValidatePagesSpec(PagesText);

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>
    {
        ["pages"] = PagesText.Trim(),
    };
}

/// <summary>
/// 转换组视图模型工厂：tool id → 页面视图模型。Views/Converter/ConverterPages.cs
/// 据此创建页面并注册进 ToolPageRegistry；非转换组工具返回 null。
/// </summary>
public static class ConverterToolPages
{
    public static ConverterToolViewModelBase? CreateViewModel(Tool tool, EngineBridge? bridge) => tool.Id switch
    {
        "images_to_pdf" => new ImagesToPdfViewModel(bridge, tool),
        "pdf_to_images" => new PdfToImagesViewModel(bridge, tool),
        "word_to_pdf" => new WordToPdfViewModel(bridge, tool),
        "ppt_to_pdf" => new PptToPdfViewModel(bridge, tool),
        "excel_to_pdf" => new ExcelToPdfViewModel(bridge, tool),
        "pdf_to_word" => new PdfToWordViewModel(bridge, tool),
        "pdf_to_ppt" => new PdfToPptViewModel(bridge, tool),
        "pdf_to_excel" => new PdfToExcelViewModel(bridge, tool),
        "html_to_pdf" => new HtmlToPdfViewModel(bridge, tool),
        "pdfa" => new PdfaViewModel(bridge, tool),
        "markdown" => new MarkdownViewModel(bridge, tool),
        _ => null,
    };
}
