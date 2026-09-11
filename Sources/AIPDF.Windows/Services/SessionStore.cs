// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T6 会话恢复【D22】：
//   - 落 %APPDATA%\AIPDF\session.json，schema 见 PRD 3.7.1（tabs / window / leftPanel）；
//   - 沿袭 WorkflowPresetStore 持久化模式：schema 版本 / UTF-8 无 BOM / 损坏静默回退；
//   - 会话是「窗口级状态」（应用长什么样），与 per-file reading-state（读到哪了）独立存储、互不影响；
//   - 恢复顺序（FR-29）：窗口几何 → 左栏状态 → 标签集合（异步加载，激活态标记最后激活）；
//   - 失效文件跳过（AC-29.1）、全部失效进起始页空态（AC-29.2）、session 损坏走默认（AC-28.2）；
//   - 显示器拓扑变化后把恢复窗口钳制到虚拟屏范围（M-6）。
// 红线：本文件不含任何密码字段（4.3.3；加密文档每次启动后首次打开仍需输入密码）。
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPDF.Windows.Services;

/// <summary>会话中的一个标签：文件路径 + 是否激活。</summary>
public sealed class SessionTab
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

/// <summary>会话中的窗口几何（尺寸 / 屏幕坐标 / 最大化态）。</summary>
public sealed class SessionWindow
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("maximized")]
    public bool Maximized { get; set; }
}

/// <summary>会话中的左栏状态（宽度 + 激活面板键）。</summary>
public sealed class SessionLeftPanel
{
    /// <summary>无有效记录时的默认面板键（与 ReaderPanelState.Thumbs 一致；Services 不依赖 ViewModels）。</summary>
    public const string DefaultPanelKey = "thumbs";

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("activeTab")]
    public string ActiveTab { get; set; } = DefaultPanelKey;
}

/// <summary>
/// 会话文档（schema 沿袭 WorkflowPresetStore；无密码字段）。
/// 字段与 PRD 3.7.1 代码块一一对应。
/// </summary>
public sealed class SessionState
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schemaVersion")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("tabs")]
    public List<SessionTab> Tabs { get; set; } = [];

    [JsonPropertyName("window")]
    public SessionWindow Window { get; set; } = new();

    [JsonPropertyName("leftPanel")]
    public SessionLeftPanel LeftPanel { get; set; } = new();
}

/// <summary>单个待恢复标签（已过失效过滤）与其激活标记。</summary>
public sealed record SessionTabPlan(string Path, bool Active);

/// <summary>
/// 恢复计划（纯数据，可脱离 UI 测试）：窗口几何 / 左栏状态 / 有效标签（按会话原序，active 标记最后激活）
/// + 失效跳过计数。全部失效时 <see cref="Tabs"/> 为空、<see cref="HasAnyTab"/> 为 false → 起始页空态。
/// </summary>
public sealed record SessionRestorePlan(
    SessionWindow Window,
    SessionLeftPanel LeftPanel,
    IReadOnlyList<SessionTabPlan> Tabs,
    int SkippedCount)
{
    /// <summary>是否存在可恢复标签（false = AC-29.2 起始页空态）。</summary>
    public bool HasAnyTab => Tabs.Count > 0;
}

/// <summary>恢复接收方：由宿主（MainWindow）实现，把计划套用到窗口/面板/标签宿主。</summary>
public interface ISessionRestoreTarget
{
    /// <summary>① 恢复窗口几何（尺寸/位置/最大化态，含 M-6 虚拟屏钳制）。</summary>
    void ApplyWindow(SessionWindow window);

    /// <summary>② 恢复左栏状态（宽度 + 全局激活面板；仅作无标签/起始页初始态，文件标签的 reading-state 面板优先）。</summary>
    void ApplyLeftPanel(SessionLeftPanel leftPanel);

