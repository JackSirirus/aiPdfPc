// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AIPDF.Windows.Services;

/// <summary>
/// 命名常量与命名管道名构造（FR-32 / PRD 4.7.1）。
/// <para>
/// 互斥体命名空间固定为 <c>Local\AIPDF_SingleInstance</c>：仅约束当前会话（同一登录会话内单实例）。
/// RDP / 多会话（同一用户或不同用户各开一个会话）下每会话可各有一个实例，这是 V2 的已知范围；
/// 如需跨会话防双开须改用 <c>Global\</c>，会引入权限与多用户可见性影响，V2 不采用。
/// </para>
/// <para>
/// 管道名含当前用户 SID 的 SHA-256 前缀哈希，避免同名进程抢注与不同用户之间的串扰；
/// 服务端另用 <see cref="PipeOptions.CurrentUserOnly"/> 只允许同用户连接（纵深防御）。
/// </para>
/// </summary>
public static class SingleInstanceNames
{
    /// <summary>单实例互斥体名（Local\ = 当前会话；跨会话行为见类型注释）。</summary>
    public const string MutexName = @"Local\AIPDF_SingleInstance";

    private const string PipePrefix = "AIPDF_SingleInstance_";

    /// <summary>由用户 SID 计算管道名（确定、可单测；不依赖当前进程身份）。</summary>
    public static string PipeNameForSid(string sid)
    {
        ArgumentException.ThrowIfNullOrEmpty(sid);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        return PipePrefix + Convert.ToHexString(hash, 0, 16); // 128 bit / 32 hex，防止跨用户同管
    }

    /// <summary>当前进程用户的管道名；取不到 Windows SID（异常环境）时退回用户名。</summary>
    public static string CurrentUserPipeName()
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch (Exception)
        {
            identity = Environment.UserName;
        }

        return PipeNameForSid(identity);
    }
}

/// <summary>
/// 转发消息编解码（IPC 契约，写死细节）。
/// <para>
/// 帧格式 = <c>[int32 little-endian payloadLength][payload]</c>；
/// payload = 若干条「<c>[int32 little-endian pathByteLength][UTF-8 pathBytes]</c>」串联。
/// </para>
/// <para>
/// 校验：帧长上限 <see cref="MaxFrameBytes"/>、单路径上限 <see cref="MaxPathBytes"/>、
/// 每段长度必须落在剩余边界内、帧尾必须精确对齐、UTF-8 严格解码（非法字节即拒绝）、
/// 路径内不得含 NUL。任何不满足的字节流都判为坏消息并整帧丢弃，绝不部分应用。
/// </para>
/// </summary>
public static class PathListCodec
{
    /// <summary>单帧 payload 上限（1 MiB）：防坏帧/恶意帧占用内存。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>单条路径 UTF-8 字节上限（32 KiB）：Windows 路径远小于此。</summary>
    public const int MaxPathBytes = 32 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>编码路径列表为完整帧（跳过空白项与超限项，保证空列表也产生合法空帧）。</summary>
    public static byte[] Encode(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            {
                continue;
            }

            var bytes = StrictUtf8.GetBytes(path);
            if (bytes.Length == 0 || bytes.Length > MaxPathBytes)
            {
                continue;
            }

            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        var body = payload.ToArray();

        var frame = new byte[sizeof(int) + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, sizeof(int)), body.Length);
        body.CopyTo(frame, sizeof(int));
        return frame;
    }

    /// <summary>
    /// 解码完整帧。只有整帧结构完全合法（长度前缀、逐段边界、UTF-8 严格、无 NUL）才返回 true；
    /// 坏消息返回 false 且 <paramref name="paths"/> 为空列表（调用方整帧丢弃）。
    /// </summary>
    public static bool TryDecode(byte[]? frame, out List<string> paths)
    {
        paths = [];
        if (frame is null || frame.Length < sizeof(int))
        {
            return false;
        }

        var bodyLength = BitConverter.ToInt32(frame, 0);
        if (bodyLength < 0 || bodyLength > MaxFrameBytes || frame.Length - sizeof(int) != bodyLength)
        {
            return false; // 边界校验：声明的帧长必须与实际字节数精确一致
        }

        var offset = sizeof(int);
        var end = sizeof(int) + bodyLength;
        while (offset < end)
        {
            if (end - offset < sizeof(int))
            {
                return false; // 路径长度前缀被截断
            }

            var pathLength = BitConverter.ToInt32(frame, offset);
            offset += sizeof(int);
            if (pathLength <= 0 || pathLength > MaxPathBytes || offset + pathLength > end)
            {
                return false;
            }

            string path;
            try
            {
                path = StrictUtf8.GetString(frame, offset, pathLength);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            if (path.IndexOf('\0') >= 0)
            {
                return false;
            }

            paths.Add(path);
            offset += pathLength;
        }

        return offset == end;
    }
}

/// <summary>
/// 第二实例连接首实例的有界重试节奏（FR-32 / H-7）：最多 <see cref="MaxAttempts"/> 次，
/// 指数退避封顶 <see cref="MaxDelayMs"/>。纯函数，可单测。
/// </summary>
public static class BoundedRetryPolicy
{
    public const int MaxAttempts = 10;
    public const int BaseDelayMs = 200;
    public const int MaxDelayMs = 2000;

