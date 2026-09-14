// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T1 应用设置存储（纯逻辑，无 UI）：
//   - 落 %APPDATA%\AIPDF\settings.json（与 reading-state/、session.json 同级目录，PRD 4.5.2 / FR-39）；
//   - 沿袭 SessionStore 持久化范式：schema 版本 / UTF-8 无 BOM / 临时文件→原子替换 /
//     缺文件·损坏·不可读→默认值（AC-39.2：不崩、不提示）/ 测试可注入文件路径；
//   - 7 项设置（FR-38 三项 net-new + 裁决 Q6 四项）在 Load 与 Save 双向归一化 / 钳制；
//   - v1.1 翻译：schema 升到 2，新增 translationConsents（只存 provider+endpoint+密钥指纹+时间+TTL，
//     绝不存密钥 / 正文）；Load 接受旧 schema（1..Current），缺失列表即空 = 全部未同意（决策 #14/#15/#21）；
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
/// 应用设置文档（schema 沿袭 SessionStore；无密码 / 密钥字段）。
/// 字段与 PRD FR-38 / 裁决 Q6 的 7 项设置一一对应；另含 v1.1 云端翻译同意记录 <see cref="TranslationConsents"/>。
/// 属性缺省值即规范化后的默认值。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 当前 schema 版本；不识别时整体回退默认（FR-39）。
    /// v1 → v2（v1.1 翻译）：仅新增 <see cref="TranslationConsents"/>，缺失即空列表，故 Load 兼容旧 schema。
    /// </summary>
    public const int CurrentSchema = 2;

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

    // ------------------------------------------------------------------ v1.1 云端翻译同意（决策 #14/#21）

    /// <summary>
    /// 云端翻译同意记录（每项 = provider + endpoint origin + 密钥指纹 + 授予时间 + TTL）。
    /// <b>绝不存密钥本体与文档正文</b>；密钥交 <see cref="TranslationCredentialStore"/>（系统凭据管理器）。
    /// 缺省空列表 = 全部未同意（决策 #15：同意默认未勾选）。
    /// </summary>
    [JsonPropertyName("translationConsents")]
    public List<TranslationConsent> TranslationConsents { get; set; } = [];

    // ------------------------------------------------------------------ v1.1 翻译默认项与云端连接（T23）

    /// <summary>默认源语言「自动检测」。</summary>
    public const string TranslationSourceLanguageDefault = "auto";

    /// <summary>默认目标语言（中文，必须显式选择）。</summary>
    public const string TranslationTargetLanguageDefault = "zh";

    /// <summary>默认 Prompt 模板（学术）。</summary>
    public const string TranslationTemplateDefault = "academic";

    /// <summary>云端成本硬上限默认 token 数（与 TranslationConfigViewModel.DefaultCostLimitTokens 一致）。</summary>
    public const long TranslationCostLimitTokensDefault = 2_000_000;

    /// <summary>成本上限下限。</summary>
    public const long TranslationCostLimitTokensMin = 1;

    /// <summary>成本上限上限（防手工编辑注入离谱值）。</summary>
    public const long TranslationCostLimitTokensMax = 1_000_000_000;

    /// <summary>源语言允许标识（含「自动检测」）。与 <c>TranslationConfigViewModel.SourceLanguageOptions</c> 保持一致。</summary>
    public static readonly string[] TranslationSourceLanguageCodes =
        ["auto", "zh", "en", "ja", "ko", "fr", "de", "ru", "es", "pt", "it"];

    /// <summary>目标语言允许标识（不含「自动检测」）。与 <c>TranslationConfigViewModel.TargetLanguageOptions</c> 保持一致。</summary>
    public static readonly string[] TranslationTargetLanguageCodes =
        ["zh", "en", "ja", "ko", "fr", "de", "ru", "es", "pt", "it"];

    /// <summary>Prompt 模板允许标识（5 类预置 + 自定义）。与 <c>TranslationConfigViewModel.TemplateOptions</c> 保持一致。</summary>
    public static readonly string[] TranslationTemplateIds =
        ["academic", "legal", "medical", "technical", "general", "custom"];

    /// <summary>
    /// 云端翻译连接配置列表（provider + endpoint + model）。<b>绝不存密钥</b>；
    /// 密钥交 <see cref="TranslationCredentialStore"/>（系统凭据管理器）。缺省空列表 = 尚未配置云端。
    /// </summary>
    [JsonPropertyName("translationProviders")]
    public List<TranslationProviderConfig> TranslationProviders { get; set; } = [];

    /// <summary>默认源语言（<c>auto</c> 或语言码）；未知值回退 <c>auto</c>。</summary>
    [JsonPropertyName("translationDefaultSourceLanguage")]
    public string TranslationDefaultSourceLanguage { get; set; } = TranslationSourceLanguageDefault;

    /// <summary>默认目标语言（语言码，不含 auto）；未知值回退 <c>zh</c>。</summary>
    [JsonPropertyName("translationDefaultTargetLanguage")]
    public string TranslationDefaultTargetLanguage { get; set; } = TranslationTargetLanguageDefault;

    /// <summary>默认 Prompt 模板标识；未知值回退 <c>academic</c>。</summary>
    [JsonPropertyName("translationTemplateId")]
    public string TranslationTemplateId { get; set; } = TranslationTemplateDefault;

    /// <summary>自定义 Prompt 正文（模板为 <c>custom</c> 时使用）；缺省空串。</summary>
    [JsonPropertyName("translationCustomPrompt")]
    public string TranslationCustomPrompt { get; set; } = "";

    /// <summary>术语表 CSV 路径；空串 = 不使用术语表。</summary>
    [JsonPropertyName("translationGlossaryPath")]
    public string TranslationGlossaryPath { get; set; } = "";

    /// <summary>是否启用云端成本硬上限（缺省开）。</summary>
    [JsonPropertyName("translationCostLimitEnabled")]
    public bool TranslationCostLimitEnabled { get; set; } = true;

    /// <summary>成本硬上限 token 数，钳制到 [1, 1e9]；非正数回退默认 2.0M。</summary>
    [JsonPropertyName("translationCostLimitTokens")]
    public long TranslationCostLimitTokens { get; set; } = TranslationCostLimitTokensDefault;

    /// <summary>
    /// 产物 PDF 页脚是否写「机器翻译 · 请核对」标记（缺省开，§9.3）。一般文档可关；
    /// 法律 / 医学高风险模板**强制写标记、忽略此设置**，由 backend <c>output.add_marker(high_risk=True)</c> 兜底。
    /// </summary>
    [JsonPropertyName("translationMarkerEnabled")]
    public bool TranslationMarkerEnabled { get; set; } = true;
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
                // schema 迁移：接受 [1, CurrentSchema]（v1→v2 仅追加 translationConsents，缺失即空列表）；
                // 未识别（未来版本 / 0 / 负值）整体回退默认（FR-39）。
                if (state is null || state.Schema < 1 || state.Schema > AppSettings.CurrentSchema) return new AppSettings();

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

    /// <summary>
    /// 设置归一化：schema 收敛 + 7 项字段各自回退/钳制 + 同意记录清洗（Load 与 Save 双向一致）。
    /// </summary>
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
        settings.TranslationConsents = NormalizeConsents(settings.TranslationConsents);
        settings.TranslationProviders = NormalizeTranslationProviders(settings.TranslationProviders);
        settings.TranslationDefaultSourceLanguage = NormalizeTranslationLanguage(
            settings.TranslationDefaultSourceLanguage, AppSettings.TranslationSourceLanguageDefault, AppSettings.TranslationSourceLanguageCodes);
        settings.TranslationDefaultTargetLanguage = NormalizeTranslationLanguage(
            settings.TranslationDefaultTargetLanguage, AppSettings.TranslationTargetLanguageDefault, AppSettings.TranslationTargetLanguageCodes);
        settings.TranslationTemplateId = NormalizeTranslationLanguage(
            settings.TranslationTemplateId, AppSettings.TranslationTemplateDefault, AppSettings.TranslationTemplateIds);
        settings.TranslationCustomPrompt = settings.TranslationCustomPrompt?.Trim() ?? "";
        settings.TranslationGlossaryPath = NormalizeTranslationGlossaryPath(settings.TranslationGlossaryPath);
        settings.TranslationCostLimitTokens = NormalizeTranslationCostLimitTokens(settings.TranslationCostLimitTokens);
        return settings;
    }

    /// <summary>
    /// 同意记录归一化：丢弃 provider / endpoint / 指纹缺项的损坏记录；endpoint 归一为 origin；
    /// TTL 钳制；同一目的地保留最近授予的一条（防手工编辑产生重复导致行为歧义）。
    /// </summary>
    internal static List<TranslationConsent> NormalizeConsents(List<TranslationConsent>? consents)
    {
        var result = new List<TranslationConsent>();
        if (consents is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var consent in consents.OrderByDescending(c => c?.GrantedAt ?? DateTimeOffset.MinValue))
        {
            if (consent is null) continue;

            var provider = consent.Provider?.Trim() ?? "";
            var endpoint = TranslationConsent.NormalizeEndpoint(consent.Endpoint);
            var fingerprint = consent.KeyFingerprint?.Trim() ?? "";
            if (provider.Length == 0 || endpoint.Length == 0 || fingerprint.Length == 0) continue;

            var destination = provider + "\n" + endpoint;
            if (!seen.Add(destination)) continue;

            consent.Provider = provider;
            consent.Endpoint = endpoint;
            consent.KeyFingerprint = fingerprint;
            consent.TtlDays = TranslationConsent.NormalizeTtlDays(consent.TtlDays);
            result.Add(consent);
        }

        return result;
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

    // ------------------------------------------------------------------ v1.1 翻译归一化（Load 与 Save 共用）

    /// <summary>语言 / 模板标识归一化：允许集内保留规范形式（大小写不敏感），否则回退 <paramref name="fallback"/>。</summary>
    internal static string NormalizeTranslationLanguage(string? code, string fallback, IReadOnlyList<string> allowed)
    {
        var value = code?.Trim();
        if (string.IsNullOrEmpty(value)) return fallback;

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        return fallback;
    }

    /// <summary>术语表路径归一化：空白 → 空串；其余仅去首尾空白（存在性与扩展名由翻译配置消费方校验）。</summary>
    internal static string NormalizeTranslationGlossaryPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "" : path.Trim();

    /// <summary>成本上限归一化：非正数回退默认 2.0M，正数钳制到 [1, 1e9]。</summary>
    internal static long NormalizeTranslationCostLimitTokens(long tokens)
        => tokens <= 0
            ? AppSettings.TranslationCostLimitTokensDefault
            : Math.Clamp(tokens, AppSettings.TranslationCostLimitTokensMin, AppSettings.TranslationCostLimitTokensMax);

    /// <summary>
    /// 云端连接配置归一化：仅丢弃 provider / endpoint 缺项的「明确无效」条目；
    /// 端点校验失败的条目【保留】并写入 <see cref="TranslationProviderConfig.ValidationError"/>
    /// （不再静默丢配置，R5-3），由运行时（<c>IsUsable</c>）跳过、由设置界面提示用户修正；
    /// 同一 provider + endpoint origin 只保留一条（可用条目优先，避免失效旧条目遮蔽已修正条目）。
    /// </summary>
    internal static List<TranslationProviderConfig> NormalizeTranslationProviders(List<TranslationProviderConfig>? providers)
    {
        var result = new List<TranslationProviderConfig>();
        if (providers is null) return result;

        var cleaned = new List<TranslationProviderConfig>();
        foreach (var provider in providers)
        {
            if (provider is null) continue;

            var name = provider.Provider?.Trim() ?? "";
            var endpoint = provider.Endpoint?.Trim() ?? "";
            var model = provider.Model?.Trim() ?? "";
            if (name.Length == 0 || endpoint.Length == 0) continue;

            provider.Provider = name;
            provider.Endpoint = endpoint;
            provider.Model = model;
            provider.ValidationError = TranslationProviderConfig.ValidateEndpoint(endpoint);
            cleaned.Add(provider);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in cleaned.OrderByDescending(p => p.IsUsable))
        {
            var destination = provider.Provider + "\n" + TranslationConsent.NormalizeEndpoint(provider.Endpoint);
            if (!seen.Add(destination)) continue;

            result.Add(provider);
        }

        return result;
    }
}
