// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.Tools;

/// <summary>结果列表条目：引擎输出文件路径（位于新建任务子目录内）。</summary>
public sealed class ToolResultItem
{
    public ToolResultItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 安全工具组（解锁/加密/签名/遮盖/比较）页面视图模型基类。
/// 执行结构与页面整理组一致：文件输入 → 参数表单 → 输出目录 → 执行/取消 → 进度与结果。
/// 引擎调用直接走 T12 的 EngineBridge（一次请求 stdin 单行 JSON → stdout 单行结果）；
/// 引擎业务错误（Ok=false，如"需要正确的打开密码"）回到页面错误横幅，不抛异常。
/// </summary>
public abstract partial class ToolViewModelBase : ObservableObject
{
    private readonly EngineBridge? _bridge;
    private CancellationTokenSource? _cancellation;

    protected ToolViewModelBase(EngineBridge? bridge, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _bridge = bridge;
        Tool = tool;
        Files.CollectionChanged += OnFilesCollectionChanged;
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsRunning);
        ClearFilesCommand = new RelayCommand(ClearFiles, () => !IsRunning);
        RemoveFileCommand = new RelayCommand<string>(path =>
        {
            if (path is not null && !IsRunning)
            {
                Files.Remove(path);
            }
        }, _ => !IsRunning);
    }

    public Tool Tool { get; }

    /// <summary>输入文件（多文件工具的列表；单文件与双槽工具改写 AddDroppedFiles/EffectiveFiles）。</summary>
    public ObservableCollection<string> Files { get; } = [];

    public virtual bool HasFiles => Files.Count > 0;

    /// <summary>实际传给引擎的文件序列；双槽比较工具按旧版、新版顺序改写。</summary>
    public virtual IReadOnlyList<string> EffectiveFiles => [.. Files];

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

    /// <summary>页面错误横幅：参数校验失败或引擎业务错误（如密码错误）的消息。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    public ObservableCollection<ToolResultItem> Results { get; } = [];

    /// <summary>最近一次引擎结果（测试断言用；业务状态以 Results/StatusMessage 为准）。</summary>
    public EngineResult? LastResult { get; private set; }

    public IAsyncRelayCommand ExecuteCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ClearFilesCommand { get; }

    public IRelayCommand<string> RemoveFileCommand { get; }

    public virtual void ClearFiles() => Files.Clear();

