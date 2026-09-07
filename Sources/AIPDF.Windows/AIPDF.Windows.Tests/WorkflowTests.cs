// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 流程工具组测试：批次状态机（顺序执行/逐项失败不中断/取消）、预设序列化（密码零落盘）、
/// 记录导出（CSV 转义与 BOM）、VM 参数映射，以及真引擎集成（批量合并 3 个 PDF / 三项队列 / 两步流程）。
/// </summary>
public sealed class WorkflowTests
{
    // ======================= 基础设施 =======================

    /// <summary>假桥：不启动进程，按预设结果序列返回；真引擎集成用例用真桥。</summary>
    private sealed class FakeBridge : EngineBridge
    {
        public FakeBridge() : base("nonexistent-python", "nonexistent-engine")
        {
        }

        public List<EngineRequest> Requests { get; } = [];

        public List<EngineResult> Results { get; } = [];

        public Func<EngineRequest, CancellationToken, int, Task<EngineResult>>? Handler { get; set; }

        private int _calls;

        public override Task<EngineResult> ExecuteAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            var call = _calls++;
            Requests.Add(request);
            if (Handler is not null)
            {
                return Handler(request, cancellationToken, call);
            }

            var result = Results.Count == 0 ? OkResult(call) : Results[Math.Min(call, Results.Count - 1)];
            return Task.FromResult(result);
        }

        internal static EngineResult OkResult(int call) => new()
        {
            Ok = true,
            Outputs = [$"D:\\out\\AIPDF-{call:000000}-000{call}\\输出-{call}.pdf"],
            OutputDir = $"D:\\out\\AIPDF-{call:000000}-000{call}",
            ExitCode = 0,
        };

        internal static EngineResult FailResult(string error) => new()
        {
            Ok = false,
            Error = error,
            ExitCode = 1,
        };
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "aipdf-workflow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string dir, string name, byte[] content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static IReadOnlyList<Tool> RealCatalog() => ToolCatalog.Load();

    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ======================= 批次状态机（FakeBridge） =======================

