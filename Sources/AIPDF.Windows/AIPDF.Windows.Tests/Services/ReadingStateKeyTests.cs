// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4 阅读状态键契约测试：确定性 + 对 size/mtime 敏感（BLK-1）。
using System.IO;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class ReadingStateKeyTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var a = ReadingStateKey.Compute(@"C:\docs\a.pdf", 1234, 999);
        var b = ReadingStateKey.Compute(@"C:\docs\a.pdf", 1234, 999);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ChangesWithSize_AndWithMtime()
    {
        var baseline = ReadingStateKey.Compute(@"C:\docs\a.pdf", 100, 10);
        Assert.NotEqual(baseline, ReadingStateKey.Compute(@"C:\docs\a.pdf", 101, 10)); // size 变
        Assert.NotEqual(baseline, ReadingStateKey.Compute(@"C:\docs\a.pdf", 100, 11)); // mtime 变
        Assert.NotEqual(baseline, ReadingStateKey.Compute(@"C:\docs\b.pdf", 100, 10)); // 路径变
    }

    [Fact]
    public void TryCapture_ReflectsFileChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "aipdf-key-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(path, "AAAA");
            var first = ReadingStateKey.TryCapture(path);
            Assert.NotNull(first);

            File.WriteAllText(path, "BBBBBB");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(60));
            var second = ReadingStateKey.TryCapture(path);
            Assert.NotNull(second);

            Assert.NotEqual(first!.Value.Key, second!.Value.Key);
            Assert.NotEqual(first.Value.ModifiedTicks, second.Value.ModifiedTicks);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void TryCapture_MissingFile_ReturnsNull()
        => Assert.Null(ReadingStateKey.TryCapture(Path.Combine(Path.GetTempPath(), "aipdf-missing-" + Guid.NewGuid().ToString("N") + ".tmp")));
}
