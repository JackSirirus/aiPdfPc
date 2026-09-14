// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端 API Key 存储（决策 #9 / #14 / #21；云端配置复用 (b)(g)(i)）：
//   - 密钥交 Windows 凭据管理器（advapi32 CredRead/CredWrite/CredDelete/CredEnumerate + CredFree）加密保存；
//   - 命名空间 AIPDF-Translate-{provider}-{sha256(endpoint origin) 前 16 位 hex}，不同目的地互不覆盖；
//   - 后端 ICredentialBackend 可注入：生产为 Win32CredentialBackend，测试用内存替身（不碰真实凭据管理器）；
//   - DeleteAll 只清理 AIPDF-Translate-* 前缀（卸载清理），不影响用户其他凭据。
//
// 红线（决策 #9 / #21）：密钥只在「调用方 → 本存储 → 凭据管理器 blob」间传递；本类不含文件 I/O、
// 序列化、日志或异常消息拼接。密钥绝不写入 settings.json / recent / session / reading-state / presets，
// 绝不进 argv / 环境变量 / 崩溃转储 / 会话恢复；进入子进程时仅经 stdin 传递（T5 域）。
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace AIPDF.Windows.Services;

/// <summary>
/// 凭据后端抽象：把「系统凭据管理器」与调用方解耦，测试可注入内存实现。
/// <see cref="Read"/> 未命中返回 null；<see cref="Delete"/> 未命中返回 false；
/// 系统不可用（GPO / LTSC / 沙箱 / 服务账户）时抛 <see cref="CredentialStoreUnavailableException"/>（T39：不假装可用）。
/// </summary>
public interface ICredentialBackend
{
    /// <summary>读取目标凭据的密钥；不存在返回 null。</summary>
    string? Read(string target);

    /// <summary>写入（覆盖）目标凭据的密钥。</summary>
    void Write(string target, string secret);

    /// <summary>删除目标凭据；原本不存在返回 false。</summary>
    bool Delete(string target);

    /// <summary>枚举以 <paramref name="prefix"/> 开头的目标名（供批量清理）。</summary>
    IReadOnlyList<string> Enumerate(string prefix);
}

/// <summary>凭据管理器不可用（区别于「未配置密钥」「输入错误」）：GPO / LTSC / Windows 沙箱 / 服务账户等。</summary>
public sealed class CredentialStoreUnavailableException : Exception
{
    /// <summary>不携带任何密钥信息，仅报告系统调用失败。</summary>
    public CredentialStoreUnavailableException(string message, int win32Error)
        : base(message)
        => Win32Error = win32Error;

    /// <summary>底层 Win32 错误码（诊断用，不含密钥）。</summary>
    public int Win32Error { get; }
}

/// <summary>
/// Windows 凭据管理器后端（<c>advapi32.dll</c> 通用凭据）。密钥以 UTF-16LE 写入 CredentialBlob。
/// </summary>
public sealed class Win32CredentialBackend : ICredentialBackend
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2; // 跨会话持久化到本机
    private const int ErrorNotFound = 1168;

    /// <inheritdoc />
    public string? Read(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw Unavailable(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return "";
            }

            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)(credential.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <inheritdoc />
    public void Write(string target, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(secret);

        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(secret.Length * sizeof(char)),
                Persist = CredPersistLocalMachine,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw Unavailable(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    /// <inheritdoc />
    public bool Delete(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (CredDelete(target, CredTypeGeneric, 0)) return true;

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw Unavailable(error);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Enumerate(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (!CredEnumerate(prefix + "*", 0, out var count, out var array))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return [];
            throw Unavailable(error);
        }

        try
        {
            var targets = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var entry = Marshal.ReadIntPtr(array, (int)i * IntPtr.Size);
                if (entry == IntPtr.Zero) continue;

                var credential = Marshal.PtrToStructure<CREDENTIAL>(entry);
                if (!string.IsNullOrEmpty(credential.TargetName))
                {
                    targets.Add(credential.TargetName);
                }
            }

            return targets;
        }
        finally
        {
            CredFree(array);
        }
    }

    private static CredentialStoreUnavailableException Unavailable(int error)
        => new($"Windows 凭据管理器不可用（Win32 错误 {error}）。", error);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// 云端翻译 API Key 存储：按「服务商 + 端点 origin」寻址系统凭据管理器中的通用凭据。
/// 与 <see cref="SessionCredentialStore"/>（PDF 文档密码，仅内存、不落盘）刻意分离：本类是长期凭据，交 OS 加密保存。
/// </summary>
public sealed class TranslationCredentialStore
{
    /// <summary>凭据目标命名空间前缀；卸载清理以此为准（决策「凭据命名与卸载清理」）。</summary>
    public const string TargetPrefix = "AIPDF-Translate-";

    private readonly ICredentialBackend _backend;

    /// <summary>生产默认注入 <see cref="Win32CredentialBackend"/>；测试注入内存替身。</summary>
    public TranslationCredentialStore(ICredentialBackend? backend = null)
        => _backend = backend ?? new Win32CredentialBackend();

    /// <summary>保存（覆盖）某服务商 + 端点的 API Key。密钥仅进入系统凭据管理器。</summary>
    public void Store(string provider, string endpoint, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _backend.Write(TargetName(provider, endpoint), key);
    }

    /// <summary>读取某服务商 + 端点的 API Key；未保存返回 null。</summary>
    public string? Get(string provider, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(endpoint)) return null;
        return _backend.Read(TargetName(provider, endpoint));
    }

    /// <summary>删除某服务商 + 端点的 API Key；原本不存在返回 false。</summary>
    public bool Delete(string provider, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(endpoint)) return false;
        return _backend.Delete(TargetName(provider, endpoint));
    }

    /// <summary>清除全部 <c>AIPDF-Translate-*</c> 凭据（显式卸载 / 用户在设置中清除）。返回删除条数。</summary>
    public int DeleteAll()
    {
        var removed = 0;
        foreach (var target in _backend.Enumerate(TargetPrefix))
        {
            if (_backend.Delete(target)) removed++;
        }

        return removed;
    }

    /// <summary>
    /// 凭据目标名：<c>AIPDF-Translate-{provider}-{sha256(endpoint origin) 前 16 位 hex}</c>。
    /// endpoint 归一为 origin，使同一目的地的密钥不被路径 / 查询串差异切成多条。
    /// 指纹长度与 <see cref="TranslationConsent.FingerprintLength"/> 统一为 16 位，避免凭据目标与同意指纹长度不一致。
    /// </summary>
    internal static string TargetName(string provider, string endpoint)
    {
        var origin = TranslationConsent.NormalizeEndpoint(endpoint);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(origin));
        var shortHash = Convert.ToHexString(hash).ToLowerInvariant()[..TranslationConsent.FingerprintLength];
        return $"{TargetPrefix}{provider.Trim()}-{shortHash}";
    }
}
