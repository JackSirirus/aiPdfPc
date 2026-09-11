// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4 close-first 重载安全测试：句柄代际换代、LRU 清空（M-2）、在途 drain 阻塞关闭（BLK-3）、
// ReaderViewModel 重载保留未保存批注与缩放（视口保留 AC-20.2）。
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.Rendering;

public sealed class PdfiumReloadTests
{
    [Fact]
    public async Task CloseFirstReload_ChangesGeneration_AndClearsLru()
    {
        using var service = new PdfiumRenderService();
        var docId = service.OpenDocument(Corpus.Mixed100);
        var firstGeneration = service.GetDocumentGeneration(docId);

        await service.RenderPageAsync(docId, 0, 96, 100);
        Assert.Equal(1, service.GetLruStats(docId).EntryCount);

        // close-first：关闭旧句柄后重开同路径
        service.CloseDocument(docId);
        Assert.Equal(-1, service.GetDocumentGeneration(docId));

        var reopened = service.OpenDocument(Corpus.Mixed100);
        Assert.Equal(docId, reopened); // 同路径 docId 不变（规范化路径）
        var secondGeneration = service.GetDocumentGeneration(reopened);

        Assert.NotEqual(firstGeneration, secondGeneration); // 句柄已换代
        Assert.Equal(0, service.GetLruStats(reopened).EntryCount); // 缓存已清空，不会返回旧位图

        await service.RenderPageAsync(reopened, 0, 96, 100);
        Assert.Equal(1, service.GetLruStats(reopened).EntryCount);
    }

    [Fact]
    public void ClearDocumentCache_DropsLruWithoutChangingHandle()
    {
        using var service = new PdfiumRenderService();
        var docId = service.OpenDocument(Corpus.Mixed100);
        var generation = service.GetDocumentGeneration(docId);

        service.RenderPageAsync(docId, 0, 96, 100).GetAwaiter().GetResult();
        Assert.Equal(1, service.GetLruStats(docId).EntryCount);

        service.ClearDocumentCache(docId);

        Assert.Equal(0, service.GetLruStats(docId).EntryCount);
        Assert.Equal(generation, service.GetDocumentGeneration(docId)); // 不换句柄的失效路径
    }

    [Fact]
    public void CloseDocument_BlocksUntilInFlightRenderDrains()
    {
        using var service = new PdfiumRenderService();
        var docId = service.OpenDocument(Corpus.Mixed100);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        PdfiumRenderService.RenderCoreStartedForTest = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            var renderTask = service.RenderPageAsync(docId, 0, 96, 100);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "渲染应已登记在途");

            var closeTask = Task.Run(() => service.CloseDocument(docId));
            Assert.False(closeTask.Wait(TimeSpan.FromMilliseconds(300)),
                "在途原生调用未归零前 CloseDocument 必须阻塞（BLK-3）");

            release.Set();
            Assert.True(renderTask.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(closeTask.Wait(TimeSpan.FromSeconds(10)), "在途归零后关闭应完成");
            Assert.Equal(-1, service.GetDocumentGeneration(docId));
            Assert.Equal(0, service.GetInFlightCount(docId));
        }
        finally
        {
            PdfiumRenderService.RenderCoreStartedForTest = null;
            release.Set();
        }
    }

    [Fact]
    public void ReaderViewModel_Reload_PreservesAnnotationsAndZoom()
    {
        using var service = new PdfiumRenderService();
        using var vm = new ReaderViewModel(service);
        vm.LoadDocument(Corpus.Mixed100);
        vm.UpdateViewport(0, 600, 800);
        vm.ZoomPercent = 150;

        var record = vm.AddHighlight(0, new PdfRect(20, 30, 80, 60));
        Assert.NotNull(record);

        var generationBefore = service.GetDocumentGeneration(vm.DocumentPath!);
        vm.ReloadDocument();

        Assert.True(vm.HasDocument);
        Assert.Equal(150, vm.ZoomPercent); // 缩放保留（AC-20.2）
        var retained = Assert.Single(vm.GetAllRecords());
        Assert.Equal(record!.Id, retained.Id); // 未保存批注记录保留并按坐标重投影
        Assert.NotEqual(generationBefore, service.GetDocumentGeneration(vm.DocumentPath!)); // 句柄换代
    }

    [Fact]
    public void DocumentScope_DrainsBeforeClose()
    {
        using var service = new PdfiumRenderService();
        var docId = service.OpenDocument(Corpus.Mixed100);

        var scope = service.TryOpenDocumentScope(docId);
        Assert.NotNull(scope);
        Assert.Equal(1, service.GetInFlightCount(docId));

        using var closed = new ManualResetEventSlim(false);
        var closeTask = Task.Run(() =>
        {
            service.CloseDocument(docId);
            closed.Set();
        });

        Assert.False(closed.Wait(TimeSpan.FromMilliseconds(250)), "作用域未释放前关闭应阻塞");
        scope!.Dispose();
        Assert.True(closed.Wait(TimeSpan.FromSeconds(5)));
    }
}
