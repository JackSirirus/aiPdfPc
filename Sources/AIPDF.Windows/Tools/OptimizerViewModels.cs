// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AIPDF.Windows.Tools;

/// <summary>优化文件组的输入文件条目（只保留路径；展示名按需读取）。</summary>
public sealed class OptimizerFileItem
{
    public OptimizerFileItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>优化文件组的输出文件条目（引擎新建任务目录内的产物）。</summary>
public sealed class OptimizerResultItem
{
    public OptimizerResultItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 优化文件组（压缩 / 修复 / OCR）视图模型基类：文件输入（拖拽 + 对话框）→ 参数 → 输出新目录
/// → 执行/取消 → 进度。结构镜像 PageOrganizerViewModelBase（T14 约定）：VM 直接持有桥与工具条目，
/// 引擎返回新任务目录（AIPDF-日期-编号），源文件保持不变。
/// 文件条目为本组自包含定义（与 T14 的 PageFileItem/PageResultItem 同构，便于终审合并）。
/// </summary>
public abstract partial class OptimizerViewModelBase : ObservableObject
{
    private readonly EngineBridge? _bridge;
    private CancellationTokenSource? _cancellation;

    protected OptimizerViewModelBase(EngineBridge? bridge, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _bridge = bridge;
        Tool = tool;
        Files.CollectionChanged += OnFilesCollectionChanged;
    }

    public Tool Tool { get; }

    /// <summary>输入文件（顺序即处理顺序）。</summary>
    public ObservableCollection<OptimizerFileItem> Files { get; } = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>是否支持多文件（文件选择对话框的 Multiselect）。</summary>
    public bool SupportsMultipleFiles => Tool.Multiple;

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

    public ObservableCollection<OptimizerResultItem> Results { get; } = [];

    /// <summary>最近一次执行的原始引擎结果（取消/传输异常时为 null）。</summary>
    public EngineResult? LastResult { get; private set; }

    public string FileDialogFilter { get; } = "所有支持的文件|*.*";

    public bool CanExecuteNow => !IsRunning && Files.Count > 0;

