// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIPDF.Windows.Bridge;

/// <summary>发给引擎的一次请求，形状与 backend/engine.py 的 dispatch() 契约一致。</summary>
public sealed class EngineRequest
{
    public string Operation { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public Dictionary<string, object?> Options { get; set; } = [];
    public string OutputDir { get; set; } = "";
}

/// <summary>引擎返回的一次结果。引擎是权威，桥只搬运不加工；未知字段保留在 Extra。</summary>
public sealed class EngineResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Outputs { get; init; } = [];
    public string? OutputDir { get; init; }
    public long? InputBytes { get; init; }
    public long? OutputBytes { get; init; }
    public double? Elapsed { get; init; }
    public int ExitCode { get; init; }

    /// <summary>结果 JSON 中桥未建模的其余顶层字段（如 health 的 engine/office/vision）。</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } =
        new Dictionary<string, JsonElement>();
}

/// <summary>引擎 stderr 进度行（{"message":…,"progress":…}）解析后的事件参数。</summary>
public sealed class EngineProgressEventArgs : EventArgs
{
    public EngineProgressEventArgs(string message, double fraction)
    {
        Message = message;
        Fraction = fraction;
    }

    public string Message { get; }
    public double Fraction { get; }
}

/// <summary>桥层传输失败（引擎崩溃、无有效 JSON 返回等）。引擎业务错误走 EngineResult.Ok=false。</summary>
public class EngineBridgeException : Exception
{
    public EngineBridgeException(string message, int exitCode = -1, IReadOnlyList<string>? diagnostics = null)
        : base(message)
    {
        ExitCode = exitCode;
        Diagnostics = diagnostics ?? [];
    }

    public int ExitCode { get; }
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// 引擎子进程客户端。上游契约（backend/engine.py）：一次进程一份 JSON 请求（stdin），
/// 一份 JSON 结果（stdout 单行），进度走 stderr 行式 JSON；全程 UTF-8（T4）。
/// 桥是薄消费者：不缓存、不重试、不发明协议。
/// </summary>
public class EngineBridge
{
    private const int DiagnosticCapacity = 200;

    /// <summary>脱敏占位符：诊断文本中出现的敏感值一律以此替换。</summary>
    private const string RedactionPlaceholder = "[REDACTED]";

    /// <summary>请求 options 中需要从诊断里脱敏的键（apiKey / password）。</summary>
    private static readonly string[] SensitiveOptionKeys = ["apiKey", "password"];

    private readonly string _pythonPath;
    private readonly string _enginePath;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    private readonly Queue<string> _diagnostics = new();
    private readonly object _diagnosticsLock = new();

    public EngineBridge() : this(ResolveDefaultPythonPath(), ResolveDefaultEnginePath())
    {
    }

    /// <summary>测试与发布布局用：显式指定解释器与引擎脚本；environment 追加到继承环境之上。</summary>
    public EngineBridge(string pythonPath, string enginePath, IReadOnlyDictionary<string, string>? environment = null)
    {
        _pythonPath = pythonPath;
        _enginePath = enginePath;
        _environment = environment;
    }

    /// <summary>引擎 stderr 进度行（已解析为消息与 0..1 进度）。</summary>
    public event EventHandler<EngineProgressEventArgs>? Progress;

    /// <summary>最近一次启动的引擎进程 PID（测试与诊断用；未启动时为 null）。</summary>
    public int? LastEngineProcessId { get; private set; }

    /// <summary>最近一次运行的 stderr 诊断环形缓冲快照（最多 200 行）。</summary>
    public IReadOnlyList<string> RecentDiagnostics
    {
        get
        {
            lock (_diagnosticsLock)
            {
                return [.. _diagnostics];
            }
        }
    }

    /// <summary>执行一次引擎请求。取消时抛 OperationCanceledException；传输失败抛 EngineBridgeException。</summary>
    public virtual async Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 本请求的敏感值（apiKey / password）先解析一次：既用于 stderr 入缓冲前脱敏，
        // 也用于异常消息脱敏，保证任何诊断缓冲都不驻留原文（R5-5）。
        var secrets = SensitiveOptionValues(request);

        // 与引擎端 T4 的 UTF-8 stdio 契约配对，中文路径双向无损。
        // 必须用无 BOM 的 UTF8：Encoding.UTF8 静态实例带 BOM 前导，StreamWriter
        // 会把 EF BB BF 写进 stdin，引擎的 json.load 读到 U+FEFF 即报"请求格式不正确"。
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_enginePath) is { } engineDir
                ? Path.GetDirectoryName(engineDir) ?? engineDir
                : AppContext.BaseDirectory,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(_enginePath);
        if (_environment is not null)
        {
            foreach (var (key, value) in _environment)
            {
                psi.Environment[key] = value;
            }
        }

