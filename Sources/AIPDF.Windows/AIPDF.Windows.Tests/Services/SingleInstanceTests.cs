// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using System.IO.Pipes;
using System.Text;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

/// <summary>
/// 单实例互斥 / 命名管道转发 / 启动参数解析测试（FR-32）。
/// <para>
/// 互斥体是进程全局且线程亲和的：第二个获取者必须在别的线程才可能正确判定「已有实例」，
/// 且每个用例显式 <c>Dispose</c> 释放，绝不使用生产名（一律随机测试名），避免污染真实应用/其他用例。
/// 管道用例使用随机管道名，进程内隔离；不触碰真实注册表与真实 <c>.pdf</c> 关联。
/// </para>
/// </summary>
public sealed class SingleInstanceTests
{
    // ------------------------------------------------------------------ 互斥体

    [Fact]
    public void SingleInstanceLock_FirstSucceeds_SecondOnOtherThreadFails()
    {
        var name = NewMutexName();
        using var first = new SingleInstanceLock(name);
        Assert.True(first.IsFirstInstance);

        // Mutex 线程亲和：同线程递归 WaitOne 会误判成功，必须换线程验证。
        var secondIsFirst = Task.Run(() =>
        {
            using var second = new SingleInstanceLock(name);
            return second.IsFirstInstance;
        }).GetAwaiter().GetResult();

        Assert.False(secondIsFirst);
        Assert.True(first.TryAcquireOnce()); // 已持有：幂等
    }

    [Fact]
    public void SingleInstanceLock_ExplicitReleaseAllowsReacquireOnOtherThread()
    {
        var name = NewMutexName();
        var first = new SingleInstanceLock(name);
        Assert.True(first.IsFirstInstance);

        first.Dispose(); // 显式释放（非依赖进程退出）

        var reacquired = Task.Run(() =>
        {
            using var again = new SingleInstanceLock(name);
            return again.IsFirstInstance;
        }).GetAwaiter().GetResult();

        Assert.True(reacquired);
    }

    [Fact]
    public void SingleInstanceLock_SecondTryAcquireOnceStillFailsWhileHeld()
    {
        var name = NewMutexName();
        using var first = new SingleInstanceLock(name);

        var result = Task.Run(() =>
        {
            using var second = new SingleInstanceLock(name);
            return second.TryAcquireOnce();
        }).GetAwaiter().GetResult();

        Assert.False(result); // 首实例存活期间，第二实例有界重试后也无法接管
    }

    // ------------------------------------------------------------------ 管道名

    [Fact]
    public void PipeNameForSid_StableDistinctAndPrefixed()
    {
        var a = SingleInstanceNames.PipeNameForSid("S-1-5-21-111");
        var b = SingleInstanceNames.PipeNameForSid("S-1-5-21-222");

        Assert.Equal(a, SingleInstanceNames.PipeNameForSid("S-1-5-21-111")); // 确定
        Assert.NotEqual(a, b);                                             // 跨用户不串扰
        Assert.StartsWith("AIPDF_SingleInstance_", a);
        Assert.Equal(32, a["AIPDF_SingleInstance_".Length..].Length);     // 128 bit 前缀哈希
    }

    // ------------------------------------------------------------------ 消息编解码

    [Fact]
    public void PathListCodec_RoundTripsSpacesAndCjk()
    {
        var paths = new[] { @"C:\a b\报告.pdf", @"C:\x.pdf", @"\\server\share\文件 名.pdf" };

        var frame = PathListCodec.Encode(paths);

        Assert.True(PathListCodec.TryDecode(frame, out var decoded));
        Assert.Equal(paths, decoded);
    }

    [Fact]
    public void PathListCodec_EmptyListProducesValidEmptyFrame()
    {
        Assert.True(PathListCodec.TryDecode(PathListCodec.Encode([]), out var decoded));
        Assert.Empty(decoded);
    }

    [Fact]
    public void PathListCodec_SkipsBlankAndEmbeddedNulEntries()
    {
        var frame = PathListCodec.Encode(["", "   ", "ok.pdf", "a\0b.pdf"]);

        Assert.True(PathListCodec.TryDecode(frame, out var decoded));
        Assert.Equal(["ok.pdf"], decoded);
    }

    [Fact]
    public void PathListCodec_RejectsTooShortFrame()
    {
        Assert.False(PathListCodec.TryDecode(null, out var none));
        Assert.Empty(none);
        Assert.False(PathListCodec.TryDecode([1, 2, 3], out _));
    }

    [Fact]
    public void PathListCodec_RejectsMismatchedDeclaredLength()
    {
        var frame = PathListCodec.Encode(["C:\\a.pdf"]);
        frame[0] ^= 0x01; // 篡改长度前缀

        Assert.False(PathListCodec.TryDecode(frame, out _));
    }

    [Fact]
    public void PathListCodec_RejectsNegativePathLength()
    {
        Assert.False(PathListCodec.TryDecode(FrameWithBody([0xFF, 0xFF, 0xFF, 0xFF]), out _));
    }