    /// <summary>状态栏是否有内容（执行卡片的展示开关）。</summary>
    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>当前状态是否为失败/异常（错误配色展示引擎原文）。</summary>
    public bool IsErrorStatus => StatusMessage.StartsWith("失败", StringComparison.Ordinal)
        || StatusMessage.StartsWith("引擎异常", StringComparison.Ordinal);

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsErrorStatus));
    }

    // ---------- 命令 ----------

    [RelayCommand(CanExecute = nameof(CanExecuteNow))]
    private async Task ExecuteAsync()
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
                    Results.Add(new OptimizerResultItem(output));
                }

                StatusMessage = $"完成 {Tool.Name}：{result.Outputs.Count} 个文件 → {result.OutputDir}";
            }
            else
            {
                // 引擎业务错误（ok=false）按引擎原文展示，不抛异常、不崩溃。
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
    private void RemoveFile(OptimizerFileItem? item)
    {
        if (item is not null && !IsRunning && Files.Remove(item))
        {
            OnPropertyChanged(nameof(HasFiles));
        }
    }

    /// <summary>选择输出文件夹（引擎会在其下新建 AIPDF-日期-编号 任务子目录）。</summary>
    [RelayCommand]
    private void BrowseOutputDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择输出文件夹",
        };
        if (OutputDir.Trim().Length > 0 && Directory.Exists(OutputDir.Trim()))
        {
            dialog.InitialDirectory = OutputDir.Trim();
        }

        if (dialog.ShowDialog() == true)
        {
            OutputDir = dialog.FolderName;
        }
    }

    // ---------- 文件输入 ----------

    /// <summary>拖拽 / 对话框统一入口：按扩展名过滤、去重；首个文件默认其所在目录为输出位置。</summary>
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
                continue; // 路径字符串非法（防御），忽略用户没选中的项
            }

            var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!allowed.Contains(extension))
            {
                continue; // 不在允许的扩展名里，同源目录忽略越界文件
            }

            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!SupportsMultipleFiles)
            {
                Files.Clear();
            }

            Files.Add(new OptimizerFileItem(path));
            if (OutputDir.Trim().Length == 0)
            {
                // 默认输出目录 = 源文件所在目录（最终会在其下新建任务子目录，不覆盖源文件）
                OutputDir = System.IO.Path.GetDirectoryName(path) ?? "";
            }
        }

        OnPropertyChanged(nameof(HasFiles));
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFiles));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    // ---------- 参数与校验（子类实现） ----------

    /// <summary>执行前校验。返回完整（含标点）的错误文案；null 表示通过。</summary>
    protected virtual string? Validate()
    {
        if (Files.Count == 0)
        {
            return "请先选择文件。";
        }

        if (!Tool.Multiple && Files.Count > 1)
        {
            return "此工具一次处理一份文件。";
        }

        var output = OutputDir.Trim();
        if (output.Length == 0 || !Path.IsPathRooted(output) || !Directory.Exists(output))
        {
            return "请选择有效的输出文件夹。";
        }

        return null;
    }

    /// <summary>组装引擎 options（值一律字符串，与 catalog 默认值合并，多选项按字符串传递）。</summary>
    protected abstract IReadOnlyDictionary<string, object?> BuildOptions();

    /// <summary>取目录字段默认值（catalog 权威）。</summary>
    protected static string FieldValue(Tool tool, string key)
        => tool.Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";

    /// <summary>
    /// 与 backend/engine.py parse_pages 的文档语义一致：只允许空、整数或 a-b 范围，
    /// 以半角逗号分隔（全角逗号由引擎归一，这里只做提示级校验）。
    /// </summary>
    protected static string? ValidatePagesSpec(string spec)
    {
        var normalized = spec.Replace('，', ',');
        foreach (var item in normalized.Split(','))
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var dash = trimmed.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(trimmed, out _))
                {
                    return "页面格式不正确。请输入 1-3,5 形式的范围。";
                }
            }
            else
            {
                var head = trimmed[..dash].Trim();
                var tail = trimmed[(dash + 1)..].Trim();
                if (!int.TryParse(head, out _) || !int.TryParse(tail, out _))
                {
                    return "页面格式不正确。请输入 1-3,5 形式的范围。";
                }
            }
        }

        return null;
    }

    private void OnBridgeProgress(object? sender, EngineProgressEventArgs e)
    {
        // 进度事件来自子进程的读循环线程；WPF 要求 UI 线程，测试环境没有 Application。
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

    private static string BuildFilter(Tool tool)
    {
        var extensions = tool.Extensions;
        var pattern = string.Join(";", extensions.Select(e => "*." + e.ToLowerInvariant()));
        var label = extensions.Contains("pdf", StringComparer.OrdinalIgnoreCase) ? "PDF 文件" : "图片文件";
        return $"{label}|{pattern}";
    }
}

/// <summary>压缩方式的显示选项：value 与 backend/catalog.py 的 compress.quality choices 一致。</summary>
public sealed record CompressQualityOption(string Value, string Label, string Hint);

/// <summary>压缩 PDF：质量三档映射（无损 / 均衡 / 小文件），选项集合来自 catalog。</summary>
public sealed partial class CompressViewModel : OptimizerViewModelBase
{
    public CompressViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        // catalog 权威：choices 顺序即展示顺序，默认值来自字段 value。
        var qualityField = tool.Fields.FirstOrDefault(f => f.Key == "quality");
        QualityOptions = BuildQualityOptions(qualityField?.Choices);
        var defaultChoice = qualityField?.Value is { Length: > 0 } value ? value : "balanced";
        _selectedQuality = QualityOptions.FirstOrDefault(q => string.Equals(q.Value, defaultChoice, StringComparison.Ordinal))
            ?? QualityOptions.First(q => string.Equals(q.Value, "balanced", StringComparison.Ordinal));
    }

    public IReadOnlyList<CompressQualityOption> QualityOptions { get; }

    [ObservableProperty]
    private CompressQualityOption _selectedQuality;

    public string QualityHint => SelectedQuality.Hint;

    partial void OnSelectedQualityChanged(CompressQualityOption value) => OnPropertyChanged(nameof(QualityHint));

    /// <summary>按引擎值选择压缩方式；未知值视为超出允许范围并拒绝（范围校验）。</summary>
    public void SelectQuality(string value)
    {
        var match = QualityOptions.FirstOrDefault(q => string.Equals(q.Value, value, StringComparison.Ordinal))
            ?? throw new ArgumentOutOfRangeException(nameof(value), value, $"未知的压缩方式：{value}（允许：{string.Join("、", QualityOptions.Select(q => q.Value))}）");
        SelectedQuality = match;
    }

    protected override IReadOnlyDictionary<string, object?> BuildOptions()
        => new Dictionary<string, object?> { ["quality"] = SelectedQuality.Value };

    /// <summary>把 catalog choices 映射为显示选项；未知的新档位回退显示原始值（不写死清单）。</summary>
    private static IReadOnlyList<CompressQualityOption> BuildQualityOptions(IReadOnlyList<string>? choices)
    {
        if (choices is not { Count: > 0 })
        {
            choices = ["lossless", "balanced", "small"];
        }

        return [.. choices.Select(value => value switch
        {
            "lossless" => new CompressQualityOption(value, "无损", "保留全部内容，仅重组文件结构。适合归档或二次处理。"),
            "balanced" => new CompressQualityOption(value, "均衡", "适当降低图片分辨率，兼顾体积与清晰度，保留文字。"),
            "small" => new CompressQualityOption(value, "小文件", "大幅降低图片分辨率，优先减小体积，文字保持可读。"),
            _ => new CompressQualityOption(value, value, "按引擎默认参数处理。"),
        })];
    }
}

