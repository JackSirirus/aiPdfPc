// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2「用工具处理」置顶 + 页范围预填契约测试（H-6）：split/rotate 预填，compress/pdf_to_word 仅置顶。
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class ToolPinningTests : IDisposable
{
    private readonly string _dir;
    private readonly string _pdf;

    public ToolPinningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aipdf-t2-pin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _pdf = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(_pdf, [0x25, 0x50, 0x44, 0x46]); // %PDF（仅扩展名校验，不解析内容）
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* 临时目录清理失败不影响断言 */ }
    }

    [Fact]
    public void Split_PinAndPrefill_UsesRangesTextAndPinsDocument()
    {
        var tool = ToolCatalog.Load().Single(t => t.Id == "split");
        var vm = new SplitViewModel(null, tool);

        Assert.True(vm.SupportsPageRangePrefill);
        vm.PinCurrentDocument(_pdf);
        Assert.True(vm.HasPinnedDocument);
        Assert.Single(vm.Files);
        Assert.Equal(Path.GetFullPath(_pdf), vm.Files[0].Path);

        vm.PrefillPageRange(3, 3);
        Assert.Equal("3-3", vm.RangesText);
    }

    [Fact]
    public void Rotate_PinAndPrefill_UsesPagesAndInputFile()
    {
        var tool = ToolCatalog.Load().Single(t => t.Id == "rotate");
        var vm = new RotateViewModel(tool, (_, _, _, _) => Task.CompletedTask);

        Assert.True(vm.SupportsPageRangePrefill);
        vm.PinCurrentDocument(_pdf);
        Assert.True(vm.HasPinnedDocument);
        Assert.Equal(Path.GetFullPath(_pdf), vm.InputFile);

        vm.PrefillPageRange(5, 5);
        Assert.Equal("5-5", vm.Pages);
    }

    [Fact]
    public void Compress_PinsButHasNoPageRangePrefill()
    {
        var tool = ToolCatalog.Load().Single(t => t.Id == "compress");
        var vm = new CompressViewModel(null, tool);

        Assert.False(vm.SupportsPageRangePrefill);
        vm.PinCurrentDocument(_pdf);
        Assert.True(vm.HasPinnedDocument);
        Assert.Single(vm.Files);
        Assert.Equal(Path.GetFullPath(_pdf), vm.Files[0].Path);
    }

    [Fact]
    public void PdfToWord_PinsButHasNoPageRangePrefill()
    {
        var tool = ToolCatalog.Load().Single(t => t.Id == "pdf_to_word");
        var vm = new PdfToWordViewModel(null, tool);

        Assert.False(vm.SupportsPageRangePrefill);
        vm.PinCurrentDocument(_pdf);
        Assert.True(vm.HasPinnedDocument);
        Assert.Single(vm.Files);
        Assert.Equal(Path.GetFullPath(_pdf), vm.Files[0].Path);
    }
}