    [Fact]
    public async Task BatchRunner_Sequential_AllItemsComplete()
    {
        var dir = NewTempDir();
        try
        {
            var files = new[]
            {
                WriteFile(dir, "甲.pdf", [1]),
                WriteFile(dir, "乙.pdf", [2]),
                WriteFile(dir, "丙.pdf", [3]),
            };
            var bridge = new FakeBridge();
            bridge.Results.Add(FakeBridge.OkResult(0));
            bridge.Results.Add(FakeBridge.OkResult(1));
            bridge.Results.Add(FakeBridge.OkResult(2));
            var history = new WorkflowHistory();
            var vm = new BatchToolViewModel(bridge, RealCatalog(), history);
            vm.SelectedTool = vm.Tools.First(tool => tool.Id == "split");
            vm.AddFiles(files);
            Assert.Equal(3, vm.Queue.Count);
            Assert.All(vm.Queue, item => Assert.Equal(BatchItemState.Pending, item.State));

            await vm.RunAsync();

            Assert.All(vm.Queue, item => Assert.Equal(BatchItemState.Completed, item.State));
            Assert.Equal(3, bridge.Requests.Count);
            // 顺序执行：第 i 次请求对应第 i 项的文件，且一次请求一项（从不并行合并）
            Assert.Equal([files[0]], bridge.Requests[0].Files);
            Assert.Equal([files[1]], bridge.Requests[1].Files);
            Assert.Equal([files[2]], bridge.Requests[2].Files);
            Assert.Equal("split", bridge.Requests[0].Operation);
            Assert.False(vm.IsRunning);
            Assert.Contains("批量完成：3 成功，0 失败", vm.StatusText);
            Assert.Equal(3, history.Records.Count);
            Assert.All(history.Records, record => Assert.Equal("成功", record.Result));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BatchRunner_ItemFails_RemainingItemsContinue()
    {
        var dir = NewTempDir();
        try
        {
            var files = new[]
            {
                WriteFile(dir, "好1.pdf", [1]),
                WriteFile(dir, "坏.pdf", [2]),
                WriteFile(dir, "好2.pdf", [3]),
            };
            var bridge = new FakeBridge();
            bridge.Results.Add(FakeBridge.OkResult(0));
            bridge.Results.Add(FakeBridge.FailResult("处理失败，请检查文件格式和参数。"));
            bridge.Results.Add(FakeBridge.OkResult(2));
            var vm = new BatchToolViewModel(bridge, RealCatalog(), new WorkflowHistory());
            vm.SelectedTool = vm.Tools.First(tool => tool.Id == "split");
            vm.AddFiles(files);
            vm.OutputDir = dir;

            await vm.RunAsync();

            Assert.Equal(BatchItemState.Completed, vm.Queue[0].State);
            Assert.Equal(BatchItemState.Failed, vm.Queue[1].State);
            Assert.Contains("处理失败", vm.Queue[1].Error);
            Assert.Equal(BatchItemState.Completed, vm.Queue[2].State);
            Assert.Equal(3, bridge.Requests.Count);
            Assert.Contains("批量完成：2 成功，1 失败", vm.StatusText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BatchRunner_Cancel_CurrentStopsRemainingCancelled()
    {
        var dir = NewTempDir();
        try
        {
            var files = new[]
            {
                WriteFile(dir, "第一.pdf", [1]),
                WriteFile(dir, "第二.pdf", [2]),
                WriteFile(dir, "第三.pdf", [3]),
            };
            var bridge = new FakeBridge();
            var history = new WorkflowHistory();
            var vm = new BatchToolViewModel(bridge, RealCatalog(), history);
            vm.SelectedTool = vm.Tools.First(tool => tool.Id == "split");
            vm.AddFiles(files);
            vm.OutputDir = dir;

            bridge.Handler = (request, token, call) =>
            {
                if (call == 1)
                {
                    // 第二项进行中时取消批次：桥收到的令牌被取消 → 当前项停止
                    vm.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(FakeBridge.OkResult(call));
                }

                return Task.FromResult(FakeBridge.OkResult(call));
            };

            await vm.RunAsync();

            Assert.Equal(BatchItemState.Completed, vm.Queue[0].State);
            Assert.Equal(BatchItemState.Cancelled, vm.Queue[1].State);
            Assert.Equal(BatchItemState.Cancelled, vm.Queue[2].State);
            // 取消后不再发起新的引擎请求
            Assert.Equal(2, bridge.Requests.Count);
            Assert.False(vm.IsRunning);
            Assert.Contains("批次已取消", vm.StatusText);
            Assert.Contains("已取消", vm.StatusText);
            Assert.Equal(2, history.Records.Count);
            Assert.Equal("成功", history.Records[0].Result);
            Assert.Equal("已取消", history.Records[1].Result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BatchQueueBuilder_Merge_GroupsAllFilesIntoOneItem()
    {
        var dir = NewTempDir();
        try
        {
            var files = new[]
            {
                WriteFile(dir, "a.pdf", [1]),
                WriteFile(dir, "b.pdf", [2]),
                WriteFile(dir, "c.pdf", [3]),
            };
            var merge = RealCatalog().First(tool => tool.Id == "merge");

            var items = BatchQueueBuilder.Build(merge, files);

            var item = Assert.Single(items);
            Assert.Equal(3, item.Files.Count);
            Assert.Equal(BatchItemState.Pending, item.State);
            Assert.Equal("待处理", item.StateText);
            Assert.Contains("a.pdf", item.DisplayName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BatchQueueBuilder_OtherTool_OneItemPerFile()
    {
        var dir = NewTempDir();
        try
        {
            var files = new[]
            {
                WriteFile(dir, "a.pdf", [1]),
                WriteFile(dir, "b.pdf", [2]),
            };
            var split = RealCatalog().First(tool => tool.Id == "split");

            var items = BatchQueueBuilder.Build(split, files);

            Assert.Equal(2, items.Count);
            Assert.Equal(["a.pdf", "b.pdf"], items.Select(item => item.DisplayName));
            Assert.All(items, item => Assert.Single(item.Files));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BatchQueueBuilder_FiltersMissingFiles()
    {
        var split = RealCatalog().First(tool => tool.Id == "split");

        var items = BatchQueueBuilder.Build(split, ["Z:\\不存在\\nope.pdf"]);

        Assert.Empty(items);
    }

    // ======================= VM 参数映射 =======================

    [Fact]
    public void WorkflowToolViewModel_BuildRequest_MapsStepsOptionsAndPassword()
    {
        var catalog = RealCatalog();
        var vm = new WorkflowToolViewModel(new FakeBridge(), catalog, new WorkflowPresetStore(NewTempDir()), new WorkflowHistory());
        var dir = NewTempDir();
        try
        {
            var file = WriteFile(dir, "文档.pdf", [1]);
            vm.AddFiles([file]);
            vm.OutputDir = dir;
            vm.Password = "pw-密码";

            vm.AddStepInternal();
            vm.Steps[0].SelectedOperation = catalog.First(tool => tool.Id == "rotate");
            vm.Steps[0].Options.First(field => field.Key == "pages").Value = "1-2";
            vm.Steps[0].Options.First(field => field.Key == "angle").Value = "180";
            vm.AddStepInternal();
            vm.Steps[1].SelectedOperation = catalog.First(tool => tool.Id == "protect");
            vm.Steps[1].Options.First(field => field.Key == "newPassword").Value = "step-secret";

            var request = vm.BuildRequest();

            Assert.Equal("workflow", request.Operation);
            Assert.Equal([file], request.Files);
            Assert.Equal(dir, request.OutputDir);
            Assert.Equal("pw-密码", Assert.IsType<string>(request.Options["password"]!));
            var steps = Assert.IsType<List<object?>>(request.Options["steps"]);
            Assert.Equal(2, steps.Count);

            var step1 = Assert.IsType<Dictionary<string, object?>>(steps[0]);
            Assert.Equal("rotate", step1["operation"]);
            var options1 = Assert.IsType<Dictionary<string, string>>(step1["options"]);
            Assert.Equal("180", options1["angle"]);
            Assert.Equal("1-2", options1["pages"]);

            var step2 = Assert.IsType<Dictionary<string, object?>>(steps[1]);
            Assert.Equal("protect", step2["operation"]);
            var options2 = Assert.IsType<Dictionary<string, string>>(step2["options"]);
            Assert.Equal("step-secret", options2["newPassword"]);

            // 请求 JSON 形状与引擎 workflow 分支契约一致（中文原样）
            var json = JsonSerializer.Serialize(request.Options, RelaxedJson);
            Assert.Contains("\"operation\":\"rotate\"", json);
            Assert.Contains("\"operation\":\"protect\"", json);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WorkflowToolViewModel_BuildRequest_EmptyPassword_OmitsKey()
    {
        var catalog = RealCatalog();
        var vm = new WorkflowToolViewModel(new FakeBridge(), catalog, new WorkflowPresetStore(NewTempDir()), new WorkflowHistory());
        var dir = NewTempDir();
        try
        {
            var file = WriteFile(dir, "文档.pdf", [1]);
            vm.AddFiles([file]);
            vm.OutputDir = dir;
            vm.AddStepInternal();

            var json = JsonSerializer.Serialize(vm.BuildRequest().Options, RelaxedJson);

            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowToolViewModel_ExecuteWithoutFiles_ShowsValidationAndSkipsBridge()
    {
        var bridge = new FakeBridge();
        var catalog = RealCatalog();
        var vm = new WorkflowToolViewModel(bridge, catalog, new WorkflowPresetStore(NewTempDir()), new WorkflowHistory());

        await vm.ExecuteAsync();

        Assert.Contains("请先添加文件", vm.StatusText);
        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void BatchToolViewModel_BuildRequestFor_MapsToolOptionsAndFiles()
    {
        var catalog = RealCatalog();
        var dir = NewTempDir();
        try
        {
            var files = new[] { WriteFile(dir, "文档.pdf", [1]) };
            var bridge = new FakeBridge();
            var vm = new BatchToolViewModel(bridge, catalog, new WorkflowHistory());
            vm.SelectedTool = catalog.First(tool => tool.Id == "rotate");
            vm.Options.First(field => field.Key == "angle").Value = "270";
            vm.AddFiles(files);
            vm.OutputDir = dir;

            var request = vm.BuildRequestFor(vm.Queue[0]);

            Assert.Equal("rotate", request.Operation);
            Assert.Equal(files, request.Files);
            Assert.Equal(dir, request.OutputDir);
            Assert.Equal("270", Assert.IsType<string>(request.Options["angle"]!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BatchToolViewModel_RunWithoutQueue_ShowsValidationAndSkipsBridge()
    {
        var bridge = new FakeBridge();
        var vm = new BatchToolViewModel(bridge, RealCatalog(), new WorkflowHistory());
        vm.SelectedTool = vm.Tools.First(tool => tool.Id == "split");
        vm.OutputDir = NewTempDir();

        await vm.RunAsync();

        Assert.Contains("请先添加文件", vm.StatusText);
        Assert.Empty(bridge.Requests);
    }

    // ======================= 预设序列化（密码跳过） =======================

    [Theory]
    [InlineData("password", true)]
    [InlineData("newPassword", true)]
    [InlineData("Password", true)]
    [InlineData("pages", false)]
    [InlineData("fontsize", false)]
    public void IsPasswordKey_MatchesVariants(string key, bool expected)
        => Assert.Equal(expected, WorkflowPreset.IsPasswordKey(key));

    [Fact]
    public void Preset_Sanitize_RemovesPasswordKeysKeepsOthers()
    {
        var preset = new WorkflowPreset
        {
            Name = "流程",
            Steps =
            [
                new()
                {
                    Operation = "rotate",
                    Options = new Dictionary<string, string> { ["angle"] = "90", ["pages"] = "1-3" },
                },
            ],
        };

        preset.Steps.Add(new WorkflowStep
        {
            Operation = "protect",
            Options = new Dictionary<string, string> { ["newPassword"] = "秘密", ["allowPrint"] = "true" },
        });

        var clean = WorkflowPreset.Sanitize(preset);

        Assert.Equal("90", clean.Steps[0].Options["angle"]);
        Assert.Equal("true", clean.Steps[1].Options["allowPrint"]);
        Assert.False(clean.Steps[1].Options.ContainsKey("newPassword"));
        // 原对象不被修改
        Assert.Equal("秘密", preset.Steps[1].Options["newPassword"]);
    }

    [Fact]
    public void PresetStore_Save_NeverWritesPasswordKeysOrValues()
    {
        var dir = NewTempDir();
        try
        {
            var store = new WorkflowPresetStore(dir);
            var preset = new WorkflowPreset
            {
                Name = "加密归档流程",
                Steps =
                [
                    new() { Operation = "unlock", Options = new Dictionary<string, string> { ["password"] = "SuperSecret123" } },
                    new()
                    {
                        Operation = "protect",
                        Options = new Dictionary<string, string> { ["newPassword"] = "NewSecret456", ["allowPrint"] = "true" },
                    },
                ],
            };

            store.Save(preset);

            var raw = File.ReadAllText(Path.Combine(dir, "加密归档流程.json"));
            Assert.DoesNotContain("SuperSecret123", raw);
            Assert.DoesNotContain("NewSecret456", raw);
            Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
            // 读回不含密码字段，其余参数保留
            var loaded = store.Load("加密归档流程");
            Assert.Equal(["unlock", "protect"], loaded.Steps.Select(step => step.Operation));
            Assert.Equal("true", loaded.Steps[1].Options["allowPrint"]);
            Assert.False(loaded.Steps[0].Options.ContainsKey("password"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PresetStore_SaveAndLoad_RoundTripPreservesSteps()
    {
        var dir = NewTempDir();
        try
        {
            var store = new WorkflowPresetStore(dir);
            var preset = new WorkflowPreset
            {
                Name = "周报归档流程",
                Steps =
                [
                    new()
                    {
                        Operation = "rotate",
                        Options = new Dictionary<string, string> { ["pages"] = "", ["angle"] = "90" },
                    },
                    new()
                    {
                        Operation = "watermark",
                        Options = new Dictionary<string, string> { ["text"] = "内部资料·2026", ["opacity"] = "0.2" },
                    },
                ],
            };

            store.Save(preset);

            Assert.Contains("周报归档流程", store.ListNames());
            var loaded = store.Load("周报归档流程");
            Assert.Equal(1, loaded.Schema);
            Assert.Equal("workflow", loaded.ToolId);
            Assert.Equal(2, loaded.Steps.Count);
            Assert.Equal("rotate", loaded.Steps[0].Operation);
            Assert.Equal("90", loaded.Steps[0].Options["angle"]);
            Assert.Equal("watermark", loaded.Steps[1].Operation);
            Assert.Equal("内部资料·2026", loaded.Steps[1].Options["text"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PresetStore_LoadMissing_ThrowsAndDeleteRemoves()
    {
        var dir = NewTempDir();
        try
        {
            var store = new WorkflowPresetStore(dir);
            Assert.Throws<FileNotFoundException>(() => store.Load("不存在"));

            store.Save(new WorkflowPreset
            {
                Name = "临时预设",
                Steps = [new WorkflowStep { Operation = "rotate" }],
            });
            store.Delete("临时预设");
            Assert.DoesNotContain("临时预设", store.ListNames());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PresetStore_SafeFileName_ReplacesInvalidCharacters()
    {
        Assert.Equal("a-b-c", WorkflowPresetStore.SafeFileName("a<b>c"));
        Assert.Equal("未命名预设", WorkflowPresetStore.SafeFileName("   "));
    }

    // ======================= 记录与导出 =======================

    [Fact]
    public void History_ExportCsv_WritesHeaderEscapesFieldsAndBom()
    {
        var history = new WorkflowHistory();
        history.Add(new WorkflowHistoryRecord(
            new DateTime(2026, 9, 7, 10, 30, 0), "工作流程", "a.pdf; b.pdf", "D:\\out\\AIPDF-1", "成功", "", 1.5));
        history.Add(new WorkflowHistoryRecord(
            new DateTime(2026, 9, 7, 10, 31, 0), "合并 PDF（批量）", "x, \"y\"\n第二行.pdf", "D:\\out\\2", "失败", "错误, 含\"引号\"", null));
        var path = Path.Combine(NewTempDir(), "记录.csv");
        try
        {
            history.ExportCsv(path);

            var bytes = File.ReadAllBytes(path);
            // UTF-8 BOM：Excel 打开中文不乱码
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes[3..]);
            var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("时间,工具,输入,输出,结果,详情,耗时（秒）", lines[0]);
            Assert.Contains("工作流程", lines[1]);
            Assert.EndsWith("1.5", lines[1]);
            // 含逗号/引号/换行的字段整体加引号且内部引号翻倍
            Assert.Contains("\"x, \"\"y\"\"", lines[2]);
            Assert.Contains("\"错误, 含\"\"引号\"\"\"", lines[2]);
            Assert.EndsWith(",", lines[2]); // 空耗时字段
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void History_AddAndClear_TracksRecords()
    {
        var history = new WorkflowHistory();
        history.Add(new WorkflowHistoryRecord(DateTime.Now, "合并 PDF", "a.pdf", "D:\\out", "成功", "", null));
        history.Add(new WorkflowHistoryRecord(DateTime.Now, "拆分 PDF", "b.pdf", "D:\\out", "失败", "处理失败", null));
        Assert.Equal(2, history.Records.Count);

        history.Clear();

        Assert.Empty(history.Records);
    }

    [Fact]
    public void HistoryToolViewModel_ExportCsv_ReportsCountAndStatus()
    {
        var history = new WorkflowHistory();
        history.Add(new WorkflowHistoryRecord(DateTime.Now, "合并 PDF", "a.pdf", "D:\\out", "成功", "", null));
        history.Add(new WorkflowHistoryRecord(DateTime.Now, "压缩 PDF", "b.pdf", "D:\\out", "成功", "", null));
        var vm = new HistoryToolViewModel(history);
        var path = Path.Combine(NewTempDir(), "导出.csv");
        try
        {
            var count = vm.ExportCsv(path);

            Assert.Equal(2, count);
            Assert.Contains("2 条记录", vm.StatusText);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ======================= 真引擎集成 =======================

    [Fact]
    public async Task BatchRunner_RealEngine_MergeThreePdfs_QueueCompletes()
    {
        var bridge = new EngineBridge();
        var inputDir = NewTempDir();
        var outDir = NewTempDir();
        try
        {
            var a = WriteFile(inputDir, "甲.pdf", TestPdfFactory.Create((595, 842), (595, 842)));
            var b = WriteFile(inputDir, "乙.pdf", TestPdfFactory.Create((595, 842), (595, 842), (595, 842)));
            var c = WriteFile(inputDir, "丙.pdf", TestPdfFactory.Create((595, 842), (595, 842), (595, 842), (595, 842)));
            var merge = RealCatalog().First(tool => tool.Id == "merge");
            var vm = new BatchToolViewModel(bridge, [merge], new WorkflowHistory());
            vm.SelectedTool = merge;
            vm.AddFiles([a, b, c]);
            vm.OutputDir = outDir;

            await vm.RunAsync();

            Assert.Equal(BatchItemState.Completed, vm.Queue[0].State);
            Assert.StartsWith("批量完成", vm.StatusText);
            var taskDir = vm.Queue[0].OutputDir;
            Assert.NotNull(taskDir);
            Assert.StartsWith(outDir, taskDir, StringComparison.Ordinal);
            Assert.Contains("AIPDF-", taskDir);
            var merged = Directory.GetFiles(taskDir!, "*.pdf").Single();
            using var document = new PdfPreviewDocument(File.ReadAllBytes(merged));
            Assert.Equal(9, document.PageCount); // 2 + 3 + 4
            // 源文件保持只读不动
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
            Assert.True(File.Exists(c));
        }
        finally
        {
            Directory.Delete(inputDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task BatchRunner_RealEngine_ThreeRotateItems_AllCompleteEachInOwnTaskDir()
    {
        var bridge = new EngineBridge();
        var inputDir = NewTempDir();
        var outDir = NewTempDir();
        try
        {
            var files = new[]
            {
                WriteFile(inputDir, "一.pdf", TestPdfFactory.Create((595, 842))),
                WriteFile(inputDir, "二.pdf", TestPdfFactory.Create((595, 842))),
                WriteFile(inputDir, "三.pdf", TestPdfFactory.Create((595, 842))),
            };
            var rotate = RealCatalog().First(tool => tool.Id == "rotate");
            var vm = new BatchToolViewModel(bridge, [rotate], new WorkflowHistory());
            vm.SelectedTool = rotate;
            vm.Options.First(field => field.Key == "angle").Value = "90";
            vm.AddFiles(files);
            vm.OutputDir = outDir;

            await vm.RunAsync();

            Assert.All(vm.Queue, item => Assert.Equal(BatchItemState.Completed, item.State));
            Assert.StartsWith("批量完成：3 成功，0 失败", vm.StatusText);
            var taskDirs = vm.Queue.Select(item => item.OutputDir!).ToList();
            Assert.Equal(3, taskDirs.Select(dir => dir).Distinct().Count());
            Assert.All(taskDirs, dir => Assert.StartsWith(outDir, dir, StringComparison.Ordinal));
            foreach (var item in vm.Queue)
            {
                var output = Directory.GetFiles(item.OutputDir!, "*.pdf").Single();
                using var document = new PdfPreviewDocument(File.ReadAllBytes(output));
                Assert.Equal(1, document.PageCount);
            }
        }
        finally
        {
            Directory.Delete(inputDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowToolViewModel_RealEngine_TwoStepRotate_ChainsSteps()
    {
        var catalog = RealCatalog();
        var history = new WorkflowHistory();
        var vm = new WorkflowToolViewModel(new EngineBridge(), catalog, new WorkflowPresetStore(NewTempDir()), history);
        var inputDir = NewTempDir();
        var outDir = NewTempDir();
        try
        {
            var file = WriteFile(inputDir, "文档.pdf", TestPdfFactory.Create((595, 842), (595, 842)));
            vm.AddFiles([file]);
            vm.OutputDir = outDir;
            vm.AddStepInternal();
            vm.Steps[0].SelectedOperation = catalog.First(tool => tool.Id == "rotate");
            vm.Steps[0].Options.First(field => field.Key == "angle").Value = "90";
            vm.AddStepInternal();
            vm.Steps[1].SelectedOperation = catalog.First(tool => tool.Id == "rotate");
            vm.Steps[1].Options.First(field => field.Key == "angle").Value = "270";

            await vm.ExecuteAsync();

            Assert.StartsWith("完成 工作流程", vm.StatusText);
            Assert.False(vm.IsRunning);
            var record = Assert.Single(history.Records);
            Assert.Equal("成功", record.Result);
            Assert.NotNull(record.Outputs);
            // 引擎 workflow 每一步保留输出副本：01-rotate 与 02-rotate 各一份
            var allPdfs = Directory.GetFiles(record.Outputs!, "*.pdf", SearchOption.AllDirectories);
            Assert.Equal(2, allPdfs.Length);
            var final = allPdfs.Single(path => path.Contains("02-rotate", StringComparison.Ordinal));
            using var document = new PdfPreviewDocument(File.ReadAllBytes(final));
            Assert.Equal(2, document.PageCount);
        }
        finally
        {
            Directory.Delete(inputDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }
}