    /// <summary>③ 打开一个有效文件标签（异步分批；<paramref name="active"/> 标记最后激活的标签）。</summary>
    void OpenSessionDocument(string path, bool active);

    /// <summary>失效文件汇总 Toast（AC-29.1；计数 ≥ 1 才调用）。</summary>
    void ReportSkippedFiles(int skippedCount);
}

/// <summary>
/// 会话存储：同 <see cref="WorkflowPresetStore"/> 范式（schema 版本 / UTF-8 无 BOM / 测试可注入路径）。
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true, // 容忍手工编辑/历史版本的大小写差异
        // 中文路径按原字符写入（本地状态文件保持可读），不影响解析往返
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly HashSet<string> KnownPanelKeys =
        new(["thumbs", "outline", "bookmarks", "search"], StringComparer.Ordinal);

    private readonly object _gate = new();

    /// <summary>测试注入临时文件路径；生产为 %APPDATA%\AIPDF\session.json。</summary>
    public SessionStore(string? filePath = null)
    {
        FilePath = filePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIPDF", "session.json");
    }

    public string FilePath { get; }

    /// <summary>
    /// 读取会话；缺文件 / JSON 损坏 / schema 不识别 / 不可读 → null（调用方走默认状态，AC-28.2）。
    /// 成功时做最小归一化：null 集合补空、空路径剔除、面板键收敛到已知值。
    /// </summary>
    public SessionState? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;

                var bytes = File.ReadAllBytes(FilePath);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bytes = bytes[3..]; // 容忍手工编辑留下的 BOM
                }

                var state = JsonSerializer.Deserialize<SessionState>(bytes, JsonOptions);
                if (state is null || state.Schema != SessionState.CurrentSchema) return null;

                return Normalize(state);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>写入会话（UTF-8 无 BOM，临时文件原子替换）。</summary>
    public void Save(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            var clean = Normalize(state);
            clean.Schema = SessionState.CurrentSchema;

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(clean, JsonOptions), Utf8NoBom);
            try
            {
                File.Move(temp, FilePath, overwrite: true); // 原子替换（崩溃不产生半截目标文件）
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    /// <summary>删除会话文件；不存在返回 false（测试/重置用）。</summary>
    public bool Delete()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                File.Delete(FilePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不影响主流程
        }
    }

    private static SessionState Normalize(SessionState state)
    {
        state.Tabs ??= [];
        state.Window ??= new SessionWindow();
        state.LeftPanel ??= new SessionLeftPanel();

        state.Tabs = [.. state.Tabs.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Path))];
        state.LeftPanel.ActiveTab = NormalizePanelKey(state.LeftPanel.ActiveTab);
        return state;
    }

    /// <summary>面板键收敛：仅接受四个已知键，其余回退缩略图。</summary>
    internal static string NormalizePanelKey(string? key)
        => key is not null && KnownPanelKeys.Contains(key) ? key : SessionLeftPanel.DefaultPanelKey;
}

