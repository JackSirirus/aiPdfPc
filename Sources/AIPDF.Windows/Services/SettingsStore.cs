// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T1 应用设置存储（纯逻辑，无 UI）：
//   - 落 %APPDATA%\AIPDF\settings.json（与 reading-state/、session.json 同级目录，PRD 4.5.2 / FR-39）；
//   - 沿袭 SessionStore 持久化范式：schema 版本 / UTF-8 无 BOM / 临时文件→原子替换 /
//     缺文件·损坏·不可读→默认值（AC-39.2：不崩、不提示）/ 测试可注入文件路径；
//   - 7 项设置（FR-38 三项 net-new + 裁决 Q6 四项）在 Load 与 Save 双向归一化 / 钳制；
//   - DefaultRenderDpi 仅覆盖渲染基准 DPI，不改「zoom ≥150% → HighDpi=150」阈值阶梯
//     （PageLayoutCalculator.DpiForZoom 语义不变，注入方式见 T5）；
//   - DefaultOutputDir 仅作工具 VM 的 OutputDir 初始种子，不改变引擎「输出任务目录 + 不覆盖源文件」语义。
// 红线：本文件不含任何密码字段（PRD 4.3.3）；密码只经 stdin 传递、绝不落盘。
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPDF.Windows.Services;

/// <summary>
/// 应用设置文档（schema 沿袭 SessionStore；无密码字段）。
/// 字段与 PRD FR-38 / 裁决 Q6 的 7 项设置一一对应；属性缺省值即规范化后的默认值。
/// </summary>
public sealed class AppSettings
{
    /// <summary>当前 schema 版本；不识别时整体回退默认（FR-39）。</summary>
    public const int CurrentSchema = 1;

    // ------------------------------------------------------------------ 默认缩放（FR-38 项 1）

    /// <summary>「适配宽度」档。</summary>
    public const string ZoomFitWidth = "fit-width";

    /// <summary>「适配页面」档。</summary>
    public const string ZoomFitPage = "fit-page";

    /// <summary>默认缩放 = 非适配档 100%（沿用旧行为，打开旧文档行为不变）。</summary>
    public const string ZoomDefault = "100";

    /// <summary>九档缩放阶梯（与 PageLayoutCalculator.LadderPercent 取值一致；此处复制常量避免 Services→Views 依赖）。</summary>
    public static readonly int[] ZoomLadderPercent = [25, 33, 50, 67, 100, 150, 200, 300, 400];

    // ------------------------------------------------------------------ 默认左栏面板（FR-38 项 2）

    /// <summary>缩略图面板（默认）。</summary>
    public const string PanelThumbs = "thumbs";

    /// <summary>目录面板。</summary>
    public const string PanelOutline = "outline";

    /// <summary>书签面板。</summary>
    public const string PanelBookmarks = "bookmarks";

    // ------------------------------------------------------------------ 最近文件上限（FR-38 项 3）

    /// <summary>最近文件列表默认上限。</summary>
    public const int RecentFilesLimitDefault = 20;

    /// <summary>最近文件列表下限。</summary>
    public const int RecentFilesLimitMin = 10;

    /// <summary>最近文件列表上限。</summary>
    public const int RecentFilesLimitMax = 50;

    // ------------------------------------------------------------------ 主题（Q6 / C2）

    /// <summary>亮色主题。</summary>
    public const string ThemeLight = "light";

    /// <summary>暗色主题。</summary>
    public const string ThemeDark = "dark";

    /// <summary>跟随系统主题（默认）。</summary>
    public const string ThemeSystem = "system";

    // ------------------------------------------------------------------ 打印 DPI（Q6 / Q11）

    /// <summary>打印渲染 DPI 默认值（另受 MaxTextureEdge=4096 约束）。</summary>
    public const int PrintDpiDefault = 150;

    /// <summary>打印渲染 DPI 下限。</summary>
    public const int PrintDpiMin = 72;

    /// <summary>打印渲染 DPI 上限。</summary>
    public const int PrintDpiMax = 600;

    // ------------------------------------------------------------------ 默认渲染 DPI（Q6）

    /// <summary>渲染基准 DPI 默认值（= PageLayoutCalculator.BaseDpi）。</summary>
    public const double RenderDpiDefault = 96.0;

