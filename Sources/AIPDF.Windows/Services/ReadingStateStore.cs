// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T5 阅读状态存储【D20】【R6】：
//   - 键 = SHA256(规范化绝对路径 + "|" + size + "|" + mtime)（ReadingStateKey，FR-25）；
//   - 文件落 %APPDATA%\AIPDF\reading-state\{hash}.json（每文件一个，UTF-8 无 BOM）；
//   - 存：最后页码、缩放、左栏面板选择、书签列表（Q3：书签并入每文件状态，不另设 bookmarks.json）；
//   - 恢复：按当前 size/mtime 计算键 → 命中即恢复；未命中从第 1 页；损坏静默重建（AC-24.2/AC-25.1）；
//   - 单一真相源（M-6/BA-6）：reading-state 为页码/缩放/面板的 SoT，最近记录仅作无状态时的兜底；
//   - 保存时机（FR-24）：仅在标签关闭 / 应用退出时批量写，滚动/缩放期间不写盘；
//   - 孤儿 GC（FR-27）：>90 天未访问删除、超 100 条淘汰最旧；lastAccessUtc 由应用显式写入（M-7，不依赖 atime）；
//   - 键迁移（BLK-1）：MigrateAsync 先写新键成功再删旧键，崩溃时旧键仍可回退。
// 红线：本文件不含任何密码字段（对照 WorkflowPresetStore：密码绝不落盘）。
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIPDF.Windows.Services;

/// <summary>单条用户书签（并入每文件阅读状态，Q3）。PageIndex 为 0 基。</summary>
public sealed class ReadingStateBookmark
{
    public int PageIndex { get; set; }

    /// <summary>加入时间（显示辅助与稳定排序）。</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 每文件阅读状态文档（schema 沿袭 WorkflowPresetStore 模式；无密码字段）。
/// 字段与 PRD FR-24 一一对应：页码 / 缩放 / 左栏面板 / 书签 / 显式最后访问时间。
/// </summary>
public sealed class ReadingState
{
    public const int CurrentSchema = 1;

    /// <summary>默认左栏面板键（与 ReaderPanelState.Thumbs 一致；此处用常量避免 Services→ViewModels 依赖）。</summary>
    public const string PanelThumbs = "thumbs";

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>当时打开的规范化绝对路径（诊断/冲突检测辅助；键计算不读此字段）。</summary>
    public string Path { get; set; } = "";

    /// <summary>最后阅读页（1 基）。</summary>
    public int LastPage { get; set; } = 1;

    /// <summary>缩放百分比（100 = 100%）。</summary>
    public double LastZoom { get; set; } = 100;

    /// <summary>左栏面板选择（thumbs / outline / bookmarks / search）。</summary>
    public string PanelKey { get; set; } = PanelThumbs;

    /// <summary>用户自建书签列表。</summary>
    public List<ReadingStateBookmark> Bookmarks { get; set; } = [];

    /// <summary>显式最后访问时间（UTC）。GC 以此判定 90 天，不依赖文件系统 atime（M-7）。</summary>
    public DateTime LastAccessUtc { get; set; }
}

/// <summary>
/// 恢复决策结果（纯数据，供宿主套用到 ReaderViewModel / 面板）。
/// <see cref="FromReadingState"/> 为 false 表示来自最近记录兜底（无状态文件时的投影），不显示 ST-12。
/// </summary>
public sealed record ReadingStateRestore(
    int LastPage,
    double LastZoom,
    string PanelKey,
    IReadOnlyList<ReadingStateBookmark> Bookmarks,
    bool FromReadingState,
    DateTime LastAccessUtc);

/// <summary>孤儿 GC 结果（可断言）。</summary>
public sealed record ReadingStateGcResult(int RemovedExpired, int RemovedOverflow, int Remaining)
{
    public int RemovedTotal => RemovedExpired + RemovedOverflow;
}

/// <summary>
/// 阅读状态存储。同 <see cref="WorkflowPresetStore"/> 范式：schema 版本 / UTF-8 无 BOM /
/// 文件名消毒 / 测试可注入目录与时钟；并实现 <see cref="IReadingStateMigrator"/> 供 T4 保存后迁移键。
/// </summary>
public sealed class ReadingStateStore : IReadingStateMigrator
{
    /// <summary>状态条目总数上限【FR-27】；超出淘汰最旧（按 lastAccessUtc）。</summary>
    public const int MaxEntries = 100;