    /// <summary>拖入 / 粘贴的统一入口：按工具扩展名过滤、去重；首个文件所在目录作为默认输出目录。</summary>
    public virtual void AddDroppedFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsRunning)
        {
            return;
        }

        var added = false;
        foreach (var path in FilterExisting(paths))
        {
            if (Files.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Files.Add(path);
            added = true;
        }

        if (added && OutputDir.Trim().Length == 0)
        {
            // 默认输出目录 = 源文件旁（引擎在其下新建 AIPDF-日期-编号 任务子目录，不覆盖源文件）
            OutputDir = System.IO.Path.GetDirectoryName(Files[^1]) ?? "";
        }
    }

    /// <summary>请求选项拼装；密码类字段在此处经 Func 委托按需取值，绝不提前保存。</summary>
    protected abstract Dictionary<string, object?> BuildOptions();

    /// <summary>执行前校验；返回错误文案（不发请求），null 放行。错误信息与引擎语义对齐。</summary>
    protected virtual string? Validate()
    {
        if (EffectiveFiles.Count == 0)
        {
            return "请先选择文件。";
        }

        if (!Tool.Multiple && EffectiveFiles.Count > 1)
        {
            return "此工具一次处理一份文件。";
        }

        var output = OutputDir.Trim();
        if (output.Length == 0 || !System.IO.Path.IsPathRooted(output) || !Directory.Exists(output))
        {
            return "请选择有效的输出文件夹。";
        }

        return null;
    }

    /// <summary>过滤出真实存在且扩展名匹配目录声明的文件；目录条目忽略。</summary>
    protected IReadOnlyList<string> FilterExisting(IEnumerable<string> paths)
        => [.. paths.Where(path => File.Exists(path)
            && Tool.Extensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.').Trim(), StringComparer.OrdinalIgnoreCase))];

    private async Task ExecuteAsync()
    {
        if (IsRunning)
        {
            return; // 防重入：一次只跑一个引擎任务
        }

        ErrorText = "";
        if (Validate() is { } validationError)
        {
            ErrorText = validationError;
            StatusMessage = validationError;
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
            Files = [.. EffectiveFiles],
            Options = BuildOptions(),
            OutputDir = OutputDir.Trim(),
        };

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        ProgressFraction = 0;
        ProgressMessage = "";
        StatusMessage = $"正在执行 {Tool.Name}…";
        Results.Clear();
        LastResult = null;
        _bridge.Progress += OnBridgeProgress;
        try
        {
            var result = await _bridge.ExecuteAsync(request, _cancellation.Token);
            LastResult = result;
            if (result.Ok)
            {
                foreach (var output in result.Outputs)
                {
                    Results.Add(new ToolResultItem(output));
                }

                StatusMessage = $"完成 {Tool.Name}：{result.Outputs.Count} 个文件 → {result.OutputDir}";
            }
            else
            {
                // 引擎业务错误（如错误密码）按协议内响应处理：进页面错误横幅，不崩溃
                ErrorText = result.Error ?? "处理失败。";
                StatusMessage = $"失败：{ErrorText}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消。";
        }
        catch (EngineBridgeException error)
        {
            ErrorText = error.Message;
            StatusMessage = $"引擎异常：{error.Message}";
        }
        finally
        {
            _bridge.Progress -= OnBridgeProgress;
            IsRunning = false;
            ProgressMessage = "";
            ProgressFraction = 0;
        }
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

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasFiles));

    partial void OnIsRunningChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearFilesCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>单文件工具基类：拖入即替换当前文件。</summary>
public abstract class SingleFileToolViewModelBase : ToolViewModelBase
{
    protected SingleFileToolViewModelBase(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    public override void AddDroppedFiles(IEnumerable<string> paths)
    {
        var first = FilterExisting(paths).FirstOrDefault();
        if (first is not null)
        {
            Files.Clear();
            Files.Add(first);
            if (OutputDir.Trim().Length == 0)
            {
                OutputDir = System.IO.Path.GetDirectoryName(first) ?? "";
            }
        }
    }
}

/// <summary>
/// 解锁：密码经 Func 委托按需取值（页面 PasswordBox 接线），仅在构建请求时读取一次，
/// 随请求 JSON 经 stdin 管道进入引擎；VM 不持有密码字段，也不写任何日志。
/// </summary>
public sealed class UnlockViewModel : ToolViewModelBase
{
    public UnlockViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    /// <summary>页面 Loaded 时接线为 <c>() =&gt; passwordBox.Password</c>；VM 侧只保存委托本身。</summary>
    public Func<string?>? PasswordProvider { get; set; }

    protected override Dictionary<string, object?> BuildOptions() => new()
    {
        ["password"] = PasswordProvider?.Invoke() ?? "",
    };
}

/// <summary>加密：新密码同样只经委托按需取值；打印/复制权限映射为引擎 flag() 可解析的字符串。</summary>
public sealed partial class ProtectViewModel : SingleFileToolViewModelBase
{
    public ProtectViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    /// <summary>页面 Loaded 时接线为 <c>() =&gt; passwordBox.Password</c>。</summary>
    public Func<string?>? NewPasswordProvider { get; set; }

    [ObservableProperty]
    private bool _allowPrint = true;

    [ObservableProperty]
    private bool _allowCopy;

    protected override string? Validate()
        => base.Validate() ?? (string.IsNullOrEmpty(NewPasswordProvider?.Invoke())
            ? "请设置非空的打开密码。"
            : null);

    protected override Dictionary<string, object?> BuildOptions() => new()
    {
        ["newPassword"] = NewPasswordProvider?.Invoke() ?? "",
        ["allowPrint"] = AllowPrint ? "true" : "false",
        ["allowCopy"] = AllowCopy ? "true" : "false",
    };
}

/// <summary>签名：可视签名（文字或图片）+ 框选位置；不是证书数字签名。</summary>
public sealed partial class SignViewModel : SingleFileToolViewModelBase
{
    public SignViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _pages = ToolFieldValue(tool, "pages");
        _signatureText = ToolFieldValue(tool, "text");
        _x = ToolFieldValue(tool, "x");
        _y = ToolFieldValue(tool, "y");
        _width = ToolFieldValue(tool, "width");
        _height = ToolFieldValue(tool, "height");
    }

    /// <summary>取目录字段默认值（catalog 权威）。</summary>
    private static string ToolFieldValue(Tool tool, string key)
        => tool.Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";

    [ObservableProperty]
    private string _pages;

    [ObservableProperty]
    private string _signatureText;

    [ObservableProperty]
    private string _signatureImage = "";

    /// <summary>框选位置（PDF 点坐标，左上角原点），预填目录 RECT 默认值。</summary>
    [ObservableProperty]
    private string _x;

    [ObservableProperty]
    private string _y;

    [ObservableProperty]
    private string _width;

    [ObservableProperty]
    private string _height;

    protected override string? Validate()
        => base.Validate() ?? (SignatureText.Length == 0 && SignatureImage.Length == 0
            ? "请输入签名姓名或选择签名图片。"
            : null);

    protected override Dictionary<string, object?> BuildOptions() => new()
    {
        ["pages"] = Pages,
        ["text"] = SignatureText,
        ["image"] = SignatureImage,
        ["x"] = X,
        ["y"] = Y,
        ["width"] = Width,
        ["height"] = Height,
    };
}

/// <summary>永久遮盖：按关键词或区域真正移除内容（引擎 redaction + scrub），不是视觉覆盖。</summary>
public sealed partial class RedactViewModel : ToolViewModelBase
{
    public RedactViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
        _pages = ToolFieldValue(tool, "pages");
        _keywords = ToolFieldValue(tool, "text");
        _x = ToolFieldValue(tool, "x");
        _y = ToolFieldValue(tool, "y");
        _width = ToolFieldValue(tool, "width");
        _height = ToolFieldValue(tool, "height");
    }

    /// <summary>取目录字段默认值（catalog 权威）。</summary>
    private static string ToolFieldValue(Tool tool, string key)
        => tool.Fields.FirstOrDefault(f => f.Key == key)?.Value ?? "";

    [ObservableProperty]
    private string _pages;

    /// <summary>匹配文字，每行一项；留空时引擎按区域遮盖。</summary>
    [ObservableProperty]
    private string _keywords;

    /// <summary>区域遮盖的框（PDF 点坐标，左上角原点），预填目录 RECT 默认值。</summary>
    [ObservableProperty]
    private string _x;

    [ObservableProperty]
    private string _y;

    [ObservableProperty]
    private string _width;

    [ObservableProperty]
    private string _height;

    protected override Dictionary<string, object?> BuildOptions() => new()
    {
        ["pages"] = Pages,
        ["text"] = Keywords,
        ["x"] = X,
        ["y"] = Y,
        ["width"] = Width,
        ["height"] = Height,
    };
}

/// <summary>比较：恰好两份 PDF，按旧版、新版顺序（引擎逐页配对比较，不自动对齐插入页）。</summary>
public sealed partial class CompareViewModel : ToolViewModelBase
{
    public CompareViewModel(EngineBridge? bridge, Tool tool) : base(bridge, tool)
    {
    }

    [ObservableProperty]
    private string _firstFile = "";

    [ObservableProperty]
    private string _secondFile = "";

    public override bool HasFiles => FirstFile.Length > 0 && SecondFile.Length > 0;

    public override IReadOnlyList<string> EffectiveFiles
        => [.. new[] { FirstFile, SecondFile }.Where(path => path.Length > 0)];

    public override void ClearFiles()
    {
        FirstFile = "";
        SecondFile = "";
    }

    public void SetFirstFile(string path) => FirstFile = FilterExisting([path]).FirstOrDefault() ?? FirstFile;

    public void SetSecondFile(string path) => SecondFile = FilterExisting([path]).FirstOrDefault() ?? SecondFile;

    /// <summary>拖入的文件按顺序填充空槽；两槽已满时忽略多余的文件。</summary>
    public override void AddDroppedFiles(IEnumerable<string> paths)
    {
        if (IsRunning)
        {
            return;
        }

        foreach (var path in FilterExisting(paths))
        {
            if (FirstFile.Length == 0)
            {
                FirstFile = path;
            }
            else if (SecondFile.Length == 0)
            {
                SecondFile = path;
            }
        }

        if (OutputDir.Trim().Length == 0 && FirstFile.Length > 0)
        {
            OutputDir = System.IO.Path.GetDirectoryName(FirstFile) ?? "";
        }
    }

    partial void OnFirstFileChanged(string value) => OnPropertyChanged(nameof(HasFiles));

    partial void OnSecondFileChanged(string value) => OnPropertyChanged(nameof(HasFiles));

    protected override string? Validate()
    {
        if (FirstFile.Length == 0 || SecondFile.Length == 0)
        {
            return "比较需要正好两份 PDF，请按旧版、新版的顺序添加。";
        }

        var output = OutputDir.Trim();
        if (output.Length == 0 || !System.IO.Path.IsPathRooted(output) || !Directory.Exists(output))
        {
            return "请选择有效的输出文件夹。";
        }

        return null;
    }

    protected override Dictionary<string, object?> BuildOptions() => [];
}
