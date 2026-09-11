// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T9 会话密码驻留（PRD 4.4.3）：
//   - 解锁一次后在标签/会话内免密；键 = 规范化全路径，比较器 OrdinalIgnoreCase；
//   - 仅存活于进程内存，进程退出即清除；
//   - 密码逐请求经 EngineBridge 的 options.password 传递（不缓存到引擎进程）。
//
// 红线（PRD 4.3.3）：密码绝不进入 reading-state / recent-files / session.json 三处持久化，
// 也不得写入日志 / stdout / stderr / 异常消息 / 命令行。本类刻意不包含任何文件 I/O、
// 序列化、日志或异常消息拼接；持有密码的值只在调用方请求时按路径返回。
using System.IO;

namespace AIPDF.Windows.Services;

/// <summary>
/// 会话密码驻留：内存 <c>Dictionary&lt;规范化路径, 密码&gt;</c>（<see cref="StringComparer.OrdinalIgnoreCase"/>）。
/// 解锁一次后同一文档的渲染 / 文本 / 保存 / 就地重载均免密；<b>只存内存，不落盘</b>。
/// </summary>
public sealed class SessionCredentialStore
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>当前驻留的文档数（测试与诊断用；不暴露任何密码值）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _passwords.Count;
            }
        }
    }

    /// <summary>
    /// 驻留一次解锁成功的密码。密码为空/null 视为撤销该路径（避免空密码污染）。
    /// </summary>
    public void Store(string path, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var key = Normalize(path);
        lock (_gate)
        {
            if (string.IsNullOrEmpty(password))
            {
                _passwords.Remove(key);
                return;
            }

            _passwords[key] = password;
        }
    }

    /// <summary>按路径取驻留密码；未驻留返回 null。大小写与相对/绝对路径差异均被规范化吸收。</summary>
    public string? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = Normalize(path);
        lock (_gate)
        {
            return _passwords.TryGetValue(key, out var password) ? password : null;
        }
    }

    /// <summary>该路径是否已驻留密码。</summary>
    public bool Has(string path) => Get(path) is not null;

    /// <summary>撤销单条驻留（该文档重新打开时需要再次输入）。</summary>
    public bool Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var key = Normalize(path);
        lock (_gate)
        {
            return _passwords.Remove(key);
        }
    }

    /// <summary>清除全部驻留（进程退出 / 会话结束）。</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _passwords.Clear();
        }
    }

    /// <summary>规范化路径为字典键；无副作用，非法路径回退原串（不抛、不记录）。</summary>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