    /// <summary>孤儿判定阈值【FR-27】：超过该时长未访问即删除。</summary>
    public static readonly TimeSpan OrphanTtl = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true, // 容忍手工编辑/历史版本的大小写差异
        // 中文路径按原字符写入（本地状态文件保持可读），不影响解析往返
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    /// <summary>测试注入临时目录与确定性 UTC 时钟；生产使用 %APPDATA%\AIPDF\reading-state\。</summary>
    public ReadingStateStore(string? directory = null, Func<DateTime>? utcNow = null)
    {
        StoreDirectory = directory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIPDF", "reading-state");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string StoreDirectory { get; }

    /// <summary>键 → 状态文件全路径。键为 64 位十六进制，仍做文件名消毒防御。</summary>
    public string FilePathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(StoreDirectory, SafeFileName(key) + ".json");
    }

    // ------------------------------------------------------------------ 基础读写

    /// <summary>按键读取状态；缺文件 / JSON 损坏 / schema 不识别 → null（调用方静默重建，AC-24.2）。</summary>
    public ReadingState? Load(string key)
    {
        lock (_gate)
        {
            return LoadCore(key);
        }
    }

    /// <summary>按键写入状态（UTF-8 无 BOM，临时文件原子替换）。</summary>
    public void Save(string key, ReadingState state)
    {
        lock (_gate)
        {
            SaveCore(key, state);
        }
    }

    /// <summary>按键删除状态文件；不存在返回 false。</summary>
    public bool Delete(string key)
    {
        lock (_gate)
        {
            return DeleteCore(key);
        }
    }

    /// <summary>列出目录中全部状态键（不解析内容，供诊断/测试）。</summary>
    public IReadOnlyList<string> ListKeys()
    {
        lock (_gate)
        {
            if (!Directory.Exists(StoreDirectory)) return [];
            return [.. Directory.GetFiles(StoreDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)];
        }
    }

    // ------------------------------------------------------------------ 恢复 / 兜底

    /// <summary>
    /// 恢复入口：按当前 size/mtime 计算键 → 命中则加载、显式刷新 <c>lastAccessUtc</c>（M-7）并返回；
    /// 未命中返回 null（调用方从第 1 页，AC-25.1/25.2）。
    /// </summary>
    public ReadingState? TryRestore(string path)
    {
        var stamp = ReadingStateKey.TryCapture(path);
        if (stamp is null) return null;

        lock (_gate)
        {
            var state = LoadCore(stamp.Value.Key);
            if (state is null) return null;

            state.Schema = ReadingState.CurrentSchema;
            state.LastAccessUtc = _utcNow();
            SaveCore(stamp.Value.Key, state); // 打开时显式写最后访问（不依赖 FS atime）
            return state;
        }
    }

    /// <summary>
    /// 单一真相源决策（M-6/BA-6）：
    ///   ① reading-state 命中 → 以它为页码/缩放/面板/书签的 SoT（<c>FromReadingState=true</c>）；
    ///   ② 未命中 → 仅当最近记录镜像与当前文件一致（Bytes == 当前 size 且记录过 LastPage）时兜底，
    ///      文件被替换（size 变）则返回 null，从第 1 页（AC-25.1）。
    /// </summary>
    public ReadingStateRestore? ResolveRestore(string path, RecentFileEntry? recentEntry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var state = TryRestore(path);
        if (state is not null)
        {
            return new ReadingStateRestore(
                NormalizePage(state.LastPage),
                NormalizeZoom(state.LastZoom),
                NormalizePanelKey(state.PanelKey),
                state.Bookmarks,
                FromReadingState: true,
                state.LastAccessUtc);
        }

        var stamp = ReadingStateKey.TryCapture(path);
        if (recentEntry is null || stamp is not { } current) return null;
        if (recentEntry.Bytes > 0 && recentEntry.Bytes == current.Size && recentEntry.LastPage > 0)
        {
            return new ReadingStateRestore(
                NormalizePage(recentEntry.LastPage),
                NormalizeZoom(recentEntry.LastZoom),
                ReadingState.PanelThumbs,
                [],
                FromReadingState: false,
                default);
        }

        return null;
    }

    // ------------------------------------------------------------------ 键迁移（BLK-1）

