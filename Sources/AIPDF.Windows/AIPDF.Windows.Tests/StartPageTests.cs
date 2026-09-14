// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 起始页（T6）测试：最近文件存储读写（临时目录）、列表增删/去重/20 条上限【D15】/失效标注【ST-05】/
/// 清空二次确认，以及常用工具 8 项【R19】与 33 工具完整入口的目录消费。
/// </summary>
public sealed class StartPageTests
{
    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "aipdf-startpage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string TempPdf(string dir, string name) => Path.Combine(dir, name);

    private static IReadOnlyList<Tool> RealCatalog() => ToolCatalog.Load();

    // ======================= 存储层（RecentFilesStore） =======================

    [Fact]
    public void Store_Roundtrip_PreservesEntries_Utf8NoBom()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            var document = new RecentFilesDocument();
            document.Items.Add(new RecentFileEntry { Path = @"D:\docs\中文 报告.pdf", Name = "中文 报告.pdf", LastOpenedAt = new DateTime(2026, 9, 11, 8, 30, 0) });
            document.Items.Add(new RecentFileEntry { Path = @"D:\docs\spec.pdf", Name = "spec.pdf", LastOpenedAt = new DateTime(2026, 9, 10, 22, 0, 0) });

            store.Save(document);

            // UTF-8 无 BOM（沿袭 WorkflowPresetStore 范式）
            var bytes = File.ReadAllBytes(store.FilePath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            var loaded = new RecentFilesStore(dir).Load();
            Assert.Equal(2, loaded.Items.Count);
            Assert.Equal(@"D:\docs\中文 报告.pdf", loaded.Items[0].Path);
            Assert.Equal("中文 报告.pdf", loaded.Items[0].Name);
            Assert.Equal(new DateTime(2026, 9, 11, 8, 30, 0), loaded.Items[0].LastOpenedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_Load_MissingOrCorruptOrWrongSchema_ReturnsEmptyDocument()
    {
        var dir = NewTempDir();
        try
        {
            // 缺文件
            Assert.Empty(new RecentFilesStore(dir).Load().Items);

            // JSON 损坏
            File.WriteAllText(Path.Combine(dir, "recent-files.json"), "{ 不是 json");
            Assert.Empty(new RecentFilesStore(dir).Load().Items);

            // 版本不识别
            File.WriteAllText(Path.Combine(dir, "recent-files.json"), """{"schema":999,"items":[]}""");
            Assert.Empty(new RecentFilesStore(dir).Load().Items);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= 列表逻辑（StartPageViewModel） =======================

    private static StartPageViewModel NewVm(RecentFilesStore? store = null, IEnumerable<Tool>? catalog = null)
        => new(catalog ?? RealCatalog(), store);

    [Fact]
    public void RegisterOpened_NewPath_InsertsHead_WithFileName()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            vm.RegisterOpened(TempPdf(dir, "甲.pdf"));
            vm.RegisterOpened(TempPdf(dir, "乙.pdf"));

            Assert.Equal(2, vm.RecentFiles.Count);
            Assert.Equal(TempPdf(dir, "乙.pdf"), vm.RecentFiles[0].Path); // 最新在头部
            Assert.Equal("乙.pdf", vm.RecentFiles[0].Name);
            Assert.True(vm.HasRecentFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RegisterOpened_DuplicatePath_CaseInsensitive_MovesToHead_NoDuplicate()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            vm.RegisterOpened(TempPdf(dir, "甲.pdf"));
            vm.RegisterOpened(TempPdf(dir, "乙.pdf"));
            vm.RegisterOpened(TempPdf(dir, "甲.PDF")); // 大小写不同，同一文件【D14 语义】

            Assert.Equal(2, vm.RecentFiles.Count);
            Assert.Equal(TempPdf(dir, "甲.PDF"), vm.RecentFiles[0].Path); // 重复 → 上浮
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RegisterOpened_OverCap_EvictsOldest()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            for (var i = 1; i <= 25; i++)
            {
                vm.RegisterOpened(TempPdf(dir, $"f{i:00}.pdf")); // AC-34.1：25 个 → 保留最新 20
            }

            Assert.Equal(RecentFilesStore.DefaultMaxEntries, vm.RecentFiles.Count);
            Assert.Equal(TempPdf(dir, "f25.pdf"), vm.RecentFiles[0].Path);
            Assert.Equal(TempPdf(dir, "f06.pdf"), vm.RecentFiles[^1].Path); // 最旧 5 条被淘汰
            Assert.DoesNotContain(vm.RecentFiles, item => item.Name == "f01.pdf");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RegisterOpened_Persists_AndReloadRestores()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            vm.RegisterOpened(TempPdf(dir, "甲.pdf"));
            vm.RegisterOpened(TempPdf(dir, "乙.pdf"));

            var reloaded = NewVm(new RecentFilesStore(dir)); // 模拟重启
            Assert.Equal(2, reloaded.RecentFiles.Count);
            Assert.Equal(TempPdf(dir, "乙.pdf"), reloaded.RecentFiles[0].Path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ClearConfirmation_RequestConfirmClears_AndPersists_CancelKeeps()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            var vm = NewVm(store);
            vm.RegisterOpened(TempPdf(dir, "甲.pdf"));

            vm.RequestClear();
            Assert.True(vm.IsClearPending);

            vm.CancelClear(); // 取消：不清空
            Assert.False(vm.IsClearPending);
            Assert.Single(vm.RecentFiles);

            vm.RequestClear();
            vm.ConfirmClear();
            Assert.False(vm.IsClearPending);
            Assert.Empty(vm.RecentFiles);
            Assert.False(vm.HasRecentFiles); // 「最近打开」区块随之隐藏【ST-02】

            Assert.Empty(store.Load().Items); // 清空结果已持久化
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RequestClear_WithNoItems_DoesNotEnterPending()
    {
        // 隔离：不得依赖真实 %APPDATA%\AIPDF\recent-files.json（应用运行后会写入，导致环境相关失败）
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            vm.RequestClear();
            Assert.False(vm.IsClearPending);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RemoveRecent_RemovesEntry_AndPersists()
    {
        var dir = NewTempDir();
        try
        {
            var store = new RecentFilesStore(dir);
            var vm = NewVm(store);
            vm.RegisterOpened(TempPdf(dir, "甲.pdf"));
            vm.RegisterOpened(TempPdf(dir, "乙.pdf"));

            vm.RemoveRecent(vm.RecentFiles.First(item => item.Name == "甲.pdf"));

            Assert.Single(vm.RecentFiles);
            Assert.Equal("乙.pdf", vm.RecentFiles[0].Name);
            Assert.Single(store.Load().Items);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RefreshExistence_MissingMarkedNotRemoved()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            var kept = TempPdf(dir, "在.pdf");
            var gone = TempPdf(dir, "丢了.pdf");
            File.WriteAllBytes(kept, [1]); // 真实存在的文件
            vm.RegisterOpened(kept);
            vm.RegisterOpened(gone);
            File.Delete(gone); // 文件被移动/删除

            vm.RefreshExistence(); // PRD FR-34：缺失置灰标注，不自动删除

            Assert.True(vm.RecentFiles.First(item => item.Path == kept).Exists);
            Assert.False(vm.RecentFiles.First(item => item.Path == gone).Exists);
            Assert.Equal(2, vm.RecentFiles.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryOpenRecent_MissingFile_ShowsToast_NoOpenRequested()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            var gone = TempPdf(dir, "丢了.pdf");
            vm.RegisterOpened(gone);
            File.Delete(gone);

            string? requested = null;
            vm.DocumentOpenRequested += (_, path) => requested = path;

            Assert.False(vm.TryOpenRecent(vm.RecentFiles[0])); // 【ST-05】
            Assert.Null(requested);
            Assert.Contains("文件不存在", vm.ToastMessage);
            Assert.Single(vm.RecentFiles); // 不自动删除
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryOpenRecent_ExistingFile_MovesToHead_AndRequestsOpen()
    {
        var dir = NewTempDir();
        try
        {
            var vm = NewVm(new RecentFilesStore(dir));
            File.WriteAllBytes(TempPdf(dir, "甲.pdf"), [1]);
            vm.RegisterOpened(TempPdf(dir, "乙.pdf"));
            vm.RegisterOpened(TempPdf(dir, "甲.pdf"));

            string? requested = null;
            vm.DocumentOpenRequested += (_, path) => requested = path;

            vm.TryOpenRecent(vm.RecentFiles[0]);
            Assert.Equal(TempPdf(dir, "甲.pdf"), requested);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= 拖拽落区【ST-07】 =======================

    [Fact]
    public void HandleDroppedPaths_PdfOpens_NonPdfAndFolderToastRejected()
    {
        var dir = NewTempDir();
        try
        {
            var pdf = TempPdf(dir, "文档.pdf");
            File.WriteAllBytes(pdf, [1]);
            var vm = NewVm(new RecentFilesStore(dir));

            var opened = new List<string>();
            vm.DocumentOpenRequested += (_, path) => opened.Add(path);

            vm.HandleDroppedPaths([pdf, TempPdf(dir, "图片.png"), dir]);

            Assert.Single(opened); // 只有 PDF 进入打开流
            Assert.Equal(pdf, opened[0]);
            Assert.Contains("仅支持 PDF 文件", vm.ToastMessage); // 【ST-07】拒绝文案
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================= 工具入口（【R19】静态 8 项 + 33 工具完整入口） =======================

    [Fact]
    public void FavoriteTools_StaticEightIds_FromRealCatalog()
    {
        var vm = NewVm();

        var expected = new[]
        {
            ("merge", "合并 PDF"), ("compress", "压缩 PDF"), ("split", "拆分 PDF"), ("ocr", "OCR 识别"),
            ("pdf_to_word", "PDF 转 Word"), ("protect", "加密"), ("watermark", "水印"), ("workflow", "工作流程"),
        };
        Assert.Equal(expected.Length, vm.FavoriteTools.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Item1, vm.FavoriteTools[i].Id);
        }
    }

    [Fact]
    public void ToolGroups_ConsumeCatalog_SixGroups33Tools_ReadOnly()
    {
        var catalog = RealCatalog();
        var vm = NewVm(catalog: catalog);

        Assert.Equal(33, vm.Tools.Count);
        Assert.Equal(6, vm.ToolGroups.Count);
        Assert.Equal(catalog.Count, vm.ToolGroups.Sum(group => group.Tools.Count));

        // 组序与目录一致（分组来自数据，不在代码写死）
        var expectedOrder = new[] { "整理页面", "优化文件", "格式转换", "编辑内容", "安全保护", "工作流程" };
        var actualOrder = vm.ToolGroups.Select(group => group.Name).ToArray();
        Assert.Equal(expectedOrder, actualOrder);
    }

    [Fact]
    public void OpenTool_RaisesToolRequested()
    {
        var vm = NewVm();
        Tool? requested = null;
        vm.ToolRequested += (_, tool) => requested = tool;

        vm.OpenTool(vm.Tools.First(t => t.Id == "merge"));

        Assert.NotNull(requested);
        Assert.Equal("merge", requested!.Id);
    }

    [Fact]
    public void AllToolsToggleText_ReflectsExpansionState()
    {
        var vm = NewVm();
        Assert.Contains("33 个工具", vm.AllToolsToggleText);
        vm.IsAllToolsExpanded = true;
        Assert.Equal("收起全部工具", vm.AllToolsToggleText);
    }
}