    [Fact]
    public void PathListCodec_RejectsPathLengthBeyondBoundary()
    {
        // 声明路径 9999 字节，但帧里没有这么多字节
        Assert.False(PathListCodec.TryDecode(FrameWithBody([0x0F, 0x27, 0, 0]), out _));
    }

    [Fact]
    public void PathListCodec_RejectsInvalidUtf8()
    {
        Assert.False(PathListCodec.TryDecode(FrameWithBody([2, 0, 0, 0, 0xFF, 0xFE]), out _));
    }

    [Fact]
    public void PathListCodec_RejectsEmbeddedNul()
    {
        var raw = Encoding.UTF8.GetBytes("a\0b");
        var body = new byte[4 + raw.Length];
        BitConverter.TryWriteBytes(body.AsSpan(0, 4), raw.Length);
        raw.CopyTo(body, 4);

        Assert.False(PathListCodec.TryDecode(FrameWithBody(body), out _));
    }

    [Fact]
    public void PathListCodec_RejectsOversizeFramePrefix()
    {
        var frame = new byte[4];
        BitConverter.TryWriteBytes(frame, PathListCodec.MaxFrameBytes + 1);

        Assert.False(PathListCodec.TryDecode(frame, out _));
    }

    // ------------------------------------------------------------------ 退避策略

    [Fact]
    public void BoundedRetryPolicy_BackoffGrowsThenCaps()
    {
        Assert.Equal(200, BoundedRetryPolicy.DelayMsForAttempt(0));
        Assert.Equal(400, BoundedRetryPolicy.DelayMsForAttempt(1));
        Assert.Equal(800, BoundedRetryPolicy.DelayMsForAttempt(2));
        Assert.Equal(1600, BoundedRetryPolicy.DelayMsForAttempt(3));
        Assert.Equal(2000, BoundedRetryPolicy.DelayMsForAttempt(4));
        Assert.Equal(2000, BoundedRetryPolicy.DelayMsForAttempt(100)); // 不溢出、封顶
    }

    // ------------------------------------------------------------------ 管道（进程内集成）

    [Fact]
    public async Task PipeServer_ReceivesForwardedPaths()
    {
        var pipeName = NewPipeName();
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SingleInstancePipeServer(pipeName, paths => received.TrySetResult(paths));
        server.Start();

        var sent = new[] { @"C:\tmp\a.pdf", @"C:\tmp\测试 文档.pdf" };
        Assert.True(await SingleInstancePipeClient.SendAsync(pipeName, sent, CancellationToken.None));

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(sent, got);
    }

    [Fact]
    public async Task PipeServer_EmptyFrameSignalsActivation()
    {
        var pipeName = NewPipeName();
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SingleInstancePipeServer(pipeName, paths => received.TrySetResult(paths));
        server.Start();

        Assert.True(await SingleInstancePipeClient.SendAsync(pipeName, [], CancellationToken.None));

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(got); // 无路径的二次启动 = 激活信号
    }

    [Fact]
    public async Task PipeServer_ToleratesBadFrameThenAcceptsValidOne()
    {
        var pipeName = NewPipeName();
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SingleInstancePipeServer(pipeName, paths => received.TrySetResult(paths));
        server.Start();

        // 第一连接：非法长度前缀（-1）→ 服务端丢弃该连接但继续监听
        using (var bad = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            await bad.ConnectAsync(2000);
            await bad.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            await bad.FlushAsync();
        }

        // 第二连接：合法帧仍被正常接收
        Assert.True(await SingleInstancePipeClient.SendWithRetryAsync(pipeName, [@"C:\ok.pdf"], CancellationToken.None));

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([@"C:\ok.pdf"], got);
    }

    // ------------------------------------------------------------------ 启动参数解析

    [Fact]
    public void StartupPathParser_FiltersNonPdfAndMissingAndBlank()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\docs\a.pdf", @"C:\docs\B.PDF" };
        var args = new[] { @"C:\docs\a.pdf", @"C:\docs\missing.pdf", @"C:\docs\note.txt", "", "   ", @"C:\docs\B.PDF" };

        var result = StartupPathParser.Collect(args, existing.Contains);

        Assert.Equal([@"C:\docs\a.pdf", @"C:\docs\B.PDF"], result); // 多文件保序
    }

    [Fact]
    public void StartupPathParser_DedupsCaseInsensitivelyAndStripsQuotes()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\docs\a.pdf" };

        var result = StartupPathParser.Collect([@"""C:\docs\a.pdf""", @"C:\docs\A.pdf"], existing.Contains);

        var single = Assert.Single(result);
        Assert.Equal(@"C:\docs\a.pdf", single);
    }

    [Fact]
    public void StartupPathParser_NormalizesRelativeToFullPath()
    {
        var result = StartupPathParser.Collect(["relative.pdf"], _ => true);

        var single = Assert.Single(result);
        Assert.Equal(Path.GetFullPath("relative.pdf"), single);
    }

    // ------------------------------------------------------------------ 辅助

    private static string NewMutexName() => @"Local\AIPDF_SingleInstance_test_" + Guid.NewGuid().ToString("N");

    private static string NewPipeName() => "AIPDF_SingleInstance_test_" + Guid.NewGuid().ToString("N");

    private static byte[] FrameWithBody(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }
}