    /// <summary>
    /// 把 <paramref name="oldKey"/> 的状态（含书签）迁移到 <paramref name="newKey"/>，然后删除旧键。
    /// 原子性：先写新键成功再删旧键——崩溃时旧键仍可回退。旧键不存在则 no-op。
    /// </summary>
    public Task MigrateAsync(string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return Task.CompletedTask;
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return Task.CompletedTask;

        lock (_gate)
        {
            var state = LoadCore(oldKey);
            if (state is null) return Task.CompletedTask; // 无状态可迁

            SaveCore(newKey, state); // ① 先写新键成功
            DeleteCore(oldKey);      // ② 再删旧键（崩溃在此前 → 旧键仍可读）
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ 孤儿 GC（FR-27）

    /// <summary>
    /// 启动时扫描一次：删除超过 90 天未访问的状态；再按 lastAccessUtc 淘汰最旧至总数 ≤ 100。
    /// <paramref name="preserveKeys"/>（当前打开文件）既不被 TTL 删除也不参与上限淘汰。
    /// 损坏/无法解析的文件跳过（不误删），静默执行。
    /// </summary>
    public ReadingStateGcResult GarbageCollect(IReadOnlyCollection<string>? preserveKeys = null)
    {
        lock (_gate)
        {
            if (!Directory.Exists(StoreDirectory)) return new ReadingStateGcResult(0, 0, 0);

            var preserve = preserveKeys is null
                ? null
                : new HashSet<string>(preserveKeys, StringComparer.Ordinal);
            var now = _utcNow();
            var preserved = new List<string>();
            var evictable = new List<(string File, DateTime LastAccess, int Order)>();
            var removedExpired = 0;
            var order = 0;

            foreach (var file in Directory.GetFiles(StoreDirectory, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                var state = LoadCore(key);
                if (state is null) continue; // 损坏：跳过，不误删

                if (preserve is not null && preserve.Contains(key))
                {
                    preserved.Add(file);
                    continue;
                }

                var lastAccess = state.LastAccessUtc;
                if (now - lastAccess > OrphanTtl)
                {
                    if (TryDeleteFile(file)) removedExpired++;
                    continue;
                }

                evictable.Add((file, lastAccess, order++));
            }

            // 上限淘汰：总数（保留 + 可淘汰）> 100 时删最旧的可淘汰项
            var capacity = Math.Max(0, MaxEntries - preserved.Count);
            var removedOverflow = 0;
            if (evictable.Count > capacity)
            {
                var ordered = evictable
                    .OrderBy(e => e.LastAccess)
                    .ThenBy(e => e.Order) // 时间戳并列时以枚举序保证确定性
                    .ToList();
                var overflow = ordered.Count - capacity;
                for (var i = 0; i < overflow; i++)
                {
                    if (TryDeleteFile(ordered[i].File)) removedOverflow++;
                }
            }

            var remaining = preserved.Count + evictable.Count - removedOverflow;
            return new ReadingStateGcResult(removedExpired, removedOverflow, remaining);
        }
    }

    // ------------------------------------------------------------------ 内部实现

    private ReadingState? LoadCore(string key)
    {
        try
        {
            var path = FilePathFor(key);
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..]; // 容忍手工编辑留下的 BOM
            }

            var state = JsonSerializer.Deserialize<ReadingState>(bytes, JsonOptions);
            return state is null || state.Schema != ReadingState.CurrentSchema ? null : state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void SaveCore(string key, ReadingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Schema = ReadingState.CurrentSchema;

        Directory.CreateDirectory(StoreDirectory);
        var target = FilePathFor(key);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions), Utf8NoBom);
        try
        {
            File.Move(temp, target, overwrite: true); // 原子替换（崩溃不产生半截目标文件）
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private bool DeleteCore(string key)
    {
        try
        {
            var path = FilePathFor(key);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int NormalizePage(int page) => page < 1 ? 1 : page;

    private static double NormalizeZoom(double zoom) => zoom <= 0 ? 100 : zoom;

    private static string NormalizePanelKey(string? key) => key switch
    {
        "thumbs" or "outline" or "bookmarks" or "search" => key,
        _ => ReadingState.PanelThumbs,
    };

    /// <summary>文件名消毒（沿袭 WorkflowPresetStore.SafeFileName）：非法字符替换为 “-”，去首尾空白。</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var trimmed = (name ?? "").Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}