/// <summary>修复 PDF：引擎打开时自动重建可恢复的结构（MuPDF 恢复 + 完整保存），无额外参数。</summary>
public sealed partial class RepairViewModel : OptimizerViewModelBase
{
    public RepairViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    protected override IReadOnlyDictionary<string, object?> BuildOptions() => new Dictionary<string, object?>();
}

/// <summary>OCR 语言下拉选项，与 WindowsOcrHelper --list-languages 的 {tag,name} 一一对应。</summary>
public sealed record OcrLanguageOption(string Tag, string DisplayName);

/// <summary>
/// OCR 文字识别：页面范围 + 是否强制识别 + 识别语言下拉。
/// 语言数据来自 WindowsOcrHelper --list-languages（T8 契约）；helper 未构建时下拉禁用并提示，不崩溃。
/// </summary>
public sealed partial class OcrViewModel : OptimizerViewModelBase
{
    /// <summary>测试注入点：替换 --list-languages 的真实进程调用；生产路径为 null。</summary>
    private readonly Func<CancellationToken, Task<IReadOnlyList<OcrLanguageOption>>>? _languageLoader;
    private bool _languagesRequested;

    public OcrViewModel(EngineBridge? bridge, Tool tool)
        : this(bridge, tool, languageLoader: null)
    {
    }