    /// <summary>第 <paramref name="attempt"/> 次失败后的等待毫秒（200/400/800/1600/2000…）。</summary>
    public static int DelayMsForAttempt(int attempt)
    {
        if (attempt < 0)
        {
            attempt = 0;
        }

        // 位移上限 10，避免 1 << 31 溢出；200 * 2^10 早已超过封顶。
        var scaled = BaseDelayMs * (1 << Math.Min(attempt, 10));
        return Math.Min(scaled, MaxDelayMs);
    }
}

/// <summary>
/// 首实例的命名管道服务端：循环接受同用户连接、读取一帧路径列表、回调给宿主。
/// 报文解析/边界校验全部交给 <see cref="PathListCodec"/>；坏帧静默丢弃不影响后续连接。
/// 通过 <see cref="CancellationTokenSource"/> 取消退出（应用退出时释放）。
/// </summary>
public sealed class SingleInstancePipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onPaths;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SingleInstancePipeServer(string pipeName, Action<IReadOnlyList<string>> onPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        _pipeName = pipeName;
        _onPaths = onPaths ?? throw new ArgumentNullException(nameof(onPaths));
    }

    /// <summary>启动后台监听循环（非阻塞）；必须在创建主窗口之前调用（防启动竞态）。</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                var frame = await ReadFrameAsync(server, token).ConfigureAwait(false);
                // 空帧也回调：第二实例即使不带路径，也是一次「激活已有实例」的信号。
                if (frame is not null && PathListCodec.TryDecode(frame, out var paths))
                {
                    _onPaths(paths);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 连接中断/管道错误：丢弃本次连接继续监听（单实例转发属尽力而为，不崩溃首实例）
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>读取完整帧：先精确读 4 字节长度前缀，再精确读 payload；对端关闭返回 null。</summary>
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[sizeof(int)];
        if (!await ReadExactAsync(stream, prefix, token).ConfigureAwait(false))
        {
            return null;
        }

        var bodyLength = BitConverter.ToInt32(prefix, 0);
        if (bodyLength < 0 || bodyLength > PathListCodec.MaxFrameBytes)
        {
            return null; // 声明长度非法：不再读取，直接丢弃该连接
        }

        var frame = new byte[sizeof(int) + bodyLength];
        prefix.CopyTo(frame, 0);
        if (bodyLength > 0 && !await ReadExactAsync(stream, frame.AsMemory(sizeof(int), bodyLength), token).ConfigureAwait(false))
        {
            return null;
        }

        return frame;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], token).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // 关闭等待：退出路径不阻塞应用关闭
        }

        _cts.Dispose();
    }
}

/// <summary>
/// 第二实例的命名管道客户端：把路径列表发给首实例。单次连接有超时；
/// 调用方按 <see cref="BoundedRetryPolicy"/> 做有界重试（防启动竞态 H-7）。
/// </summary>
public static class SingleInstancePipeClient
{
    private const int ConnectTimeoutMs = 200;

    /// <summary>
    /// 尝试把路径转发给已有实例（空列表也发空帧 = 激活信号）；连接失败/超时抛
    /// <see cref="TimeoutException"/> 由调用方重试。成功返回 true。
    /// </summary>
    public static async Task<bool> SendAsync(string pipeName, IReadOnlyList<string> paths, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(paths);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectTimeoutMs, token).ConfigureAwait(false);
        var frame = PathListCodec.Encode(paths);
        await client.WriteAsync(frame, token).ConfigureAwait(false);
        await client.FlushAsync(token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 有界重试转发：最多 <see cref="BoundedRetryPolicy.MaxAttempts"/> 次，指数退避封顶 2s。
    /// 全部失败返回 false，调用方据此决定「检查互斥体/允许启动新实例」，绝不因转发失败卡死。
    /// </summary>
    public static async Task<bool> SendWithRetryAsync(string pipeName, IReadOnlyList<string> paths, CancellationToken token)
    {
        for (var attempt = 0; attempt < BoundedRetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                return await SendAsync(pipeName, paths, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 连接失败/超时/管道忙：退避后重试
            }

            try
            {
                await Task.Delay(BoundedRetryPolicy.DelayMsForAttempt(attempt), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>
/// 单实例互斥体包装：<see cref="Mutex"/> 是线程亲和的原生对象，
/// 获取与释放必须在同一线程（测试须跨线程验证「第二个获取者失败」，见 SingleInstanceTests）。
/// 显式 <see cref="Dispose"/> 释放，避免测试的进程级副作用残留。
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owns;
    private bool _disposed;

    public SingleInstanceLock(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _mutex = new Mutex(initiallyOwned: false, name);
        _owns = TryAcquireOnce();
    }

    /// <summary>本实例是否持有互斥体（= 首实例）。</summary>
    public bool IsFirstInstance => _owns;

    /// <summary>非阻塞再获取（第二实例有界重试耗尽后再试一次：对方可能已退出）。</summary>
    public bool TryAcquireOnce()
    {
        if (_owns)
        {
            return true;
        }

        try
        {
            _owns = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 前一个持有者崩溃：本实例接管互斥体视为获取成功。
            _owns = true;
        }

        return _owns;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_owns)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 非持有线程释放：忽略，进程退出时由 OS 兜底
            }

            _owns = false;
        }

        _mutex.Dispose();
    }
}
