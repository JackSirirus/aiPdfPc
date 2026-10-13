// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T11 批注导出服务测试：参数守卫（无路径 / 无输出目录 → 空结果且不抛）。
//
// 真实导出链路（XFDF / 摘要 PDF / 源文件不变 / 外部注释也可导出）由引擎侧用例覆盖
// （tests/test_engine.py 的 test_export_annotations_*），这里只锁宿主的守卫语义。
using System.Threading.Tasks;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Tools;
using Xunit;

namespace AIPDF.Windows.Tests.Tools;

/// <summary>批注导出服务（参数守卫）。</summary>
public sealed class ExportAnnotationsServiceTests
{
    [Fact]
    public async Task ExportAsync_ReturnsEmpty_WhenPathOrOutputDirMissing()
    {
        var service = new ExportAnnotationsService(new EngineBridge());

        Assert.Empty(await service.ExportAsync("", null, @"C:\temp"));
        Assert.Empty(await service.ExportAsync(@"C:\docs\a.pdf", null, ""));
        Assert.Empty(await service.ExportAsync("   ", null, "   "));
    }

    [Fact]
    public void FormatConstants_AreStable()
    {
        // 引擎侧只认这三个值（export_annotations 的 options.format）
        Assert.Equal("both", ExportAnnotationsService.FormatBoth);
        Assert.Equal("xfdf", ExportAnnotationsService.FormatXfdf);
        Assert.Equal("summary", ExportAnnotationsService.FormatSummary);
    }
}
