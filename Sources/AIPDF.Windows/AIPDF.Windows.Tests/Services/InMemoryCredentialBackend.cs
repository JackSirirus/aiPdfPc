// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 凭据后端测试替身：内存字典，绝不接触真实 Windows 凭据管理器。
using AIPDF.Windows.Services;

namespace AIPDF.Windows.Tests.Services;

/// <summary><see cref="ICredentialBackend"/> 的内存实现；供 <see cref="TranslationCredentialStore"/> 单测注入。</summary>
internal sealed class InMemoryCredentialBackend : ICredentialBackend
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <summary>当前驻留条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>目标名是否已写入（测试断言用，不返回密钥本身）。</summary>
    public bool Contains(string target) => _entries.ContainsKey(target);

    public string? Read(string target) => _entries.TryGetValue(target, out var secret) ? secret : null;

    public void Write(string target, string secret) => _entries[target] = secret;

    public bool Delete(string target) => _entries.Remove(target);

    public IReadOnlyList<string> Enumerate(string prefix)
        => _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}