/// <summary>
/// 恢复计划构建（纯逻辑）：按会话原序保留有效文件标签、跳过失效文件并计数，标记最后激活标签。
/// 路径按 OrdinalIgnoreCase 去重（与 TabHostViewModel 的 D14 去重一致）。
/// </summary>
public static class SessionRestorePlanner
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static SessionRestorePlan Build(SessionState state, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fileExists);

        var seen = new HashSet<string>(PathComparer);
        var valid = new List<(string Path, bool Active)>();
        var skipped = 0;

        foreach (var tab in state.Tabs ?? [])
        {
            if (tab is null || string.IsNullOrWhiteSpace(tab.Path)) continue;

            var path = tab.Path;
            if (!seen.Add(path)) continue; // 会话内重复路径：只保留首次

            if (ExistsSafe(fileExists, path))
            {
                valid.Add((path, tab.Active));
            }
            else
            {
                skipped++;
            }
        }

        // 激活态：会话中最后一个 Active 且有效者；失效/缺失则回退最后一个有效标签（FR-29）。
        var activeIndex = -1;
        for (var i = 0; i < valid.Count; i++)
        {
            if (valid[i].Active) activeIndex = i;
        }

        if (activeIndex < 0 && valid.Count > 0) activeIndex = valid.Count - 1;

        var tabs = valid
            .Select((entry, index) => new SessionTabPlan(entry.Path, index == activeIndex))
            .ToList();

        return new SessionRestorePlan(state.Window, state.LeftPanel, tabs, skipped);
    }

    private static bool ExistsSafe(Func<string, bool> fileExists, string path)
    {
        try
        {
            return fileExists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// 启动恢复编排（FR-29 顺序：窗口几何 → 左栏状态 → 标签集合异步加载 → 失效汇总）。
/// 通过 <see cref="ISessionRestoreTarget"/> 解耦 UI，便于 xunit 断言调用顺序。
/// </summary>
public static class SessionRestoreCoordinator
{
    /// <summary>
    /// 套用会话恢复。<paramref name="state"/> 为 null（缺文件/损坏）时直接返回 null（AC-28.2 默认状态）。
    /// <paramref name="interleaveYield"/> 在每次打开标签前 await，用于把标签加载让出 UI 线程（不阻塞启动）。
    /// </summary>
    public static async Task<SessionRestorePlan?> RestoreAsync(
        SessionState? state,
        ISessionRestoreTarget target,
        Func<string, bool> fileExists,
        Func<Task>? interleaveYield = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (state is null) return null;

        // ① 窗口几何（同步，先于任何 await，避免首帧跳动）
        target.ApplyWindow(state.Window);
        // ② 左栏状态（宽度 + 全局面板；文件标签的 reading-state 面板随后覆盖）
        target.ApplyLeftPanel(state.LeftPanel);

        var plan = SessionRestorePlanner.Build(state, fileExists);

        // ③ 标签集合：异步逐个打开，激活态标记最后激活（顺序由计划保证）
        foreach (var tab in plan.Tabs)
        {
            if (interleaveYield is not null) await interleaveYield();
            target.OpenSessionDocument(tab.Path, tab.Active);
        }

        // 失效文件汇总（无失效不打扰）
        if (plan.SkippedCount > 0) target.ReportSkippedFiles(plan.SkippedCount);

        return plan;
    }
}

/// <summary>
/// 窗口几何钳制（M-6）：显示器拓扑变化后，把恢复的窗口矩形夹回虚拟屏范围。
/// 纯函数（不依赖 WPF），便于直测。虚拟屏为空/非法时仅保证最小尺寸。
/// </summary>
public static class SessionWindowMath
{
    public static (double X, double Y, double Width, double Height) ClampToVirtualScreen(
        double x,
        double y,
        double width,
        double height,
        double virtualLeft,
        double virtualTop,
        double virtualWidth,
        double virtualHeight,
        double minWidth,
        double minHeight)
    {
        var clampedWidth = width > 0 ? width : minWidth;
        var clampedHeight = height > 0 ? height : minHeight;
        if (minWidth > 0) clampedWidth = Math.Max(clampedWidth, minWidth);
        if (minHeight > 0) clampedHeight = Math.Max(clampedHeight, minHeight);

        // 虚拟屏为空/非法：只保证最小尺寸，位置原样（由调用方决定默认居中）。
        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            return (x, y, clampedWidth, clampedHeight);
        }

        clampedWidth = Math.Min(clampedWidth, virtualWidth);
        clampedHeight = Math.Min(clampedHeight, virtualHeight);

        var maxX = virtualLeft + virtualWidth - clampedWidth;
        var maxY = virtualTop + virtualHeight - clampedHeight;

        return (
            Math.Min(Math.Max(x, virtualLeft), maxX),
            Math.Min(Math.Max(y, virtualTop), maxY),
            clampedWidth,
            clampedHeight);
    }
}