    /// <summary>渲染基准 DPI 下限。</summary>
    public const double RenderDpiMin = 36.0;

    /// <summary>渲染基准 DPI 上限。</summary>
    public const double RenderDpiMax = 600.0;

    /// <summary>schema 版本（当前恒为 <see cref="CurrentSchema"/>）。</summary>
    [JsonPropertyName("schemaVersion")]
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>
    /// 默认缩放：<c>fit-width</c> / <c>fit-page</c> / 九档阶梯之一（规范化后的十进制字符串）。
    /// 缺省 <c>100</c>（非适配档，保证不改变旧行为）。
    /// </summary>
    [JsonPropertyName("defaultZoom")]
    public string DefaultZoom { get; set; } = ZoomDefault;

    /// <summary>默认左栏面板：<c>thumbs</c> / <c>outline</c> / <c>bookmarks</c>（不含 search）。缺省 <c>thumbs</c>。</summary>
    [JsonPropertyName("defaultPanelKey")]
    public string DefaultPanelKey { get; set; } = PanelThumbs;

    /// <summary>最近文件列表上限，钳制到 [10,50]。缺省 20。</summary>
    [JsonPropertyName("recentFilesLimit")]
    public int RecentFilesLimit { get; set; } = RecentFilesLimitDefault;

    /// <summary>主题：<c>light</c> / <c>dark</c> / <c>system</c>；未知值回退 <c>system</c>。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = ThemeSystem;

    /// <summary>打印渲染 DPI，钳制到 [72,600]。缺省 150。</summary>
    [JsonPropertyName("printDpi")]
    public int PrintDpi { get; set; } = PrintDpiDefault;

    /// <summary>默认输出目录；空串 = 沿用各工具页当前行为（OpenFolderDialog 选择）。</summary>
    [JsonPropertyName("defaultOutputDir")]
    public string DefaultOutputDir { get; set; } = "";

    /// <summary>默认渲染基准 DPI，钳制到 [36,600]；仅覆盖基准，不改 150% 高 DPI 阈值阶梯。缺省 96。</summary>
    [JsonPropertyName("defaultRenderDpi")]
    public double DefaultRenderDpi { get; set; } = RenderDpiDefault;
}