        if (!File.Exists(_pythonPath))
        {
            throw new EngineBridgeException($"未找到引擎解释器：{_pythonPath}。请先创建项目 .venv。");
        }

        if (!File.Exists(_enginePath))
        {
            throw new EngineBridgeException($"未找到引擎脚本：{_enginePath}。请确认源码树完整。");
        }

        var stdoutLines = new List<string>();
        using var process = new Process { StartInfo = psi };
        process.Start();
        LastEngineProcessId = process.Id;

        // 先启动两个异步读循环再写 stdin：引擎的任何输出都不会因管道缓冲填满而阻塞。
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines.Add);
        var stderrTask = ReadLinesAsync(process.StandardError, line => OnStderrLine(line, secrets));

        try
        {
            await process.StandardInput.WriteLineAsync(SerializeRequest(request));
            await process.StandardInput.FlushAsync();
        }
        catch (IOException)
        {
            // 引擎可能已提前退出（如请求非法）；结果由退出后的 stdout 解析给出。
        }
        finally
        {
            // 引擎用 json.load(sys.stdin) 读到 EOF 才解析，必须关闭写端。
            process.StandardInput.Close();
        }

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 与 T5 语义一致：taskkill /T /F /PID 终止整棵进程树（参数数组，不经 shell）。
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            cancelled = true;
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (cancelled)
        {
            throw new OperationCanceledException("引擎处理已取消。", cancellationToken);
        }

        return ParseResult(stdoutLines, process.ExitCode, RecentDiagnostics, secrets);
    }

    /// <summary>stdout 行式异步读循环；管道断裂（进程被杀）视为流结束。</summary>
    private static async Task ReadLinesAsync(TextReader reader, Action<string> sink)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            sink(line);
        }
    }

    /// <summary>
    /// stderr 行落环形缓冲：入缓冲【前】用本请求的敏感值脱敏，保证公开的
    /// <see cref="RecentDiagnostics"/> 绝不返回原文密钥（R5-5）。进度事件同样基于脱敏后的行。
    /// </summary>
    internal void OnStderrLine(string line, IReadOnlyList<string>? secrets)
    {
        var redactedLine = RedactSecrets(line, secrets);

        lock (_diagnosticsLock)
        {
            if (_diagnostics.Count == DiagnosticCapacity)
            {
                _diagnostics.Dequeue();
            }

            _diagnostics.Enqueue(redactedLine);
        }

        var progress = TryParseProgress(redactedLine);
        if (progress is not null)
        {
            Progress?.Invoke(this, progress);
        }
    }

    /// <summary>解析引擎 stderr 行；合法进度行返回事件参数，其余（诊断文本）返回 null。</summary>
    internal static EngineProgressEventArgs? TryParseProgress(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var fraction = 0d;
            if (document.RootElement.TryGetProperty("progress", out var progress)
                && progress.ValueKind == JsonValueKind.Number
                && progress.TryGetDouble(out var value))
            {
                fraction = value;
            }

            return new EngineProgressEventArgs(message.GetString() ?? "", fraction);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 stdout 行集合解析结果：从后向前找最后一条能解析为含 "ok" 属性的 JSON 对象的行，
    /// 保证第三方库混入的通知行、损坏行、超长行都不会破坏协议。
    /// </summary>
    internal static EngineResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode, IReadOnlyList<string> diagnostics, IReadOnlyList<string>? secrets = null)
    {
        for (var i = stdoutLines.Count - 1; i >= 0; i--)
        {
            var line = stdoutLines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("ok", out _))
                {
                    return FromJson(document.RootElement, exitCode);
                }
            }
            catch (JsonException)
            {
                // 损坏行/非 JSON 行：跳过，继续向前找。
            }
        }

        // 引擎崩溃时 Python traceback 可能把请求 options（含 apiKey / password）打到 stderr；
        // 先脱敏再拼装异常消息与 Diagnostics，密钥绝不进入 UI / 日志。
        var redacted = RedactDiagnostics(diagnostics, secrets);
        var detail = redacted.Count > 0
            ? "最近诊断：" + string.Join(" | ", redacted.TakeLast(5))
            : "无 stderr 诊断。";
        throw new EngineBridgeException($"引擎没有返回有效的 JSON 结果（退出码 {exitCode}）。{detail}", exitCode, redacted);
    }

    /// <summary>
    /// 请求 options 中需要脱敏的敏感值（apiKey / password）。引擎异常时 Python 可能把
    /// 整个请求 options 打进 traceback，宿主据这些值在诊断进入异常前做替换。
    /// </summary>
    internal static IReadOnlyList<string> SensitiveOptionValues(EngineRequest request)
    {
        var secrets = new List<string>();
        foreach (var name in SensitiveOptionKeys)
        {
            if (request.Options.TryGetValue(name, out var value)
                && value is string text && text.Length > 0)
            {
                secrets.Add(text);
            }
        }

        return secrets;
    }

    /// <summary>
    /// 把文本中出现的敏感值替换为固定占位符。原样命中即覆盖
    /// <c>Bearer &lt;key&gt;</c>、<c>apiKey: "&lt;key&gt;"</c> 等常见形态；另替换 URL 编码形态
    /// （<see cref="Uri.EscapeDataString(string)"/>），防止被转义后绕过替换。
    /// </summary>
    internal static string RedactSecrets(string text, IReadOnlyList<string>? secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets is not { Count: > 0 })
        {
            return text;
        }

        var redacted = text;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret))
            {
                continue;
            }

            redacted = redacted.Replace(secret, RedactionPlaceholder, StringComparison.Ordinal);
            var encoded = Uri.EscapeDataString(secret);
            if (!string.Equals(encoded, secret, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(encoded, RedactionPlaceholder, StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    /// <summary>对整批诊断行做脱敏；无可脱敏值时原样返回。</summary>
    private static IReadOnlyList<string> RedactDiagnostics(IReadOnlyList<string> diagnostics, IReadOnlyList<string>? secrets)
        => secrets is { Count: > 0 }
            ? [.. diagnostics.Select(line => RedactSecrets(line, secrets))]
            : diagnostics;

    private static EngineResult FromJson(JsonElement root, int exitCode)
    {
        var extra = new Dictionary<string, JsonElement>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("ok" or "error" or "outputs" or "outputDir" or "inputBytes" or "outputBytes" or "elapsed"))
            {
                extra[property.Name] = property.Value.Clone();
            }
        }

        return new EngineResult
        {
            Ok = root.GetProperty("ok").GetBoolean(),
            Error = TryGetString(root, "error"),
            Outputs = TryGetArray(root, "outputs"),
            OutputDir = TryGetString(root, "outputDir"),
            InputBytes = TryGetNumber(root, "inputBytes", out var input) ? input : null,
            OutputBytes = TryGetNumber(root, "outputBytes", out var output) ? output : null,
            Elapsed = TryGetDouble(root, "elapsed", out var elapsed) ? elapsed : null,
            ExitCode = exitCode,
            Extra = extra,
        };
    }

    private static string? TryGetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> TryGetArray(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "")]
            : [];

    private static bool TryGetNumber(JsonElement root, string name, out long number)
    {
        number = 0;
        return root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number);
    }

    private static bool TryGetDouble(JsonElement root, string name, out double number)
    {
        number = 0;
        return root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out number);
    }

    private static string SerializeRequest(EngineRequest request)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["operation"] = request.Operation,
            ["files"] = request.Files,
            ["options"] = request.Options,
            ["outputDir"] = request.OutputDir,
        });

    /// <summary>taskkill /T /F /PID（参数数组，无 shell）；taskkill 不可用时退回内置整树终止。</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            using var killer = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            killer.StartInfo.ArgumentList.Add("/T");
            killer.StartInfo.ArgumentList.Add("/F");
            killer.StartInfo.ArgumentList.Add("/PID");
            killer.StartInfo.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            killer.Start();
            killer.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已退出：目标达成。
            }
        }
    }

    /// <summary>从输出目录向上查找源码树根（以 backend/engine.py 或 engine/backend/engine.py 为锚）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // 开发布局：backend/engine.py
            if (File.Exists(Path.Combine(dir.FullName, "backend", "engine.py")))
            {
                return dir.FullName;
            }

            // 安装布局：engine/backend/engine.py
            if (File.Exists(Path.Combine(dir.FullName, "engine", "backend", "engine.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static string ResolveDefaultEnginePath()
    {
        var root = FindRepoRoot();
        if (root is null) return "backend\\engine.py";

        // 优先开发布局；回退安装布局
        var devPath = Path.Combine(root, "backend", "engine.py");
        if (File.Exists(devPath)) return devPath;

        var installPath = Path.Combine(root, "engine", "backend", "engine.py");
        if (File.Exists(installPath)) return installPath;

        return devPath;
    }

    public static string ResolveDefaultPythonPath()
    {
        var root = FindRepoRoot();
        if (root is null) return ".venv\\Scripts\\python.exe";

        // 开发布局
        var devPath = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (File.Exists(devPath)) return devPath;

        // 自包含安装布局：随包内置的嵌入式解释器（build.ps1 阶段 7 产物 engine\python\python.exe）
        var bundledPath = Path.Combine(root, "engine", "python", "python.exe");
        if (File.Exists(bundledPath)) return bundledPath;

        // 安装布局：引擎目录下的 .venv
        var installPath = Path.Combine(root, "engine", ".venv", "Scripts", "python.exe");
        if (File.Exists(installPath)) return installPath;

        return devPath;
    }
}
