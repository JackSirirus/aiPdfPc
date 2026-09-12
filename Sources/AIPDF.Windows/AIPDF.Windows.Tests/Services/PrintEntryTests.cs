// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T6 打印入口门禁 / 范围映射测试（纯逻辑，不依赖真实打印机或桌面会话）：
//   - 门禁输入解析复用 T3 EvaluateGate（AC-30.2：未加载 / 加密未解锁 → 不可打印）；
//   - 系统 PrintDialog 范围选择 → T3 PrintPageRange 映射（全部 / 当前页 / 自定义）。
// 真实对话框与出纸属集成路径，交 F3 桌面 QA（不做假）。
using System.IO;
using System.Windows.Controls;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class PrintEntryTests : IDisposable
{
    private readonly string _work;
    private readonly string _settingsPath;
    private readonly string _docPath;

    public PrintEntryTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "aipdf-v3t6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        _settingsPath = Path.Combine(_work, "settings.json");
        _docPath = Path.Combine(_work, "doc.pdf"); // 仅作路径标识；门禁不读取文件
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private PrintService CreateService(SessionCredentialStore? credentials = null)
        => new(new SettingsStore(_settingsPath), credentials);

    // ---------------------------------------------------------- 门禁（AC-30.2）

    [Fact]
    public void EvaluateGate_NoActiveDocument_IsNotLoaded()
    {
        var decision = PrintEntry.EvaluateGate(CreateService(), null, readerHasDocument: true, isDocumentEncrypted: false);
        Assert.False(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.NotLoaded, decision.Status);
    }

    [Fact]
    public void EvaluateGate_LoadingInProgress_IsNotLoaded()
    {
        // 文档路径已存在但阅读实例尚未完成加载（加载中）→ 不可打印。
        var decision = PrintEntry.EvaluateGate(CreateService(), _docPath, readerHasDocument: false, isDocumentEncrypted: false);
        Assert.False(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.NotLoaded, decision.Status);
    }

    [Fact]
    public void EvaluateGate_LoadedPlainDocument_IsPrintable()
    {
        var decision = PrintEntry.EvaluateGate(CreateService(), _docPath, readerHasDocument: true, isDocumentEncrypted: false);
        Assert.True(decision.IsPrintable);
    }

    [Fact]
    public void EvaluateGate_EncryptedUnlocked_IsPrintable()
    {
        // V4 Option 3：加密信号来自阅读实例真实 IsEncrypted；解锁后会话驻留密码 → 仍可打印。
        var credentials = new SessionCredentialStore();
        credentials.Store(_docPath, "SECRET-123");
        var decision = PrintEntry.EvaluateGate(CreateService(credentials), _docPath, readerHasDocument: true, isDocumentEncrypted: true);
        Assert.True(decision.IsPrintable);
    }

    [Fact]
    public void EvaluateGate_EncryptedLocked_IsLocked()
    {
        // V4 Option 3：加密且锁定（会话无密码）→ Locked（Ctrl+P 禁用）；这是锁定标签的可观测门禁。
        var decision = PrintEntry.EvaluateGate(
            CreateService(new SessionCredentialStore()), _docPath, readerHasDocument: true, isDocumentEncrypted: true);
        Assert.False(decision.IsPrintable);
        Assert.Equal(PrintGateStatus.Locked, decision.Status);
    }

    // ---------------------------------------------------------- 对话框范围映射

    [Fact]
    public void ResolveRange_AllPages_ReturnsFullSequence()
    {
        var result = PrintEntry.ResolveRange(PageRangeSelection.AllPages, 0, 0, pageCount: 5, currentPageIndex: 2);
        Assert.True(result.IsValid);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Pages);
    }

    [Fact]
    public void ResolveRange_CurrentPage_MapsOneBasedToZeroBased()
    {
        var result = PrintEntry.ResolveRange(PageRangeSelection.CurrentPage, 0, 0, pageCount: 5, currentPageIndex: 3);
        Assert.True(result.IsValid);
        Assert.Equal(new[] { 3 }, result.Pages);
    }

    [Fact]
    public void ResolveRange_UserPages_MapsInclusiveRange()
    {
        var result = PrintEntry.ResolveRange(PageRangeSelection.UserPages, 2, 4, pageCount: 5, currentPageIndex: 0);
        Assert.True(result.IsValid);
        Assert.Equal(new[] { 1, 2, 3 }, result.Pages);
    }

    [Fact]
    public void ResolveRange_UserPagesOutOfRange_IsInvalid()
    {
        var result = PrintEntry.ResolveRange(PageRangeSelection.UserPages, 1, 99, pageCount: 5, currentPageIndex: 0);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ResolveRange_NoDocument_IsInvalid()
    {
        var result = PrintEntry.ResolveRange(PageRangeSelection.AllPages, 0, 0, pageCount: 0, currentPageIndex: 0);
        Assert.False(result.IsValid);
    }
}
