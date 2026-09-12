// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T9 ReaderViewModel 对开模式测试（真实 PDFium 渲染 + 合成语料）：
//   单页恒等映射不回归、对开配对、仅可视 ± 缓冲组持位图（虚拟化）、当前页经 group 映射、
//   组→页局部命中/选区映射、批注命中经映射、对开开关随 reading-state 持久化。
using System;
using System.IO;
using System.Threading.Tasks;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Services;
using AIPDF.Windows.Tests.Rendering;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>对开模式的 VM 集成测试。全部注入临时 reading-state 目录，避免触碰生产状态。</summary>
public sealed class ReaderSpreadModeTests
{
    private sealed class SpreadFixture : IDisposable
    {
        public string StoreDir { get; }
        public ReadingStateStore Store { get; }

        public SpreadFixture()
        {
            StoreDir = Path.Combine(Path.GetTempPath(), "aipdf-t9-" + Guid.NewGuid().ToString("N"));
            Store = new ReadingStateStore(StoreDir);
        }

        public ReaderViewModel NewVm() => new(new PdfiumRenderService(), readingStates: Store);

        public void Dispose()
        {
            try { if (Directory.Exists(StoreDir)) Directory.Delete(StoreDir, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 单页不回归

    [Fact]
    public void DefaultMode_IsSingle_WithIdentityMapping()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);

        Assert.False(vm.SpreadEnabled);
        Assert.True(vm.GroupMap.IsIdentity);
        Assert.Equal(100, vm.Pages.Count);
        Assert.Equal(100, vm.Groups.Count);
        Assert.Equal(5, vm.GroupMap.PageToGroup(5));
        Assert.Equal((5, 5), vm.GroupMap.GroupToPages(5));
        Assert.Equal(0, vm.Pages[5].OffsetX, 6);
        Assert.Equal(ReaderStrings.SpreadSinglePageLabel, vm.SpreadToggleText);
    }

    // ---------------------------------------------------------------- 对开配对 / 组几何

    [Fact]
    public void ToggleSpread_PairsPages_CoverAlone()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);

        vm.SpreadEnabled = true;

        Assert.True(vm.SpreadEnabled);
        Assert.False(vm.GroupMap.IsIdentity);
        Assert.Equal(51, vm.Groups.Count); // 封面单列：1 + 100/2
        Assert.Equal((0, 0), vm.GroupMap.GroupToPages(0));
        Assert.Equal((1, 2), vm.GroupMap.GroupToPages(1));
        Assert.Equal(2, vm.Groups[1].Pages.Count);
        Assert.Equal(1, vm.GroupMap.PageToGroup(1));
        Assert.Equal(1, vm.GroupMap.PageToGroup(2));
        Assert.Equal(2, vm.GroupMap.PageToGroup(3));
        Assert.Equal(ReaderStrings.SpreadTwoPageLabel, vm.SpreadToggleText);

        // 组内右页水平偏移 = 左页显示宽 + 页间距；左页为 0
        Assert.Equal(0, vm.Pages[1].OffsetX, 6);
        Assert.Equal(vm.Pages[1].DisplayWidth + PageLayoutCalculator.PageGap, vm.Pages[2].OffsetX, 6);
        Assert.Equal(0, vm.Pages[2].OffsetY, 6);

        // 组显示尺寸 = 组内两页宽 + 间距
        Assert.Equal(vm.Pages[1].DisplayWidth + vm.Pages[2].DisplayWidth + PageLayoutCalculator.PageGap,
            vm.Groups[1].DisplayWidth, 4);

        // 切回单页：恒等映射与组/页数一致
        vm.SpreadEnabled = false;
        Assert.True(vm.GroupMap.IsIdentity);
        Assert.Equal(100, vm.Groups.Count);
    }

