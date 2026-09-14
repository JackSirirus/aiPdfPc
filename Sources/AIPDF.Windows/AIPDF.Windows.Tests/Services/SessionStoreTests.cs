// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T6 会话恢复测试：round-trip、损坏回退（AC-28.2）、失效过滤（AC-29.1/29.2）、
// 恢复顺序（FR-29）、窗口钳制（M-6）、无密码红线。
using System.IO;
using System.Reflection;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class SessionStoreTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string SessionPath { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-t6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SessionPath = Path.Combine(Root, "session.json");
        }

        public SessionStore NewStore() => new(SessionPath);

        public string Doc(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "PDF-A");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 存储 round-trip / 损坏回退

    [Fact]
    public void SaveLoad_Roundtrip_PreservesSchemaTabsWindowLeftPanel_Utf8NoBom()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var a = fx.Doc("a.pdf");
        var b = fx.Doc("b.pdf");
        var c = fx.Doc("c.pdf");

        store.Save(new SessionState
        {
            Tabs = [new SessionTab { Path = a, Active = false }, new SessionTab { Path = b, Active = true }, new SessionTab { Path = c, Active = false }],
            Window = new SessionWindow { Width = 1200, Height = 800, X = 100, Y = 50, Maximized = true },
            LeftPanel = new SessionLeftPanel { Width = 240, ActiveTab = "bookmarks" },
        });

        var bytes = File.ReadAllBytes(fx.SessionPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // UTF-8 无 BOM

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(SessionState.CurrentSchema, loaded!.Schema);
        Assert.Equal(3, loaded.Tabs.Count);
        Assert.Equal(b, loaded.Tabs[1].Path);
        Assert.True(loaded.Tabs[1].Active);
        Assert.Equal(1200, loaded.Window.Width);
        Assert.Equal(800, loaded.Window.Height);
        Assert.Equal(100, loaded.Window.X);
        Assert.Equal(50, loaded.Window.Y);
        Assert.True(loaded.Window.Maximized);
        Assert.Equal(240, loaded.LeftPanel.Width);
        Assert.Equal("bookmarks", loaded.LeftPanel.ActiveTab);
    }

    [Fact]
    public void SerializedSession_ContainsNoPasswordField()
    {
        using var fx = new Fixture();
        fx.NewStore().Save(new SessionState
        {
            Tabs = [new SessionTab { Path = fx.Doc("secret.pdf"), Active = true }],
            LeftPanel = new SessionLeftPanel { ActiveTab = "thumbs" },
        });

        var json = File.ReadAllText(fx.SessionPath);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        using var fx = new Fixture();
        Assert.Null(fx.NewStore().Load());
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull_NoThrow()
    {
        using var fx = new Fixture();
        File.WriteAllText(fx.SessionPath, "{ 这不是 json ");
        Assert.Null(fx.NewStore().Load()); // AC-28.2：默认状态、不崩、不提示
    }

    [Fact]
    public void Load_UnsupportedSchema_ReturnsNull()
    {
        using var fx = new Fixture();
        File.WriteAllText(fx.SessionPath, """{"schemaVersion":999,"tabs":[],"window":{},"leftPanel":{}}""");
        Assert.Null(fx.NewStore().Load());
    }

    [Fact]
    public void Load_NullCollections_NormalizedWithoutThrow()
    {
        using var fx = new Fixture();
        File.WriteAllText(fx.SessionPath, """{"schemaVersion":1,"tabs":null,"window":null,"leftPanel":null}""");

        var loaded = fx.NewStore().Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Tabs);
        Assert.NotNull(loaded.Window);
        Assert.NotNull(loaded.LeftPanel);
        Assert.Equal("thumbs", loaded.LeftPanel.ActiveTab);
    }

    [Fact]
    public void Load_UnknownPanelKey_FallsBackToThumbs()
    {
        using var fx = new Fixture();
        File.WriteAllText(fx.SessionPath, """{"schemaVersion":1,"tabs":[],"window":{},"leftPanel":{"width":300,"activeTab":"nope"}}""");

        var loaded = fx.NewStore().Load();

        Assert.NotNull(loaded);
        Assert.Equal(SessionLeftPanel.DefaultPanelKey, loaded!.LeftPanel.ActiveTab);
    }

    // ---------------------------------------------------------------- 恢复计划：失效过滤 / 顺序 / 激活（AC-29.1/29.2）

    [Fact]
    public void Plan_SkipsMissingFile_KeepsOrder_CountsSkipped()
    {
        using var fx = new Fixture();
        var a = fx.Doc("a.pdf");
        var missing = Path.Combine(fx.Root, "gone.pdf");
        var c = fx.Doc("c.pdf");

        var state = new SessionState
        {
            Tabs =
            [
                new SessionTab { Path = a, Active = false },
                new SessionTab { Path = missing, Active = true }, // 文件已删除
                new SessionTab { Path = c, Active = false },
            ],
        };

        var plan = SessionRestorePlanner.Build(state, File.Exists);

        Assert.Equal(1, plan.SkippedCount);          // AC-29.1：1 个失效
        Assert.True(plan.HasAnyTab);
        Assert.Equal(2, plan.Tabs.Count);
        Assert.Equal(a, plan.Tabs[0].Path);          // 会话原序
        Assert.Equal(c, plan.Tabs[1].Path);
        Assert.True(plan.Tabs[1].Active);            // 原激活标签失效 → 回退最后有效标签
    }

    [Fact]
    public void Plan_AllFilesMissing_YieldsEmptyStartPageState()
    {
        using var fx = new Fixture();
        var state = new SessionState
        {
            Tabs =
            [
                new SessionTab { Path = Path.Combine(fx.Root, "x.pdf"), Active = true },
                new SessionTab { Path = Path.Combine(fx.Root, "y.pdf"), Active = false },
            ],
        };

        var plan = SessionRestorePlanner.Build(state, File.Exists);

        Assert.False(plan.HasAnyTab); // AC-29.2：起始页空态
        Assert.Empty(plan.Tabs);
        Assert.Equal(2, plan.SkippedCount);
    }

    [Fact]
    public void Plan_ActiveMarker_MarksLastActiveTab()
    {
        using var fx = new Fixture();
        var a = fx.Doc("a.pdf");
        var b = fx.Doc("b.pdf");
        var c = fx.Doc("c.pdf");

        var state = new SessionState
        {
            Tabs =
            [
                new SessionTab { Path = a, Active = true },
                new SessionTab { Path = b, Active = true },
                new SessionTab { Path = c, Active = false },
            ],
        };

        var plan = SessionRestorePlanner.Build(state, File.Exists);

        Assert.False(plan.Tabs.Single(t => t.Path == a).Active);
        Assert.True(plan.Tabs.Single(t => t.Path == b).Active); // 最后激活
        Assert.False(plan.Tabs.Single(t => t.Path == c).Active);
    }

    [Fact]
    public void Plan_DuplicatePaths_DedupedCaseInsensitive()
    {
        using var fx = new Fixture();
        var a = fx.Doc("a.pdf");

        var state = new SessionState
        {
            Tabs =
            [
                new SessionTab { Path = a, Active = false },
                new SessionTab { Path = a.ToUpperInvariant(), Active = true }, // 大小写不敏感去重
            ],
        };

        var plan = SessionRestorePlanner.Build(state, File.Exists);

        Assert.Single(plan.Tabs); // 不新建重复标签【D14】
        Assert.Equal(0, plan.SkippedCount);
    }

    // ---------------------------------------------------------------- 恢复顺序（FR-29）

    private sealed class RecordingTarget : ISessionRestoreTarget
    {
        public List<string> Calls { get; } = [];

        public void ApplyWindow(SessionWindow window) => Calls.Add("window");

        public void ApplyLeftPanel(SessionLeftPanel leftPanel) => Calls.Add("leftPanel");

        public void OpenSessionDocument(string path, bool active) => Calls.Add($"tab:{Path.GetFileName(path)}:{active}");

        public void ReportSkippedFiles(int skippedCount) => Calls.Add($"skipped:{skippedCount}");
    }

    [Fact]
    public async Task Coordinator_AppliesWindowThenPanelThenTabs_ReportsSkipped()
    {
        using var fx = new Fixture();
        var a = fx.Doc("a.pdf");
        var b = fx.Doc("b.pdf");
        var state = new SessionState
        {
            Tabs =
            [
                new SessionTab { Path = a, Active = false },
                new SessionTab { Path = Path.Combine(fx.Root, "missing.pdf"), Active = false },
                new SessionTab { Path = b, Active = true },
            ],
        };
        var target = new RecordingTarget();

        var plan = await SessionRestoreCoordinator.RestoreAsync(state, target, File.Exists);

        Assert.NotNull(plan);
        Assert.Equal(
            ["window", "leftPanel", "tab:a.pdf:False", "tab:b.pdf:True", "skipped:1"],
            target.Calls);
    }

    [Fact]
    public async Task Coordinator_NullState_DoesNothing()
    {
        var target = new RecordingTarget();

        var plan = await SessionRestoreCoordinator.RestoreAsync(null, target, File.Exists);

        Assert.Null(plan);
        Assert.Empty(target.Calls);
    }

    // ---------------------------------------------------------------- 窗口钳制（M-6）

    [Fact]
    public void Clamp_OffscreenPosition_BroughtInsideVirtualScreen()
    {
        var (x, y, w, h) = SessionWindowMath.ClampToVirtualScreen(
            5000, -3000, 1200, 800,
            virtualLeft: 0, virtualTop: 0, virtualWidth: 1920, virtualHeight: 1080,
            minWidth: 860, minHeight: 560);

        Assert.Equal(720, x);   // 1920 - 1200
        Assert.Equal(0, y);
        Assert.Equal(1200, w);
        Assert.Equal(800, h);
    }

    [Fact]
    public void Clamp_OversizedWindow_ShrunkToVirtualScreen_AndMinEnforced()
    {
        var (x, y, w, h) = SessionWindowMath.ClampToVirtualScreen(
            100, 100, 4000, 3000,
            virtualLeft: 0, virtualTop: 0, virtualWidth: 1920, virtualHeight: 1080,
            minWidth: 860, minHeight: 560);

        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
        Assert.Equal(0, x);
        Assert.Equal(0, y);

        var (_, _, tinyW, tinyH) = SessionWindowMath.ClampToVirtualScreen(
            10, 10, -5, 0,
            virtualLeft: 0, virtualTop: 0, virtualWidth: 1920, virtualHeight: 1080,
            minWidth: 860, minHeight: 560);
        Assert.Equal(860, tinyW);  // 非法尺寸回退最小尺寸
        Assert.Equal(560, tinyH);
    }

    [Fact]
    public void Clamp_EmptyVirtualScreen_KeepsMinSizeAndPosition()
    {
        var (x, y, w, h) = SessionWindowMath.ClampToVirtualScreen(
            12, 34, 0, 0,
            virtualLeft: 0, virtualTop: 0, virtualWidth: 0, virtualHeight: 0,
            minWidth: 860, minHeight: 560);

        Assert.Equal(12, x);
        Assert.Equal(34, y);
        Assert.Equal(860, w);
        Assert.Equal(560, h);
    }

    // ---------------------------------------------------------------- F25：翻译任务状态刻意不持久化

    [Fact]
    public void SessionState_HasNoTranslationOrSidecarMembers_TaskStateIsSessionOnly()
    {
        var members = typeof(SessionState)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();

        // 翻译任务状态（整篇/批量进度与产物、划词 sidecar）刻意不写进会话：类型不得有相关成员。
        Assert.DoesNotContain(members, name =>
            name.Contains("translation", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
    }
}
