// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views;
using AIPDF.Windows.Views.Security;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 安全工具组（T18）测试：
/// ① VM 单元（FakeBridge，无进程）：密码只经 Func 委托进入请求选项、错误传播进页面横幅、
///    比较双槽顺序、sign/redact 参数映射、校验拦截；
/// ② 源码审计：Tools/ 与 Views/Security/ 不出现 argv/env/控制台通道；
/// ③ 真引擎集成：加密→解锁往返（pypdf 验证加密态与解密后可读）、错误密码经 VM 走 UI 提示不崩溃。
/// </summary>
public sealed class SecurityTests
{
    // 合成测试口令（非真实凭据；与上游 tests/test_engine.py 的做法一致）。
    private const string RoundTripSecret = "t18-round-trip#42";
    private const string WrongSecret = "definitely-not-the-password";
    private const string TextMarker = "AIPDF-T18-ROUNDTRIP";

    private static readonly string RepoRoot = FindRepoRoot();

    // ---------- ① VM 单元（FakeBridge，无进程） ----------

    private sealed class FakeBridge : EngineBridge
    {
        public EngineRequest? LastRequest { get; private set; }
        public Func<EngineRequest, Task<EngineResult>>? Handler { get; set; }

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Handler is null
                ? Task.FromResult(new EngineResult { Ok = true, Outputs = ["out.pdf"], OutputDir = "D:\\out" })
                : Handler(request);
        }
    }

    private static Tool ToolOf(string id) => ToolCatalog.Load().First(tool => tool.Id == id);

    [Fact]
    public void SecurityPages_MapFiveSecurityToolsToViewModels()
    {
        var bridge = new FakeBridge();
        Assert.IsType<UnlockViewModel>(SecurityPages.CreateViewModel(ToolOf("unlock"), bridge));
        Assert.IsType<ProtectViewModel>(SecurityPages.CreateViewModel(ToolOf("protect"), bridge));
        Assert.IsType<SignViewModel>(SecurityPages.CreateViewModel(ToolOf("sign"), bridge));
        Assert.IsType<RedactViewModel>(SecurityPages.CreateViewModel(ToolOf("redact"), bridge));
        Assert.IsType<CompareViewModel>(SecurityPages.CreateViewModel(ToolOf("compare"), bridge));
        Assert.Null(SecurityPages.CreateViewModel(ToolOf("merge"), bridge)); // 非安全组不认领
    }

    [Fact]
    public void SecurityPages_RegisteredResolver_DoesNotClaimForeignTools()
    {
        ToolPageRegistry.Reset();
        try
        {
            SecurityPages.Register();
            Assert.Null(ToolPageRegistry.TryCreate(ToolOf("merge"), new FakeBridge()));
        }
        finally
        {
            ToolPageRegistry.Reset();
        }
    }

    [Fact]
    public async Task Unlock_PasswordReachesRequestOptionsOnly()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new UnlockViewModel(bridge, ToolOf("unlock"))
        {
            PasswordProvider = () => RoundTripSecret,
            OutputDir = workspace.Path,
        };
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(bridge.LastRequest is not null, "应发起引擎请求");
        Assert.Equal("unlock", bridge.LastRequest.Operation);
        Assert.Equal(RoundTripSecret, bridge.LastRequest.Options["password"]);
        // 硬门槛：密码明文不落在 VM 的任何字段或属性上（只能经委托按需进入请求 JSON）
        AssertSecretNotStored(viewModel, RoundTripSecret);
    }

    [Fact]
    public async Task Unlock_NoPasswordProvider_SendsEmptyPassword()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new UnlockViewModel(bridge, ToolOf("unlock")) { OutputDir = workspace.Path };
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("", bridge.LastRequest?.Options["password"]);
    }

    [Fact]
    public async Task Unlock_WrongPassword_EngineErrorReachesBanner()
    {
        var bridge = new FakeBridge
        {
            Handler = _ => Task.FromResult(new EngineResult
            {
                Ok = false,
                Error = "加密.pdf 需要正确的打开密码。",
            }),
        };
        var viewModel = new UnlockViewModel(bridge, ToolOf("unlock"))
        {
            PasswordProvider = () => WrongSecret,
            OutputDir = new TempWorkspace().Path,
        };
        viewModel.Files.Add("加密.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasError);
        Assert.Contains("需要正确的打开密码", viewModel.ErrorText);
        Assert.False(viewModel.IsRunning); // 不崩溃，状态复位
    }

    [Fact]
    public async Task Protect_MapsNewPasswordAndPermissionToggles()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new ProtectViewModel(bridge, ToolOf("protect"))
        {
            NewPasswordProvider = () => RoundTripSecret,
            AllowPrint = true,
            AllowCopy = false,
            OutputDir = workspace.Path,
        };
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(bridge.LastRequest);
        Assert.Equal(RoundTripSecret, bridge.LastRequest.Options["newPassword"]);
        Assert.Equal("true", bridge.LastRequest.Options["allowPrint"]);
        Assert.Equal("false", bridge.LastRequest.Options["allowCopy"]);
        AssertSecretNotStored(viewModel, RoundTripSecret);
    }

    [Fact]
    public async Task Protect_EmptyPassword_BlockedBeforeEngine()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new ProtectViewModel(bridge, ToolOf("protect"))
        {
            NewPasswordProvider = () => "",
            OutputDir = workspace.Path,
        };
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(bridge.LastRequest);
        Assert.Contains("非空", viewModel.ErrorText);
    }

    [Fact]
    public async Task Compare_TwoSlots_KeepOrderInRequest()
    {
        using var workspace = new TempWorkspace();
        var oldPdf = System.IO.Path.Combine(workspace.Path, "old.pdf");
        var newPdf = System.IO.Path.Combine(workspace.Path, "new.pdf");
        await File.WriteAllBytesAsync(oldPdf, [0x25, 0x50]);
        await File.WriteAllBytesAsync(newPdf, [0x25, 0x50]);

        var bridge = new FakeBridge();
        var viewModel = new CompareViewModel(bridge, ToolOf("compare")) { OutputDir = workspace.Path };
        viewModel.SetFirstFile(oldPdf);
        viewModel.SetSecondFile(newPdf);

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(bridge.LastRequest);
        Assert.Equal([oldPdf, newPdf], bridge.LastRequest!.Files);
    }

    [Fact]
    public async Task Compare_MissingSecondFile_BlockedWithEngineMessage()
    {
        using var workspace = new TempWorkspace();
        var only = System.IO.Path.Combine(workspace.Path, "only.pdf");
        await File.WriteAllBytesAsync(only, [0x25, 0x50]);

        var bridge = new FakeBridge();
        var viewModel = new CompareViewModel(bridge, ToolOf("compare")) { OutputDir = workspace.Path };
        viewModel.SetFirstFile(only);

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(bridge.LastRequest);
        Assert.Contains("正好两份", viewModel.ErrorText);
    }

    [Fact]
    public void Compare_AddDroppedFiles_FillsSlotsSequentially()
    {
        using var workspace = new TempWorkspace();
        var first = System.IO.Path.Combine(workspace.Path, "a.pdf");
        var second = System.IO.Path.Combine(workspace.Path, "b.pdf");
        var extra = System.IO.Path.Combine(workspace.Path, "c.pdf");
        foreach (var path in new[] { first, second, extra })
        {
            File.WriteAllBytes(path, [0x25, 0x50]);
        }

        var viewModel = new CompareViewModel(new FakeBridge(), ToolOf("compare"));
        viewModel.AddDroppedFiles([first, second, extra]);

        Assert.Equal(first, viewModel.FirstFile);
        Assert.Equal(second, viewModel.SecondFile);
        Assert.Equal([first, second], viewModel.EffectiveFiles); // 多余文件被忽略
    }

    [Fact]
    public async Task Sign_EmptySignature_BlockedBeforeEngine()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new SignViewModel(bridge, ToolOf("sign")) { OutputDir = workspace.Path };
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(bridge.LastRequest);
        Assert.Contains("签名姓名或选择签名图片", viewModel.ErrorText);
    }

    [Fact]
    public async Task Sign_MapsCatalogDefaultsAndText()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new SignViewModel(bridge, ToolOf("sign"))
        {
            SignatureText = "张三",
            OutputDir = workspace.Path,
        };
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var options = bridge.LastRequest?.Options;
        Assert.NotNull(options);
        Assert.Equal("张三", options["text"]);
        // 位置与尺寸预填 catalog RECT 默认值（点坐标，左上角原点）
        Assert.Equal("48", options["x"]);
        Assert.Equal("72", options["y"]);
        Assert.Equal("240", options["width"]);
        Assert.Equal("64", options["height"]);
    }

    [Fact]
    public async Task Redact_MapsKeywordsAndRect()
    {
        using var workspace = new TempWorkspace();
        var bridge = new FakeBridge();
        var viewModel = new RedactViewModel(bridge, ToolOf("redact")) { OutputDir = workspace.Path };
        viewModel.Keywords = "SECRET-123\n身份证号";
        viewModel.Files.Add("synthetic.pdf");
        viewModel.Files.Add("synthetic2.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        var options = bridge.LastRequest?.Options;
        Assert.NotNull(options);
        Assert.Equal("SECRET-123\n身份证号", options["text"]);
        Assert.Equal("48", options["x"]);
        Assert.Equal("240", options["width"]);
        Assert.Equal(2, bridge.LastRequest.Files.Count);
    }

    [Fact]
    public async Task Execute_WithoutOutputDir_BlockedBeforeEngine()
    {
        var bridge = new FakeBridge();
        var viewModel = new UnlockViewModel(bridge, ToolOf("unlock")); // OutputDir 默认为空
        viewModel.Files.Add("synthetic.pdf");

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(bridge.LastRequest);
        Assert.Contains("输出文件夹", viewModel.ErrorText);
    }

    // ---------- ② 源码审计：密码不得走 argv/env/控制台 ----------

    [Fact]
    public void SecuritySources_PasswordNeverTouchesArgvEnvOrLogs()
    {
        // 审计范围 = 安全组自己的密码承载代码（Tools/SecurityViewModels.cs + Views/Security/）。
        // 其他工具组的文件（如 OCR helper 的进程调用）不在本组审计范围内。
        var windowsRoot = System.IO.Path.Combine(RepoRoot, "Sources", "AIPDF.Windows");
        var sources = new[] { System.IO.Path.Combine(windowsRoot, "Tools", "SecurityViewModels.cs") }
            .Concat(Directory.GetFiles(System.IO.Path.Combine(windowsRoot, "Views", "Security"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(System.IO.Path.Combine(windowsRoot, "Views", "Security"), "*.xaml", SearchOption.AllDirectories))
            .ToList();
        string[] forbidden =
        {
            "ArgumentList",            // 密码绝不进命令行参数
            "SetEnvironmentVariable",  // 绝不写环境变量
            "EnvironmentVariables",    // 绝不读写环境变量
            "Console.Write",           // 绝不写控制台（stdout 只归引擎 JSON）
            "Debug.WriteLine",
            "Trace.WriteLine",
            "File.AppendAllText",      // 绝不把响应体/内容写入文件日志（P3-A：诊断走 stderr，不落盘）
        };

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var content = File.ReadAllText(source);
            foreach (var marker in forbidden)
            {
                Assert.True(!content.Contains(marker, StringComparison.Ordinal),
                    $"{System.IO.Path.GetFileName(source)} 不应包含 {marker}（密码只能经 stdin 请求 JSON 进入引擎）");
            }
        }
    }

    // ---------- ③ 真引擎集成 ----------

    private static EngineBridge CreateRealBridge()
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        var engine = EngineBridge.ResolveDefaultEnginePath();
        Assert.True(File.Exists(python), $"缺少引擎解释器（先建 .venv）：{python}");
        Assert.True(File.Exists(engine), $"缺少引擎脚本：{engine}");
        return new EngineBridge(python, engine);
    }

    /// <summary>跑一次真实引擎加密，返回加密输出路径。</summary>
    private static async Task<string> ProtectAsync(EngineBridge bridge, string source, string outputDir)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await bridge.ExecuteAsync(new EngineRequest
        {
            Operation = "protect",
            Files = [source],
            Options = new Dictionary<string, object?>
            {
                ["newPassword"] = RoundTripSecret,
                ["allowPrint"] = "true",
                ["allowCopy"] = "false",
            },
            OutputDir = outputDir,
        }, timeout.Token);
        Assert.True(result.Ok, result.Error ?? "加密失败");
        return Assert.Single(result.Outputs);
    }

    [Fact]
    public async Task EncryptUnlockRoundTrip_PypdfVerifiesEncryptedStateAndReadableOutput()
    {
        var bridge = CreateRealBridge();
        using var workspace = new TempWorkspace();
        var source = System.IO.Path.Combine(workspace.Path, "往返测试.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.CreateWithText((612, 792, TextMarker)));

        // ① 加密：AES-256；输出必须处于加密态（pypdf 验证）
        var protectedPdf = await ProtectAsync(bridge, source, workspace.Path);
        RunPypdfAssert("""
            import sys
            from pypdf import PdfReader
            reader = PdfReader(sys.argv[1])
            assert reader.is_encrypted, "protect output must stay encrypted"
            print("PYPDF-ENCRYPTED-OK")
            """, protectedPdf);

        // ② 错误密码：引擎返回 ok=false 的错误 JSON（协议内响应），不崩溃
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            var wrong = await bridge.ExecuteAsync(new EngineRequest
            {
                Operation = "unlock",
                Files = [protectedPdf],
                Options = new Dictionary<string, object?> { ["password"] = WrongSecret },
                OutputDir = workspace.Path,
            }, timeout.Token);
            Assert.False(wrong.Ok);
            Assert.Contains("需要正确的打开密码", wrong.Error);
        }

        // ③ 正确密码解锁：输出不再是加密态，且原文可读（pypdf 验证）
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            var unlocked = await bridge.ExecuteAsync(new EngineRequest
            {
                Operation = "unlock",
                Files = [protectedPdf],
                Options = new Dictionary<string, object?> { ["password"] = RoundTripSecret },
                OutputDir = workspace.Path,
            }, timeout.Token);
            Assert.True(unlocked.Ok, unlocked.Error ?? "解锁失败");
            var output = Assert.Single(unlocked.Outputs);
            RunPypdfAssert("""
                import sys
                from pypdf import PdfReader
                reader = PdfReader(sys.argv[1])
                assert not reader.is_encrypted, "unlock output must not be encrypted"
                text = "".join((page.extract_text() or "") for page in reader.pages)
                assert sys.argv[2] in text, "unlock output text unreadable or lost"
                print("PYPDF-PLAIN-OK")
                """, output, TextMarker);
        }
    }

    [Fact]
    public async Task Unlock_WrongPassword_ViaViewModel_ShowsPasswordErrorWithoutCrash()
    {
        var bridge = CreateRealBridge();
        using var workspace = new TempWorkspace();
        var source = System.IO.Path.Combine(workspace.Path, "加密文档.pdf");
        await File.WriteAllBytesAsync(source, TestPdfFactory.CreateWithText((612, 792, TextMarker)));
        var protectedPdf = await ProtectAsync(bridge, source, workspace.Path);

        var main = new MainViewModel(ToolCatalog.Load(), bridge);
        var tool = main.Tools.First(t => t.Id == "unlock");
        var viewModel = Assert.IsType<UnlockViewModel>(SecurityPages.CreateViewModel(tool, bridge));
        viewModel.PasswordProvider = () => WrongSecret;
        viewModel.Files.Add(protectedPdf);
        viewModel.OutputDir = workspace.Path;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasError);
        Assert.Contains("需要正确的打开密码", viewModel.ErrorText); // UI 错误提示
        Assert.False(viewModel.IsRunning);
    }

    /// <summary>运行 pypdf 校验脚本（-c 参数数组；不传密码，任何 secret 都不进子进程 argv/env）。</summary>
    private static void RunPypdfAssert(string script, params string[] args)
    {
        var python = EngineBridge.ResolveDefaultPythonPath();
        Assert.True(File.Exists(python), $"缺少引擎解释器：{python}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        foreach (var argument in args)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "pypdf 校验超时");
        Assert.True(process.ExitCode == 0, $"pypdf 校验失败（退出码 {process.ExitCode}）：{stderr}{stdout}");
    }

    /// <summary>硬门槛断言：VM 全部实例字段与属性的字符串值都不等于密码明文。</summary>
    private static void AssertSecretNotStored(object viewModel, string secret)
    {
        for (var type = viewModel.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(string))
                {
                    Assert.NotEqual(secret, field.GetValue(viewModel) as string);
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (property.PropertyType == typeof(string) && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    Assert.NotEqual(secret, property.GetValue(viewModel) as string);
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "backend", "engine.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("未找到仓库根（backend/engine.py 锚点）");
        throw new UnreachableException();
    }

    /// <summary>测试用临时目录：释放时递归删除（句柄未完全释放时容忍残留）。</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-sec-" + Guid.NewGuid().ToString("N"));
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
