// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T19 替换标注 sidecar 存储：把划词译文写入独立 sidecar 文件，在阅读视图叠加显示，
// 绝不写回源 PDF、绝不缓存原文全文。
//
// 关键约束（设计 §sidecar 标注文件）：
//   - 文件：源目录下 `.{源文件名}.transl_sidecar.json`，写后置 FILE_ATTRIBUTE_HIDDEN
//     （Windows 上点号前缀不等于隐藏，必须设置系统属性）；
//   - 回退：源目录不可写（CD-ROM / 只读 / 网络盘）时落到 %TEMP%\AIPDF-Sidecar\{hash}.json；
//   - 生命周期：仅会话内有效——应用退出 Clear() 删文件；下次打开同一文档不自动加载旧 sidecar；
//   - 内容：仅 {页码, 坐标, 译文, 语言对, 时间戳}，不存原文全文、不写日志/正文；
//   - 原子写：temp → rename，避免半成品被读取。
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.Services;

/// <summary>
/// 会话内 sidecar 存储（<see cref="ISelectionSidecarStore"/> 的真实实现）。
/// 线程安全：全部读写经实例锁；落盘在锁外完成 I/O，避免长 I/O 阻塞叠加层读取。
/// </summary>
public sealed class SelectionSidecarStore : ISelectionSidecarStore
{
    /// <summary>sidecar 文件名后缀（前置 <c>.</c> + 源文件名，见设计 §sidecar）。</summary>
    public const string FileSuffix = ".transl_sidecar.json";

    private const string TempFolderName = "AIPDF-Sidecar";
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _lock = new();
    private readonly Dictionary<string, DocumentSidecar> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _tempRoot;
    private readonly Func<string, bool> _isDirectoryWritable;
    private string? _activeDocument;

    /// <param name="tempRoot">只读回退目录；默认 <c>%TEMP%\AIPDF-Sidecar</c>（测试可注入）。</param>
    /// <param name="isDirectoryWritable">目录可写探测（默认真实探测；测试可注入以验证回退路径）。</param>
    public SelectionSidecarStore(string? tempRoot = null, Func<string, bool>? isDirectoryWritable = null)
    {
        _tempRoot = string.IsNullOrWhiteSpace(tempRoot)
            ? Path.Combine(Path.GetTempPath(), TempFolderName)
            : tempRoot!;
        _isDirectoryWritable = isDirectoryWritable ?? IsDirectoryWritable;
    }

    /// <inheritdoc />
    public string? ActiveDocument
    {
        get
        {
            lock (_lock) return _activeDocument;
        }
        set
        {
            string? normalized = null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    normalized = Path.GetFullPath(value);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    normalized = value; // 非法路径原样保留（Add 时再兜底）
                }
            }

