// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T9a：批注读回服务——调用引擎 `list_annotations` 把文件内注释读成客户端模型。
//
// 读回是**只读** operation：引擎侧走独立 dispatch 分支（绝不可放进 ANNOTATION_OPERATIONS，
// 那会走 save_annotations 的就地写回并覆盖源文件）。读回结果**不进脏记录集合**，
// 否则保存时会被当成新增批注重复写回。
//
// 名字语义（S2 尖刺实测，.omo/evidence/v4-spike-annotation-readback.md）：
//   - `privateName` = PyMuPDF 私有 `/Name`，即本引擎 `set_name()` 真正写入的键（跨会话可读）；
//   - `name` = 标准 `/NM`（其它阅读器看得到，但本引擎写的是 auto 名）。
//   本服务以 `privateName` 优先作为客户端 Id，保证与引擎的稳定名一致。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Annotations;

/// <summary>从 PDF 读回批注（只读）。</summary>
public sealed class AnnotationReadbackService
{
    private readonly EngineBridge _bridge;

    public AnnotationReadbackService(EngineBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    /// <summary>
    /// 读回文档内批注。<paramref name="pages"/> 为空表示全篇；大文档建议分页惰性读。
    /// 读回失败（引擎不可用/文件损坏）返回空列表——**不阻断阅读**。
    /// </summary>
    public async Task<IReadOnlyList<AnnotationRecord>> ReadAsync(
        string path,
        string? password = null,
        IReadOnlyList<int>? pages = null,
        CancellationToken cancellationToken = default)
    {
        var request = new EngineRequest
        {
            Operation = "list_annotations",
            Files = [path],
        };
        if (!string.IsNullOrEmpty(password))
        {
            request.Options["password"] = password;
        }

        if (pages is { Count: > 0 })
        {
            request.Options["pages"] = pages;
        }

        EngineResult result;
        try
        {
            result = await _bridge.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];   // 读回失败不阻断阅读（与搜索/缩略图同一取向）
        }

        if (!result.Ok
            || !result.Extra.TryGetValue("annotations", out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var records = new List<AnnotationRecord>();

        // V4 T10：先建 xref → 稳定名 映射——`/IRT` 存的是**父注释的 xref**（形如 "7 0 R"），
        // 需要借此还原父批注 Id，才能把回复挂回它的线程。
        var nameByXref = new Dictionary<int, string>();
        foreach (var row in element.EnumerateArray())
        {
            var name = ReadString(row, "privateName") ?? ReadString(row, "name");
            if (name is not null && ReadInt(row, "xref") is { } xref)
            {
                nameByXref[xref] = name;
            }
        }

        foreach (var row in element.EnumerateArray())
        {
            if (ToRecord(row, nameByXref) is { } record)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>
    /// 引擎行 → 客户端记录。不支持的 subtype 返回 null（跳过，不阻断其它注释读回）。
    /// 目前覆盖 Highlight / Underline / StrikeOut / FreeText：它们是本应用会写入、
    /// 也是属性面板与汇总导出最需要的类型；Ink / 形状 / 图章的几何还原留待后续。
    /// 带 <c>/IRT</c> 的 FreeText 还原为**回复**（见 <see cref="AnnotationRecord.ParentId"/>）。
    /// </summary>
    internal static AnnotationRecord? ToRecord(JsonElement row)
        => ToRecord(row, new Dictionary<int, string>());

    internal static AnnotationRecord? ToRecord(JsonElement row, IReadOnlyDictionary<int, string> nameByXref)
    {
        var subtype = ReadString(row, "subtype");
        var kind = subtype switch
        {
            "/Highlight" => AnnotationKind.Highlight,
            "/Underline" => AnnotationKind.Underline,
            "/StrikeOut" => AnnotationKind.Strikeout,
            "/FreeText" => AnnotationKind.Note,
            _ => (AnnotationKind?)null,
        };
        if (kind is null || ReadRect(row) is not { } rect)
        {
            return null;
        }

        var page = ReadInt(row, "page") ?? 0;
        var color = ToHexColor(ReadString(row, "color"));
        // Id 用引擎稳定名（私有 /Name）优先；退化到标准 /NM，再退化到 xref
        var id = ReadString(row, "privateName")
                 ?? ReadString(row, "name")
                 ?? (ReadInt(row, "xref") is { } xref ? $"xref-{xref}" : null);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        AnnotationRecord record;
        if (kind == AnnotationKind.Note)
        {
            var text = ReadString(row, "content");
            var content = string.IsNullOrWhiteSpace(text) ? "(无内容)" : text;
            // V4 T10：带 /IRT 的 FreeText = 回复（还原父 Id，供线程展示）
            record = ResolveParentId(row, nameByXref) is { } parentId
                ? AnnotationRecord.CreateReply(page, rect, parentId, content, color)
                : AnnotationRecord.CreateNote(page, rect, content, color);
        }
        else
        {
            record = AnnotationRecord.CreateTextMarkup(kind.Value, page, [rect], color);
        }

        return record with { Id = id };
    }

    /// <summary>
    /// 解析 <c>/IRT</c>（形如 <c>"7 0 R"</c>）为父批注的稳定名；无法解析返回 null
    /// （父注释不在本次读回范围内时退化为普通笔记，不丢内容）。
    /// </summary>
    private static string? ResolveParentId(JsonElement row, IReadOnlyDictionary<int, string> nameByXref)
    {
        var irt = ReadString(row, "irt");
        if (string.IsNullOrWhiteSpace(irt))
        {
            return null;
        }

        var token = irt.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return token.Length > 0
               && int.TryParse(token[0], out var xref)
               && nameByXref.TryGetValue(xref, out var name)
            ? name
            : null;
    }

    /// <summary>PDF <c>/C</c> 数组（形如 <c>[1 0 0]</c>）→ <c>#RRGGBB</c>；无法解析返回 null（用默认色）。</summary>
    internal static string? ToHexColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Trim().Trim('[', ']').Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var channels = new int[3];
        for (var index = 0; index < 3; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            channels[index] = Math.Clamp((int)Math.Round(value * 255), 0, 255);
        }

        return $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
    }

    private static string? ReadString(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object
           && row.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object
           && row.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static PdfRect? ReadRect(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("rect", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var numbers = new List<double>(4);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            numbers.Add(item.GetDouble());
        }

        return numbers.Count == 4
            ? new PdfRect(numbers[0], numbers[1], numbers[2], numbers[3])
            : null;
    }
}
