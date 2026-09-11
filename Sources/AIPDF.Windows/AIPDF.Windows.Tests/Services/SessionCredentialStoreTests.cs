// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T9 会话密码驻留测试：存取/清除、大小写不敏感路径键、不落盘（三处持久化无密码字段/字面）、
// 密码仅经 EngineBridge options.password 通道逐请求传递。
using System.IO;
using System.Text.Json;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class SessionCredentialStoreTests
{
    private const string Secret = "S3CRET-T9-DoNotPersist-7788";

    // ---------------------------------------------------------------- 存取 / 清除

    [Fact]
    public void Store_Then_Get_RoundTrips()
    {
        var store = new SessionCredentialStore();
        store.Store(@"C:\docs\report.pdf", Secret);

        Assert.True(store.Has(@"C:\docs\report.pdf"));
        Assert.Equal(Secret, store.Get(@"C:\docs\report.pdf"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void PathKey_IsCaseInsensitive()
    {
        var store = new SessionCredentialStore();
        store.Store(@"C:\Docs\Report.PDF", Secret);

        // Windows 路径大小写不敏感：同一文档的不同大小写写法命中同一驻留密码
        Assert.Equal(Secret, store.Get(@"c:\docs\report.pdf"));
        Assert.Equal(Secret, store.Get(@"C:\DOCS\REPORT.PDF"));
        Assert.True(store.Has(@"c:/docs/REPORT.pdf")); // 分隔符差异亦被 GetFullPath 归一
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Store_EmptyPassword_RetractsEntry()
    {
        var store = new SessionCredentialStore();
        store.Store("/tmp/a.pdf", Secret);
        Assert.True(store.Has("/tmp/a.pdf"));

        store.Store("/tmp/a.pdf", null);
        Assert.False(store.Has("/tmp/a.pdf"));

        store.Store("/tmp/a.pdf", Secret);
        store.Store("/tmp/a.pdf", "");
        Assert.False(store.Has("/tmp/a.pdf"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Get_MissingPath_ReturnsNull()
    {
        var store = new SessionCredentialStore();
        Assert.Null(store.Get(@"C:\docs\not-opened.pdf"));
        Assert.False(store.Has(@"C:\docs\not-opened.pdf"));
    }

    [Fact]
    public void Remove_ClearsOnlyTargetPath()
    {
        var store = new SessionCredentialStore();
        store.Store(@"C:\docs\a.pdf", "pw-a");
        store.Store(@"C:\docs\b.pdf", "pw-b");

        Assert.True(store.Remove(@"C:\docs\a.pdf"));
        Assert.False(store.Has(@"C:\docs\a.pdf"));
        Assert.Equal("pw-b", store.Get(@"C:\docs\b.pdf"));
        Assert.False(store.Remove(@"C:\docs\a.pdf")); // 已移除再删返回 false
    }

    [Fact]
    public void ClearAll_DropsEverything()
    {
        var store = new SessionCredentialStore();
        store.Store(@"C:\docs\a.pdf", "pw-a");
        store.Store(@"C:\docs\b.pdf", "pw-b");

        store.ClearAll();

        Assert.Equal(0, store.Count);
        Assert.Null(store.Get(@"C:\docs\a.pdf"));
        Assert.Null(store.Get(@"C:\docs\b.pdf"));
    }

    [Fact]
    public void Store_IsThreadSafe_ConcurrentAccessKeepsLastValue()
    {
        var store = new SessionCredentialStore();
        Parallel.For(0, 200, i => store.Store(@"C:\docs\shared.pdf", "pw-" + i));

        Assert.True(store.Count <= 1);
        Assert.StartsWith("pw-", store.Get(@"C:\docs\shared.pdf") ?? "", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 不落盘（红线）

    [Fact]
    public void PersistedStores_HaveNoPasswordFieldOrLiteral()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aipdf-t9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string docPath = @"C:\docs\sample.pdf";

            // 三处持久化各写入一条真实形态的数据（路径 / 页码 / 缩放 / 标签），随后扫描原始 JSON。
            new ReadingStateStore(Path.Combine(dir, "reading-state")).Save("key1", new ReadingState
            {
                Path = docPath,
                LastPage = 3,
                LastZoom = 150,
                PanelKey = ReadingState.PanelThumbs,
            });

            new RecentFilesStore(dir).Save(new RecentFilesDocument
            {
                Items = [new RecentFileEntry { Path = docPath, Name = "sample.pdf", LastPage = 3, LastZoom = 150 }],
            });

            new SessionStore(Path.Combine(dir, "session.json")).Save(new SessionState
            {
                Tabs = [new SessionTab { Path = docPath, Active = true }],
            });

            var persisted = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories);
            Assert.Equal(3, persisted.Length);
            foreach (var file in persisted)
            {
                var raw = File.ReadAllText(file);
                Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
                Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
            }

            // 结构性红线（防未来回归）：三处持久化类型不得新增任何 password 成员。
            var persistedTypes = new[]
            {
                typeof(ReadingState), typeof(ReadingStateBookmark),
                typeof(RecentFilesDocument), typeof(RecentFileEntry),
                typeof(SessionState), typeof(SessionTab), typeof(SessionWindow), typeof(SessionLeftPanel),
            };
            foreach (var type in persistedTypes)
            {
                Assert.DoesNotContain(
                    type.GetProperties(),
                    p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(
                    type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                    f => f.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
            }

            // 凭据驻留无任何写盘副作用：驻留后目录内容不变。
            var before = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            var credentials = new SessionCredentialStore();
            credentials.Store(docPath, Secret);
            var after = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            Assert.Equal(before, after);
            Assert.Equal(Secret, credentials.Get(@"c:\docs\SAMPLE.PDF"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 密码仅经 options 通道

    [Fact]
    public async Task EngineSave_PassesPasswordOnlyViaOptions()
    {
        var bridge = new CapturingBridge();
        var engine = new EngineAnnotationSaveEngine(bridge);

        await engine.SaveAnnotationsAsync(
            @"C:\docs\secret.pdf",
            [new Dictionary<string, object?> { ["action"] = "add", ["id"] = "r1" }],
            Secret,
            CancellationToken.None);

        var request = Assert.IsType<EngineRequest>(bridge.LastRequest);
        Assert.Equal("save_annotations", request.Operation);
        Assert.Equal(Secret, request.Options["password"]);

        // 密码绝不进 files / outputDir / operation（逐请求 options 通道唯一入口）
        Assert.DoesNotContain(Secret, request.Operation);
        Assert.DoesNotContain(Secret, request.Files);
        Assert.DoesNotContain(Secret, request.OutputDir);

        // 序列化后的请求体：密码只出现在 options 段。
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["operation"] = request.Operation,
            ["files"] = request.Files,
            ["options"] = request.Options,
            ["outputDir"] = request.OutputDir,
        });
        Assert.Contains("\"password\":\"" + Secret + "\"", json, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(json, Secret));
    }

    [Fact]
    public async Task EngineSave_NoPassword_OmitsOptionsKey()
    {
        var bridge = new CapturingBridge();
        var engine = new EngineAnnotationSaveEngine(bridge);

        await engine.SaveAnnotationsAsync(@"C:\docs\plain.pdf", [], null, CancellationToken.None);
        Assert.False(bridge.LastRequest!.Options.ContainsKey("password"));

        await engine.SaveAnnotationsAsync(@"C:\docs\plain.pdf", [], "", CancellationToken.None);
        Assert.False(bridge.LastRequest!.Options.ContainsKey("password"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>捕获请求的桥替身：只记录最后一次 <see cref="EngineRequest"/>，不启动子进程。</summary>
    private sealed class CapturingBridge : EngineBridge
    {
        public CapturingBridge() : base("python", "engine.py")
        {
        }

        public EngineRequest? LastRequest { get; private set; }

        public override Task<EngineResult> ExecuteAsync(
            EngineRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new EngineResult { Ok = true });
        }
    }
}