            lock (_lock) _activeDocument = normalized;
        }
    }

    /// <inheritdoc />
    public SelectionSidecarResult Add(int pageNo, PdfRect rect, string translation, string langPair)
    {
        if (pageNo < 0)
        {
            return new SelectionSidecarResult(false, "页码无效，无法写入替换标注。");
        }

        if (string.IsNullOrWhiteSpace(translation))
        {
            return new SelectionSidecarResult(false, "译文为空，无法写入替换标注。");
        }

        DocumentSidecar document;
        lock (_lock)
        {
            if (_activeDocument is null)
            {
                return new SelectionSidecarResult(false, "当前没有打开的文档，无法写入替换标注。");
            }

            document = GetOrCreateLocked(_activeDocument);
            document.Entries.Add(new SelectionSidecarAnnotation(
                pageNo, Normalize(rect), translation, langPair ?? string.Empty, DateTimeOffset.Now));
        }

        try
        {
            Persist(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or PathTooLongException)
        {
            return new SelectionSidecarResult(false, $"写入替换标注失败：{ex.Message}");
        }

        return new SelectionSidecarResult(true, $"已写入替换标注（第 {pageNo + 1} 页）。");
    }

    /// <inheritdoc />
    public IReadOnlyList<SelectionSidecarAnnotation> Get(int pageNo)
    {
        lock (_lock)
        {
            if (_activeDocument is null || !_documents.TryGetValue(_activeDocument, out var document))
            {
                return [];
            }

            return document.Entries.Where(e => e.PageIndex == pageNo).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SelectionSidecarAnnotation> GetAll()
    {
        lock (_lock)
        {
            return _activeDocument is not null && _documents.TryGetValue(_activeDocument, out var document)
                ? document.Entries.ToList()
                : [];
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        List<DocumentSidecar> documents;
        lock (_lock)
        {
            documents = _documents.Values.ToList();
            _documents.Clear();
            _activeDocument = null;
        }

        foreach (var document in documents)
        {
            TryDelete(document);
        }
    }

    /// <inheritdoc />
    public void ClearFor(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath)) return;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(documentPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = documentPath;
        }

        DocumentSidecar? document;
        lock (_lock)
        {
            _documents.Remove(normalized, out document);
        }

        if (document is not null)
        {
            TryDelete(document);
        }
    }

    // ------------------------------------------------------------------ 内部

    private DocumentSidecar GetOrCreateLocked(string documentPath)
    {
        if (_documents.TryGetValue(documentPath, out var existing)) return existing;
        var created = new DocumentSidecar(documentPath);
        _documents[documentPath] = created;
        return created;
    }

    /// <summary>落盘（原子写：temp → rename），随后置隐藏属性。首次解析并缓存文件路径。</summary>
    private void Persist(DocumentSidecar document)
    {
        document.FilePath ??= ResolveFilePath(document.DocumentPath);
        var target = document.FilePath;

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(BuildFileModel(document), SerializerOptions);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json, Utf8NoBom);

        if (File.Exists(target))
        {
            // 已存在的 sidecar 可能带隐藏属性，先归位再删除，避免替换失败。
            TryClearAttributes(target);
            File.Delete(target);
        }

        File.Move(temp, target);
        _ = TrySetHidden(target);
    }

    /// <summary>优先源目录；源目录不可写时回退 %TEMP%（路径含规范化文档路径的 SHA-256 前 16 位）。</summary>
    private string ResolveFilePath(string documentPath)
    {
        var directory = Path.GetDirectoryName(documentPath);
        if (!string.IsNullOrEmpty(directory) && _isDirectoryWritable(directory))
        {
            return Path.Combine(directory, "." + Path.GetFileName(documentPath) + FileSuffix);
        }

        Directory.CreateDirectory(_tempRoot);
        return Path.Combine(_tempRoot, HashPath(documentPath) + FileSuffix);
    }

    private static SidecarFileModel BuildFileModel(DocumentSidecar document)
    {
        var entries = new List<SidecarEntryModel>(document.Entries.Count);
        foreach (var entry in document.Entries)
        {
            entries.Add(new SidecarEntryModel(
                entry.PageIndex,
                entry.Rect.Left,
                entry.Rect.Top,
                entry.Rect.Right,
                entry.Rect.Bottom,
                entry.Translation,
                entry.LangPair,
                entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)));
        }

        return new SidecarFileModel(SchemaVersion, document.DocumentPath, entries);
    }

    private void TryDelete(DocumentSidecar document)
    {
        var path = document.FilePath;
        if (path is null) return;
        try
        {
            TryClearAttributes(path);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败（文件占用 / 杀软锁）不致命：进程退出即结束会话，残留文件下次不被加载。
        }
    }

    private static void TryClearAttributes(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 属性归位失败：交给后续删除/替换路径处理。
        }
    }

    private static bool TrySetHidden(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 隐藏属性设置失败不影响 sidecar 内容（文件仍写入成功）。
            return false;
        }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        try
        {
            var probe = Path.Combine(directory, ".aipdf_write_probe_" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or PathTooLongException)
        {
            return false;
        }
    }

    private static PdfRect Normalize(PdfRect rect)
        => new(
            Math.Min(rect.Left, rect.Right),
            Math.Min(rect.Top, rect.Bottom),
            Math.Max(rect.Left, rect.Right),
            Math.Max(rect.Top, rect.Bottom));

    private static string HashPath(string documentPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(documentPath.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>单文档会话内条目 + 已解析的落盘路径。</summary>
    private sealed class DocumentSidecar
    {
        public DocumentSidecar(string documentPath) => DocumentPath = documentPath;

        public string DocumentPath { get; }

        public string? FilePath { get; set; }

        public List<SelectionSidecarAnnotation> Entries { get; } = [];
    }

    private sealed record SidecarFileModel(int Version, string Document, IReadOnlyList<SidecarEntryModel> Annotations);

    /// <summary>落盘条目：坐标展开为四边，避免序列化 <see cref="PdfRect"/> 的派生 Width/Height。</summary>
    private sealed record SidecarEntryModel(
        int Page,
        double Left,
        double Top,
        double Right,
        double Bottom,
        string Translation,
        string LangPair,
        string Timestamp);
}
