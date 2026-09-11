// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIPDF.Windows.Services;

/// <summary>
/// 最近文件单条记录。V1 降级点①（计划 T6）：仅含路径/文件名/打开时间，
/// 无进度元信息。V2 挂点：补充页数、体积、上次阅读进度与缩放，
/// 供起始页卡片「恢复到上次页码与缩放」（PRD 3.10 FR-33 ST-01/ST-02）。
/// 本记录不含任何密码或敏感参数字段（对照 WorkflowPresetStore：密码绝不落盘）。
/// </summary>
public sealed class RecentFileEntry
{
    /// <summary>规范化全路径（去重键，Windows 下大小写不敏感比较）。</summary>
    public string Path { get; set; } = "";

    /// <summary>文件名（卡片显示用；保存时随路径写入，读取时不重算以容忍文件已改名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>最近一次打开时间（本地时间，ISO 8601 往返格式序列化）。</summary>
    public DateTime LastOpenedAt { get; set; }

    // ---- V2 进度元信息（T4 保存成功后同步；T5 起始页卡片消费） ----

    /// <summary>文档页数（未知为 0）。</summary>
    public int PageCount { get; set; }

    /// <summary>文件字节数（未知为 0；就地保存成功后由 T4 同步为新 size）。</summary>
    public long Bytes { get; set; }

    /// <summary>上次阅读页（1 基；0 = 未知）。就地保存成功后由 T4 同步。</summary>
    public int LastPage { get; set; }

    /// <summary>上次缩放百分比（0 = 未知）。就地保存成功后由 T4 同步。</summary>
    public double LastZoom { get; set; }
}

/// <summary>recent-files.json 文档根。schema 版本沿袭 WorkflowPresetStore 模式（当前 1）。</summary>
public sealed class RecentFilesDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>按最近打开时间倒序（头部最新）。上限由 RecentFilesStore.MaxEntries 保证。</summary>
    public List<RecentFileEntry> Items { get; set; } = [];
}

/// <summary>
/// 最近文件存储：本机用户目录 %APPDATA%\AIPDF\recent-files.json（不入仓库、不上传）。
/// 沿袭 Tools/WorkflowViewModels.cs 的 WorkflowPresetStore 范式：
/// schema 版本字段 / UTF-8 无 BOM / UnsafeRelaxedJsonEscaping（中文按原字符可读）/ 测试可注入目录。
/// 与预设存储的差异：最近列表可随时重建，读取失败（缺文件/损坏/版本不符）一律返回空文档而非抛出。
/// </summary>
public sealed class RecentFilesStore
{
    /// <summary>最近列表上限【D15】；超出淘汰最旧。</summary>
    public const int MaxEntries = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文路径按原字符写入（本地记录文件保持可读），不影响解析往返
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>测试注入临时目录；生产使用 %APPDATA%\AIPDF\。</summary>
    public RecentFilesStore(string? directory = null)
        => StoreDirectory = directory
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIPDF");

    public string StoreDirectory { get; }

    public string FilePath => Path.Combine(StoreDirectory, "recent-files.json");

    /// <summary>
    /// 读取最近文件文档。文件不存在 / JSON 损坏 / schema 版本不识别 → 返回空文档（不抛出、不删除原文件）。
    /// </summary>
    public RecentFilesDocument Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new RecentFilesDocument();
            }

            var bytes = File.ReadAllBytes(FilePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..]; // 容忍手工编辑留下的 BOM
            }

            var document = JsonSerializer.Deserialize<RecentFilesDocument>(bytes, JsonOptions);
            if (document is null || document.Schema != RecentFilesDocument.CurrentSchema)
            {
                return new RecentFilesDocument();
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RecentFilesDocument();
        }
    }

    /// <summary>写入最近文件文档：目录不存在先建；UTF-8 无 BOM（沿袭预设存储）。</summary>
    public void Save(RecentFilesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(StoreDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
