// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 引擎桥测试：① 协议解析健壮性（假 stdout 流）② 真引擎集成 ③ 取消进程树 ≤5s ④ 中文往返。
/// 真引擎用例依赖仓库 .venv（T3 已建）与 backend/engine.py（T4 UTF-8 / T5 taskkill 语义）。
/// </summary>
public sealed class EngineBridgeTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // ---------- ① 协议解析（纯单元，无进程） ----------

    [Fact]
    public void ParseResult_ValidJsonLine_ParsesAllFields()
    {
        var lines = new[]
        {
            """{"ok":true,"outputs":["D:\\out\\01-文件-rotate.pdf"],"outputDir":"D:\\out","inputBytes":123,"outputBytes":456,"elapsed":1.5}""",
        };

        var result = EngineBridge.ParseResult(lines, exitCode: 0, diagnostics: []);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(["D:\\out\\01-文件-rotate.pdf"], result.Outputs);
        Assert.Equal("D:\\out", result.OutputDir);
        Assert.Equal(123, result.InputBytes);
        Assert.Equal(456, result.OutputBytes);
        Assert.Equal(1.5, result.Elapsed);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ParseResult_SkipsGarbageCorruptAndOverlongLines()
    {
        var garbage = new string('x', 1024 * 1024); // 1MB 非 JSON 超长行
        var corrupt = """{"ok":true,"outputs":["截断"""; // 损坏 JSON
        var valid = """{"ok":false,"error":"处理失败，请检查文件格式和参数。"}""";
        var lines = new[] { "第三方库通知：已加载插件", garbage, corrupt, valid, "   " };

        var result = EngineBridge.ParseResult(lines, exitCode: 1, diagnostics: ["诊断行"]);

        Assert.False(result.Ok);
        Assert.Contains("处理失败", result.Error);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void ParseResult_LastValidJsonWins()
    {
        var first = """{"ok":true,"outputs":["旧结果.pdf"]}""";
        var second = """{"ok":true,"outputs":["新结果.pdf"]}""";
        var lines = new[] { first, "噪声行", second };

        var result = EngineBridge.ParseResult(lines, exitCode: 0, diagnostics: []);

        Assert.Equal(["新结果.pdf"], result.Outputs);
    }

    [Fact]
    public void ParseResult_KeepsUnknownTopLevelFieldsInExtra()
    {
        var lines = new[] { """{"ok":true,"pages":3,"fields":[],"engine":"8.0"}""" };

        var result = EngineBridge.ParseResult(lines, exitCode: 0, diagnostics: []);

        Assert.Equal(3, result.Extra["pages"].GetInt32());
        Assert.Equal("8.0", result.Extra["engine"].GetString());
        Assert.False(result.Extra.ContainsKey("ok"));
    }

    [Fact]
    public void ParseResult_NoValidJson_ThrowsWithExitCodeAndDiagnostics()
    {
        var lines = new[] { "引擎崩溃前的最后输出", "Segmentation fault" };

        var error = Assert.Throws<EngineBridgeException>(
            () => EngineBridge.ParseResult(lines, exitCode: -1073741819, diagnostics: ["Traceback (most recent call last):", "MemoryError"]));

        Assert.Equal(-1073741819, error.ExitCode);
        Assert.Contains("没有返回有效的 JSON 结果", error.Message);
        Assert.Contains("MemoryError", error.Message);
    }

    // ---------- ①b 诊断脱敏（F27：引擎 traceback 不得泄漏 apiKey / password） ----------

    [Fact]
    public void ParseResult_RedactsSecretsFromDiagnostics()
    {
        const string key = "sk-live-murphy-secret-123";
        var lines = new[] { "引擎崩溃前的最后输出" };

        var error = Assert.Throws<EngineBridgeException>(() => EngineBridge.ParseResult(
            lines,
            exitCode: 1,
            diagnostics:
            [
                $"Traceback (most recent call last): options={{'apiKey': '{key}', 'targetLang': 'zh'}}",
                $"Authorization: Bearer {key}",
            ],
            secrets: [key]));

        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error.Message, StringComparison.Ordinal);
        // Diagnostics 属性同样不得残留密钥。
        Assert.DoesNotContain(key, string.Join("|", error.Diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_RedactsUrlEncodedSecrets()
    {
        const string key = "sk/with+special=chars";
        var encoded = Uri.EscapeDataString(key);

        var error = Assert.Throws<EngineBridgeException>(() => EngineBridge.ParseResult(
            ["x"],
            exitCode: 1,
            diagnostics: [$"request body: apiKey={encoded}"],
            secrets: [key]));

        Assert.DoesNotContain(encoded, error.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveOptionValues_ExtractsApiKeyAndPasswordOnly()
    {
        var request = new EngineRequest
        {
            Options = new Dictionary<string, object?>
            {
                ["apiKey"] = "k1",
                ["password"] = "p1",
                ["endpoint"] = "https://api.example.test/v1",
            },
        };

        var secrets = EngineBridge.SensitiveOptionValues(request);

        Assert.Contains("k1", secrets);
        Assert.Contains("p1", secrets);
        Assert.DoesNotContain("https://api.example.test/v1", secrets);
    }

    [Fact]
    public void TryParseProgress_ValidLine_ReturnsMessageAndFraction()
    {
        var progress = EngineBridge.TryParseProgress("""{"message":"正在合并 2/3","progress":0.5}""");

        Assert.NotNull(progress);
        Assert.Equal("正在合并 2/3", progress.Message);
        Assert.Equal(0.5, progress.Fraction);
    }

    [Fact]
    public void TryParseProgress_DiagnosticOrCorruptLine_ReturnsNull()
    {
        Assert.Null(EngineBridge.TryParseProgress("Traceback (most recent call last):"));
        Assert.Null(EngineBridge.TryParseProgress("""{"progress":0.5}"""));
        // progress 字段缺失或非数字时消息仍有效（引擎契约里 message 是进度行的标志），进度默认 0
        var noFraction = EngineBridge.TryParseProgress("""{"message":"识别完成"}""");
        Assert.NotNull(noFraction);
        Assert.Equal(0, noFraction.Fraction);
        var badFraction = EngineBridge.TryParseProgress("""{"message":"异常行","progress":"不是数字"}""");
        Assert.NotNull(badFraction);
        Assert.Equal(0, badFraction.Fraction);
    }

    [Fact]
    public void BridgeSource_ContainsNoReadToEnd()
    {
        var bridgeDir = Path.Combine(RepoRoot, "Sources", "AIPDF.Windows", "Bridge");
        var sources = Directory.GetFiles(bridgeDir, "*.cs", SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            Assert.DoesNotContain("ReadToEnd", File.ReadAllText(source), StringComparison.Ordinal);
        }
    }

    // ---------- ② 真引擎集成 ----------

    [Fact]
    public async Task Execute_HealthOperation_ReturnsOkWithEngineVersion()
    {
        var bridge = CreateRealBridge();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await bridge.ExecuteAsync(new EngineRequest { Operation = "health" }, timeout.Token);

        Assert.True(result.Ok);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Extra["engine"].GetString()?.Length > 0, "health 应返回引擎版本");
        Assert.True(result.Extra["office"].ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    [Fact]
    public async Task Execute_MissingFile_ReturnsEngineErrorResult()
    {
        var bridge = CreateRealBridge();
        var request = new EngineRequest
        {
            Operation = "rotate",
            Files = [Path.Combine(RepoRoot, "不存在的文件.pdf")],
            Options = new Dictionary<string, object?> { ["angle"] = "90" },
            OutputDir = Path.GetTempPath(),
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await bridge.ExecuteAsync(request, timeout.Token);

        Assert.False(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("文件不存在", result.Error);
    }

    // ---------- ④ 中文往返 ----------

    [Fact]
    public async Task Execute_ChineseFilenameRoundTrip_PreservesCjk()
    {
        var bridge = CreateRealBridge();
        using var workspace = new TempDirectory();
        var source = Path.Combine(workspace.Path, "中文文件.pdf");
        await File.WriteAllBytesAsync(source, BuildMinimalPdf());

        var request = new EngineRequest
        {
            Operation = "rotate",
            Files = [source],
            Options = new Dictionary<string, object?> { ["angle"] = "90" },
            OutputDir = workspace.Path,
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await bridge.ExecuteAsync(request, timeout.Token);

        Assert.True(result.Ok, result.Error ?? "旋转失败");
        var output = Assert.Single(result.Outputs);
        // 请求方向（中文路径经 stdin UTF-8 进入引擎）与响应方向（中文输出名经 stdout 返回）都无损
        Assert.Contains("中文文件", output);
        Assert.DoesNotContain("\uFFFD", output);
        Assert.True(File.Exists(output), $"输出文件应存在：{output}");
        Assert.True(new FileInfo(output).Length > 0);
        // 引擎的 outputDir 是新建的 AIPDF-日期-编号 任务子目录，位于请求的输出文件夹之下
        Assert.NotNull(result.OutputDir);
        Assert.StartsWith(workspace.Path, result.OutputDir);
        Assert.Contains("AIPDF-", result.OutputDir);
    }

    // ---------- ③ 取消：进程树 ≤5s 消失 ----------

    [Fact]
    public async Task Cancel_LongRunningOfficeHelper_KillsTreeWithin5Seconds()
    {
        using var workspace = new TempDirectory();

        // 慢速假 Office helper：find_office() 在 win32 第一优先级读 AIPDF_OFFICE（T6 语义），
        // 注入一个睡 120s 并把自己的 PID 写到 helper.pid 的假 soffice（镜像上游 test_cancel_stops_children 的 sleep helper）。
        // 注意不能用 AIPDF_VISION：T8 后 vision_path() 的 win32 分支只探测 WindowsOcrHelper 构建产物，不读环境变量。
        var helper = Path.Combine(workspace.Path, "slow-office.cmd");
        await File.WriteAllTextAsync(helper, """
            @echo off
            powershell -NoProfile -Command "$PID | Set-Content -LiteralPath '%~dp0helper.pid'; Start-Sleep -Seconds 120"
            """);
        var document = Path.Combine(workspace.Path, "文档.docx");
        await File.WriteAllBytesAsync(document, [0x50, 0x4B]); // 假 docx：假 helper 不读内容，引擎只做扩展名校验

        var bridgeWithSlowHelper = new EngineBridge(
            EngineBridge.ResolveDefaultPythonPath(),
            EngineBridge.ResolveDefaultEnginePath(),
            new Dictionary<string, string> { ["AIPDF_OFFICE"] = helper });

        using var cancelSource = new CancellationTokenSource();
        var request = new EngineRequest
        {
            Operation = "word_to_pdf",
            Files = [document],
            Options = [],
            OutputDir = workspace.Path,
        };
        var executeTask = bridgeWithSlowHelper.ExecuteAsync(request, cancelSource.Token);

        // 等 helper 真正跑起来（pidfile 由 helper 自己写出），再取消才有整树可杀
        var pidFile = Path.Combine(workspace.Path, "helper.pid");
        var helperPid = await WaitForHelperPid(pidFile, TimeSpan.FromSeconds(60));
        Assert.True(ProcessAlive(helperPid), "helper 进程应在取消前存活");

        var stopwatch = Stopwatch.StartNew();
        cancelSource.Cancel();

        var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(15)));
        if (!ReferenceEquals(completed, executeTask))
        {
            Assert.Fail("取消后桥未在 15 秒内返回");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);

        // 引擎进程与最深层的 helper 都必须消失（taskkill /T 整树语义）
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5) && (ProcessAlive(helperPid) || ProcessAlive(bridgeWithSlowHelper.LastEngineProcessId)))
        {
            await Task.Delay(100);
        }

        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(5), $"取消耗时 {stopwatch.Elapsed.TotalSeconds:F1}s，超过 5 秒");
        Assert.False(ProcessAlive(helperPid), "helper 进程未随进程树终止");
        Assert.False(ProcessAlive(bridgeWithSlowHelper.LastEngineProcessId), "引擎进程未终止");
    }

    // ---------- VM 接线骨架（FakeBridge，无进程） ----------

    private sealed class FakeBridge : EngineBridge
    {
        public EngineRequest? LastRequest { get; private set; }
        public Func<EngineRequest, CancellationToken, Task<EngineResult>>? Handler { get; set; }

        public override async Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Handler is null)
            {
                await Task.Yield();
                return new EngineResult { Ok = true, Outputs = ["out.pdf"], OutputDir = "D:\\out" };
            }

            return await Handler(request, cancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_WithBridge_CompletesAndResetsIsRunning()
    {
        var bridge = new FakeBridge();
        var viewModel = new MainViewModel(ToolCatalog.Load(), bridge);
        var tool = viewModel.Tools.First(t => t.Id == "rotate");

        await viewModel.ExecuteToolAsync(tool, ["a.pdf"], "D:\\out");

        Assert.False(viewModel.IsRunning);
        Assert.Contains("完成", viewModel.StatusText);
        Assert.Contains("D:\\out", viewModel.StatusText); // 状态栏展示输出目录与文件数，不含输入名
        Assert.NotNull(bridge.LastRequest);
        Assert.Equal("rotate", bridge.LastRequest.Operation);
        Assert.Equal(["a.pdf"], bridge.LastRequest.Files);
    }

    [Fact]
    public async Task ExecuteToolAsync_Cancelled_ShowsCancelledAndResets()
    {
        var bridge = new FakeBridge
        {
            Handler = (_, _) => throw new OperationCanceledException(),
        };
        var viewModel = new MainViewModel(ToolCatalog.Load(), bridge);

        await viewModel.ExecuteToolAsync(viewModel.Tools[0], [], "D:\\out");

        Assert.False(viewModel.IsRunning);
        Assert.Contains("已取消", viewModel.StatusText);
    }

    [Fact]
    public async Task ExecuteToolAsync_EngineError_ShowsEngineMessage()
    {
        var bridge = new FakeBridge
        {
            Handler = (_, _) => Task.FromResult(new EngineResult { Ok = false, Error = "页码超出范围，这份文件共有 3 页。" }),
        };
        var viewModel = new MainViewModel(ToolCatalog.Load(), bridge);

        await viewModel.ExecuteToolAsync(viewModel.Tools[0], [], "D:\\out");

        Assert.False(viewModel.IsRunning);
        Assert.Contains("页码超出范围", viewModel.StatusText);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithoutBridge_ShowsNotReady()
    {
        var viewModel = new MainViewModel(ToolCatalog.Load());

        await viewModel.ExecuteToolAsync(viewModel.Tools[0], [], "D:\\out");

        Assert.False(viewModel.IsRunning);
        Assert.Contains("引擎未就绪", viewModel.StatusText);
    }

    // ---------- 辅助 ----------

    private static EngineBridge CreateRealBridge()
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        var engine = EngineBridge.ResolveDefaultEnginePath();
        Assert.True(File.Exists(python), $"缺少引擎解释器（先建 .venv）：{python}");
        Assert.True(File.Exists(engine), $"缺少引擎脚本：{engine}");
        return new EngineBridge(python, engine);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "engine.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("未找到仓库根（backend/engine.py 锚点）");
        throw new UnreachableException(); // Assert.Fail 无 DoesNotReturn 标注，需显式满足编译器
    }

    private static async Task<int> WaitForHelperPid(string pidFile, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    return int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
                }
                catch (IOException)
                {
                    // powershell 可能还在写，稍后重试
                }
            }

            await Task.Delay(100);
        }

        Assert.Fail($"helper 未在 {limit.TotalSeconds}s 内写出 pidfile：{pidFile}");
        throw new UnreachableException(); // Assert.Fail 无 DoesNotReturn 标注，需显式满足编译器
    }

    private static bool ProcessAlive(int? pid)
    {
        if (pid is not { } value)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // PID 不存在 = 已退出
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>手写最小单页 PDF（对象偏移与 xref 逐字节计算），供引擎打开。</summary>
    private static byte[] BuildMinimalPdf()
    {
        using var stream = new MemoryStream();
        stream.Write("%PDF-1.4\n"u8);
        var offsets = new List<int>();
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add((int)stream.Position);
            stream.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
        }

        var xrefStart = (int)stream.Position;
        var xref = new StringBuilder("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            xref.Append($"{offset:D10} 00000 n \n");
        }

        xref.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
        stream.Write(Encoding.ASCII.GetBytes(xref.ToString()));
        return stream.ToArray();
    }

    /// <summary>测试用临时目录：释放时递归删除（含仍被占用的容忍重试）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-bridge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 句柄尚未完全释放时容忍残留，由系统临时目录清理兜底
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
