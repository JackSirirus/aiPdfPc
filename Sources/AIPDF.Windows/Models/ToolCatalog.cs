// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIPDF.Windows.Models;

/// <summary>工具目录中的一个参数字段，与 backend/catalog.py 的 field() 输出对应。</summary>
public sealed class ToolField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Kind { get; set; } = "text";
    public List<string> Choices { get; set; } = [];
    public string Help { get; set; } = "";
}

/// <summary>工具目录中的一个工具条目，来源是 backend/catalog.json（不写死在代码里）。</summary>
public sealed class Tool
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<ToolField> Fields { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
    public bool Multiple { get; set; } = true;
    public string Note { get; set; } = "";
    public string Dependency { get; set; } = "";
}

/// <summary>侧栏使用的一个分组：同一 category 下按目录顺序排列的工具集合。</summary>
public sealed class ToolGroup
{
    public ToolGroup(string name, IReadOnlyList<Tool> tools)
    {
        Name = name;
        Tools = tools;
    }

    public string Name { get; }

    public IReadOnlyList<Tool> Tools { get; }
}

/// <summary>
/// 加载与分组工具目录。上游权威来源是 backend/catalog.py 生成的 backend/catalog.json；
/// 本类型只做读取，不维护第二份工具清单。
/// </summary>
public static class ToolCatalog
{
    public const string CatalogFileName = "catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static ToolCatalog()
    {
        // catalog.json 在 Windows 上可能被 catalog.py 以系统默认编码（GBK）重写；
        // CodePagesEncodingProvider 提供兼容 GBK 的 GB18030 解码，作为 UTF-8 之后的回退。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>从自动解析出的路径加载完整工具目录。</summary>
    public static IReadOnlyList<Tool> Load() => Load(ResolveCatalogPath());

    /// <summary>从指定路径加载完整工具目录。</summary>
    public static IReadOnlyList<Tool> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..]; // 去掉 UTF-8 BOM，避免 JSON 解析器读到 U+FEFF
        }

        var tools = JsonSerializer.Deserialize<List<Tool>>(Decode(bytes), JsonOptions)
                    ?? throw new InvalidDataException($"工具目录为空或格式不正确：{path}");
        return tools;
    }

    /// <summary>
    /// 按 category 分组，保持目录中各分组首次出现的顺序；
    /// 组名与组序完全来自数据，不在代码里写死。
    /// </summary>
    public static IReadOnlyList<ToolGroup> GroupByCategory(IEnumerable<Tool> tools)
    {
        // 用有序列表记录分组出现顺序（Dictionary 不保证枚举顺序）
        var order = new List<(string Category, List<Tool> Items)>();
        var index = new Dictionary<string, List<Tool>>();
        foreach (var tool in tools)
        {
            if (!index.TryGetValue(tool.Category, out var bucket))
            {
                bucket = [];
                index[tool.Category] = bucket;
                order.Add((tool.Category, bucket));
            }

            bucket.Add(tool);
        }

        return [.. order.Select(pair => new ToolGroup(pair.Category, pair.Items))];
    }

    /// <summary>优先 UTF-8 严格解码；失败时回退 GB18030（向下兼容 GBK）。</summary>
    internal static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    /// <summary>
    /// 解析 catalog.json 的位置：优先应用输出目录（含打包后的场景），
    /// 其次从输出目录向上查找源码树中的 backend/catalog.json。
    /// </summary>
    public static string ResolveCatalogPath()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, CatalogFileName), Path.Combine(baseDir, "Assets", CatalogFileName) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var upstream = Path.Combine(dir.FullName, "backend", CatalogFileName);
            if (File.Exists(upstream))
            {
                return upstream;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"未找到工具目录文件（{CatalogFileName}）：应用目录与源码树 backend/ 下均不存在。请重新构建项目。");
    }
}
