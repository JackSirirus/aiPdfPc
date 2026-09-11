// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 阅读状态键的跨任务契约（T4 落接口与键计算；完整状态文件 schema 由 T5 实现）。
//
// 键 = SHA-256(规范化绝对路径 + "|" + size + "|" + mtime)。
// 就地保存会改变 size/mtime，直接摧毁旧键；T4 在保存成功后计算新键并调用
// IReadingStateMigrator.MigrateAsync(oldKey, newKey)（迁移实现由 T5 提供）。
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIPDF.Windows.Services;

/// <summary>
/// 阅读状态键的纯计算（不读写状态文件）。T5 的 ReadingStateStore 必须复用同一算法，
/// 否则保存后的键迁移会指向错误文件。
/// </summary>
public static class ReadingStateKey
{
    /// <summary>
    /// 计算键：SHA-256(规范化绝对路径 + "|" + size + "|" + mtimeTicks)，小写十六进制。
    /// 路径按传入原样参与哈希（调用方负责先用 Path.GetFullPath 规范化）；
    /// mtime 取 <see cref="FileInfo.LastWriteTimeUtc"/> 的 Ticks（100ns，Windows 原生精度）。
    /// </summary>
    public static string Compute(string normalizedPath, long size, long modifiedTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        var payload = string.Create(CultureInfo.InvariantCulture, $"{normalizedPath}|{size}|{modifiedTicks}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>文件当前 stamp（size/mtime/key）。文件不存在或不可访问返回 null（不抛）。</summary>
    public static FileStateStamp? TryCapture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists) return null;
            var size = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            return new FileStateStamp(size, ticks, Compute(info.FullName, size, ticks));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>某文件在某时刻的 size / mtime / 键快照。</summary>
public readonly record struct FileStateStamp(long Size, long ModifiedTicks, string Key);