    public OcrViewModel(EngineBridge? bridge, Tool tool,
        Func<CancellationToken, Task<IReadOnlyList<OcrLanguageOption>>>? languageLoader)
        : base(bridge, tool)
    {
        _languageLoader = languageLoader;
        _pagesText = FieldValue(tool, "pages");

        // catalog toggle 默认值是字符串 "true"/"false"，与引擎 flag() 同源解析。
        var forceField = tool.Fields.FirstOrDefault(f => f.Key == "force");
        _force = string.Equals(forceField?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private string _pagesText = "";

    [ObservableProperty]
    private bool _force;

    public ObservableCollection<OcrLanguageOption> Languages { get; } = [];

    [ObservableProperty]
    private OcrLanguageOption? _selectedLanguage;

    /// <summary>语言下拉是否可用；仅当检测到至少一种语言时为 true。</summary>
    [ObservableProperty]
    private bool _isLanguageListEnabled;

    [ObservableProperty]
    private string _languageHint = "尚未检测本机 OCR 语言。";

    /// <summary>页面加载后调用一次；重复调用被忽略，刷新按钮走 RefreshLanguagesCommand。</summary>
    public async Task LoadLanguagesOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_languagesRequested)
        {
            return;
        }

        _languagesRequested = true;
        await LoadLanguagesCoreAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task RefreshLanguagesAsync() => await LoadLanguagesCoreAsync();

    public Task LoadLanguagesAsync(CancellationToken cancellationToken = default)
        => LoadLanguagesCoreAsync(cancellationToken);

    private async Task LoadLanguagesCoreAsync(CancellationToken cancellationToken = default)
    {
        IsLanguageListEnabled = false;
        SelectedLanguage = null;
        LanguageHint = "正在检测本机可用的 OCR 识别语言…";
        try
        {
            var languages = _languageLoader is not null
                ? await _languageLoader(cancellationToken)
                : await WindowsOcrLanguages.LoadFromHelperAsync(cancellationToken);

            Languages.Clear();
            foreach (var language in languages)
            {
                Languages.Add(language);
            }

            if (Languages.Count == 0)
            {
                LanguageHint = "本机尚未安装任何 OCR 识别语言包。请在系统「设置 → 时间和语言 → 语言」中添加语言后重试。";
                return;
            }

            SelectedLanguage = Languages[0];
            IsLanguageListEnabled = true;
            // 如实说明：helper 按简中 → 繁中 → 英文优先自动选择识别器，列表用于展示本机能力。
            LanguageHint = $"已检测到 {Languages.Count} 种本机可用识别语言（引擎按简体中文 → 繁体中文 → 英文优先自动选择）。";
        }
        catch (OperationCanceledException)
        {
            LanguageHint = "已取消检测本机 OCR 语言。";
        }
        catch (Exception error)
        {
            // helper 未构建 / 运行失败：下拉保持禁用，原因直接展示，不崩溃、不静默。
            LanguageHint = $"OCR 语言不可用：{error.Message}";
        }
    }

    protected override IReadOnlyDictionary<string, object?> BuildOptions()
    {
        var options = new Dictionary<string, object?>
        {
            ["pages"] = PagesText.Trim(),
            ["force"] = Force ? "true" : "false",
        };
        if (SelectedLanguage is { } language && !string.IsNullOrWhiteSpace(language.Tag))
        {
            // 引擎当前自动选择语言（见上方提示），该值作为请求语义随请求传递，向前兼容。
            options["language"] = language.Tag;
        }

        return options;
    }

    protected override string? Validate()
        => base.Validate() ?? (PagesText.Trim().Length == 0 ? null : ValidatePagesSpec(PagesText));
}

/// <summary>WindowsOcrHelper 的 UI 侧轻量访问：--list-languages 输出解析与产物路径解析。</summary>
public static class WindowsOcrLanguages
{
    /// <summary>
    /// 解析 --list-languages 输出：[{"tag":"en-US","name":"English (United States)"}]。
    /// 非 JSON / 非 JSON 数组抛 JsonException（调用方转为禁用 + 提示）。
    /// </summary>
    public static IReadOnlyList<OcrLanguageOption> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("语言列表不是 JSON 数组。");
        }

        var result = new List<OcrLanguageOption>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tag = item.TryGetProperty("tag", out var tagElement) && tagElement.ValueKind == JsonValueKind.String
                ? tagElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            result.Add(new OcrLanguageOption(tag, string.IsNullOrWhiteSpace(name) ? tag : name!));
        }

        return result;
    }

    /// <summary>
    /// 解析 WindowsOcrHelper 构建产物路径，与 engine.vision_path() 的 win32 分支一致
    /// （Release 优先、Debug 兜底、自包含产物在 win-x64 子目录）；未构建返回 null。
    /// </summary>
    public static string? ResolveHelperPath(string? repoRoot = null)
    {
        repoRoot ??= FindRepoRoot();
        if (repoRoot is null)
        {
            return null;
        }

        var helperDir = Path.Combine(repoRoot, "Sources", "WindowsOcrHelper", "bin");
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var exe = Path.Combine(helperDir, configuration, "net8.0-windows10.0.19041.0", "win-x64", "WindowsOcrHelper.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>调用 helper --list-languages 并解析；helper 未构建或失败时抛异常（由调用方转为禁用提示）。</summary>
    public static async Task<IReadOnlyList<OcrLanguageOption>> LoadFromHelperAsync(CancellationToken cancellationToken = default)
    {
        var helper = ResolveHelperPath()
            ?? throw new InvalidOperationException("OCR helper 未构建：找不到 WindowsOcrHelper.exe，请先构建 WindowsOcrHelper 项目。");
        return Parse(await RunListLanguagesAsync(helper, cancellationToken));
    }

    private static async Task<string> RunListLanguagesAsync(string helperPath, CancellationToken cancellationToken)
    {
        // helper 是一次性进程：stdout 输出 JSON（UTF-8，T8 契约），stderr 报错。
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--list-languages");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            throw new InvalidOperationException(
                detail.Length > 0 ? $"WindowsOcrHelper 退出码 {process.ExitCode}：{detail}" : $"WindowsOcrHelper 退出码 {process.ExitCode}。");
        }

        return stdout;
    }

    private static string? FindRepoRoot()
    {
        // 与 EngineBridge 相同的锚点：向上查找 backend/engine.py。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "engine.py")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