/// <summary>
/// 应用设置存储：同 <see cref="SessionStore"/> 范式（schema 版本 / UTF-8 无 BOM / 测试可注入路径）。
/// <see cref="Load"/> 永不返回 null——缺文件 / JSON 损坏 / schema 不识别 / 不可读一律回默认值（AC-39.2）。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true, // 容忍手工编辑/历史版本的大小写差异
        // 中文路径按原字符写入（本地状态文件保持可读），不影响解析往返
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();

    /// <summary>测试注入临时文件路径；生产为 %APPDATA%\AIPDF\settings.json。</summary>
    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIPDF", "settings.json");
    }

    public string FilePath { get; }

    /// <summary>
    /// 读取设置并做归一化；缺文件 / JSON 损坏 / schema 不识别 / 不可读 → 返回默认设置（AC-39.2：不崩、不提示）。
    /// 缺失字段由属性缺省值兜底，越界字段在 <see cref="Normalize"/> 中钳制/回退。
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();

                var bytes = File.ReadAllBytes(FilePath);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bytes = bytes[3..]; // 容忍手工编辑留下的 BOM
                }

                var state = JsonSerializer.Deserialize<AppSettings>(bytes, JsonOptions);
                if (state is null || state.Schema != AppSettings.CurrentSchema) return new AppSettings();

                return Normalize(state);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new AppSettings();
            }
        }
    }

    /// <summary>
    /// 写入设置（先归一化，再 UTF-8 无 BOM 写临时文件后原子替换）。
    /// 崩溃时不会产生半截目标文件；目标目录不存在时自动创建。
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            var clean = Normalize(settings);

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

    /// <summary>删除设置文件并回到默认值；不存在返回 false（测试/重置用）。</summary>
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

    // ------------------------------------------------------------------ 归一化 / 钳制（Load 与 Save 共用）

    /// <summary>设置归一化：schema 收敛 + 7 项字段各自回退/钳制（Load 与 Save 双向一致）。</summary>
    internal static AppSettings Normalize(AppSettings settings)
    {
        settings.Schema = AppSettings.CurrentSchema;
        settings.DefaultZoom = NormalizeZoom(settings.DefaultZoom);
        settings.DefaultPanelKey = NormalizePanelKey(settings.DefaultPanelKey);
        settings.RecentFilesLimit = NormalizeRecentFilesLimit(settings.RecentFilesLimit);
        settings.Theme = NormalizeTheme(settings.Theme);
        settings.PrintDpi = NormalizePrintDpi(settings.PrintDpi);
        settings.DefaultOutputDir = NormalizeOutputDir(settings.DefaultOutputDir);
        settings.DefaultRenderDpi = NormalizeRenderDpi(settings.DefaultRenderDpi);
        return settings;
    }

    /// <summary>缩放归一化：适配两档保留，九档阶梯取规范十进制形式，其余（含未知）回退 100%。</summary>
    internal static string NormalizeZoom(string? zoom)
    {
        var value = zoom?.Trim();
        if (string.IsNullOrEmpty(value)) return AppSettings.ZoomDefault;
        if (value.Equals(AppSettings.ZoomFitWidth, StringComparison.OrdinalIgnoreCase)) return AppSettings.ZoomFitWidth;
        if (value.Equals(AppSettings.ZoomFitPage, StringComparison.OrdinalIgnoreCase)) return AppSettings.ZoomFitPage;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
            && Array.IndexOf(AppSettings.ZoomLadderPercent, percent) >= 0)
        {
            return percent.ToString(CultureInfo.InvariantCulture);
        }

        return AppSettings.ZoomDefault;
    }

    /// <summary>面板键归一化：仅接受 thumbs/outline/bookmarks，其余（含 search）回退缩略图。</summary>
    internal static string NormalizePanelKey(string? key)
    {
        var value = key?.Trim();
        if (string.Equals(value, AppSettings.PanelThumbs, StringComparison.OrdinalIgnoreCase)) return AppSettings.PanelThumbs;
        if (string.Equals(value, AppSettings.PanelOutline, StringComparison.OrdinalIgnoreCase)) return AppSettings.PanelOutline;
        if (string.Equals(value, AppSettings.PanelBookmarks, StringComparison.OrdinalIgnoreCase)) return AppSettings.PanelBookmarks;
        return AppSettings.PanelThumbs;
    }

    /// <summary>主题归一化：仅接受 light/dark/system，未知值回退 system。</summary>
    internal static string NormalizeTheme(string? theme)
    {
        var value = theme?.Trim();
        if (string.Equals(value, AppSettings.ThemeLight, StringComparison.OrdinalIgnoreCase)) return AppSettings.ThemeLight;
        if (string.Equals(value, AppSettings.ThemeDark, StringComparison.OrdinalIgnoreCase)) return AppSettings.ThemeDark;
        return AppSettings.ThemeSystem;
    }

    /// <summary>最近文件上限归一化：一律钳制到 [10,50]。</summary>
    internal static int NormalizeRecentFilesLimit(int limit)
        => Math.Clamp(limit, AppSettings.RecentFilesLimitMin, AppSettings.RecentFilesLimitMax);

    /// <summary>打印 DPI 归一化：钳制到 [72,600]（另受 MaxTextureEdge=4096 约束）。</summary>
    internal static int NormalizePrintDpi(int dpi)
        => Math.Clamp(dpi, AppSettings.PrintDpiMin, AppSettings.PrintDpiMax);

    /// <summary>输出目录归一化：空白 → 空串；其余仅去首尾空白（存在性由消费方工具页校验，不改变引擎语义）。</summary>
    internal static string NormalizeOutputDir(string? directory)
        => string.IsNullOrWhiteSpace(directory) ? "" : directory.Trim();

    /// <summary>
    /// 渲染基准 DPI 归一化：非正数（未知/非法）回退 96，正数越界钳制到 [36,600]。
    /// 仅覆盖渲染基准，不参与 150% 高 DPI 阈值判定（PageLayoutCalculator.DpiForZoom 不受影响）。
    /// </summary>
    internal static double NormalizeRenderDpi(double dpi)
        => dpi <= 0
            ? AppSettings.RenderDpiDefault
            : Math.Clamp(dpi, AppSettings.RenderDpiMin, AppSettings.RenderDpiMax);
}