    [Fact]
    public void ToggleSpread_PreservesCurrentPage()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);
        vm.GoToPage(10); // 单页：第 10 页

        vm.SpreadEnabled = true; // 组 4 = 页 9,10（封面单列）；当前页应仍落在组内

        var group = vm.GroupMap.PageToGroup(9);
        var (first, last) = vm.GroupMap.GroupToPages(group);
        Assert.InRange(vm.CurrentPageNumber - 1, first, last);
    }

    // ---------------------------------------------------------------- 虚拟化（仅可视 ± 缓冲组持位图）

    [Fact]
    public async Task SpreadMode_RendersOnlyVisibleGroupsAndBuffer()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);
        vm.SpreadEnabled = true;

        vm.UpdateViewport(0, 600, 800);
        await WaitUntilAsync(() => vm.Pages[0].IsRendered, "对开模式页 0 应渲染");

        // 视口 600px 落在组 0（高约 1123），缓冲 2 组 → 组 0..2 → 页 0..4
        for (var i = 0; i < 5; i++)
        {
            Assert.True(vm.Pages[i].Bitmap is not null || vm.Pages[i].PendingKey is not null,
                $"页 {i} 在可视 ± 缓冲组内，应持位图或在途");
        }

        for (var i = 5; i < vm.Pages.Count; i++)
        {
            Assert.True(vm.Pages[i].Bitmap is null && vm.Pages[i].PendingKey is null,
                $"页 {i} 超出可视 ± 缓冲组，不应持位图或在途（虚拟化不退化）");
        }
    }

    // ---------------------------------------------------------------- 当前页经 group 映射

    [Fact]
    public void SpreadMode_CurrentPage_IsFirstPageOfCurrentGroup()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);
        vm.SpreadEnabled = true;
        vm.UpdateViewport(0, 600, 800);

        vm.GoToPage(3); // 组 1 = 页 2,3 → 当前页取组首页
        Assert.Equal(2, vm.CurrentPageNumber);

        vm.GoToPage(4); // 组 2 = 页 4,5 → 当前页 4
        Assert.Equal(4, vm.CurrentPageNumber);
    }

    // ---------------------------------------------------------------- 组→页局部映射（选择/命中几何）

    [Fact]
    public void TryMapGroupPoint_MapsRightPageToPageLocalCoords()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);
        vm.SpreadEnabled = true;

        var left = vm.Pages[1];
        var right = vm.Pages[2];

        // 左页：groupX 直接为页局部 x
        Assert.True(vm.TryMapGroupPoint(1, 12, 7, out var leftPage, out var leftX, out var leftY));
        Assert.Equal(1, leftPage);
        Assert.Equal(12, leftX, 6);
        Assert.Equal(7, leftY, 6);

        // 右页：groupX 需减去页在组内偏移
        Assert.True(vm.TryMapGroupPoint(1, right.OffsetX + 20, 9, out var rightPage, out var rightX, out var rightY));
        Assert.Equal(2, rightPage);
        Assert.Equal(20, rightX, 6);
        Assert.Equal(9, rightY, 6);

        // 组内页间距：不命中（Inside=false），但仍返回最近页
        var inGap = vm.TryMapGroupPoint(1, left.DisplayWidth + 1, 9, out var gapPage, out _, out _);
        Assert.False(inGap);
        Assert.InRange(gapPage, 1, 2);
    }

    [Fact]
    public void HitTestAnnotationAt_UsesGroupMapping_DoesNotLeakAcrossPages()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);
        vm.SpreadEnabled = true;

        var record = vm.AddHighlight(1, new PdfRect(10, 10, 60, 40));
        Assert.NotNull(record);

        var page = vm.Pages[1];
        var overlay = Assert.Single(page.Annotations);

        // 组坐标 = 页在组内偏移 + 页局部命中点
        var groupIndex = vm.GroupMap.PageToGroup(1);
        var hit = vm.HitTestAnnotationAt(groupIndex, page.OffsetX + overlay.X + 1, overlay.Y + 1, out var hitPage);
        Assert.Equal(record!.Id, hit);
        Assert.Equal(1, hitPage);

        // 同组另一页上无批注 → 不命中（映射不会串页）
        var other = vm.Pages[2];
        var miss = vm.HitTestAnnotationAt(groupIndex, other.OffsetX + 5, 5, out var missPage);
        Assert.Null(miss);
        Assert.Equal(2, missPage);
    }

    // ---------------------------------------------------------------- 对开开关持久化（reading-state）

    [Fact]
    public void SpreadToggle_PersistsToReadingState_AndRestoresOnNextOpen()
    {
        using var fx = new SpreadFixture();
        string docPath;
        string key;

        using (var vm = fx.NewVm())
        {
            vm.LoadDocument(Corpus.Mixed100);
            docPath = vm.DocumentPath!;
            Assert.False(vm.SpreadEnabled); // 无状态默认单页

            vm.SpreadEnabled = true;

            var stamp = ReadingStateKey.TryCapture(docPath);
            Assert.NotNull(stamp);
            key = stamp!.Value.Key;
            var state = fx.Store.Load(key);
            Assert.NotNull(state);
            Assert.True(state!.SpreadEnabled);
        }

        // 重新打开同一文件：从 reading-state 恢复对开
        using (var vm2 = fx.NewVm())
        {
            vm2.LoadDocument(Corpus.Mixed100);
            Assert.True(vm2.SpreadEnabled);
            Assert.Equal(51, vm2.Groups.Count);
            Assert.Equal((1, 2), vm2.GroupMap.GroupToPages(1));
        }

        // 关掉对开并回写：状态翻回单页
        using (var vm3 = fx.NewVm())
        {
            vm3.LoadDocument(Corpus.Mixed100);
            Assert.True(vm3.SpreadEnabled);
            vm3.SpreadEnabled = false;
            Assert.False(fx.Store.Load(key)!.SpreadEnabled);
        }
    }

    [Fact]
    public void NoReadingState_DefaultsToSinglePage()
    {
        using var fx = new SpreadFixture();
        using var vm = fx.NewVm();
        vm.LoadDocument(Corpus.Mixed100);
        Assert.False(vm.SpreadEnabled);
        Assert.True(vm.GroupMap.IsIdentity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Xunit.Sdk.XunitException($"{message}（等待 {timeoutMs}ms 超时）");
            await Task.Delay(50);
        }
    }
}
