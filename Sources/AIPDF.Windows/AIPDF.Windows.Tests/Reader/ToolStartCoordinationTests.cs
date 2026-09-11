// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T10 批注 × 工具协作测试：FR-21 flush 决策（成功启动/失败阻止/强制继续）、
// FR-22 redact 警告触发条件、编辑组（不经队列）启动门禁。
using System.IO;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class ToolStartCoordinationTests
{
    // ---------------------------------------------------------------- FR-21 flush 决策

    [Fact]
    public void FlushDecision_NoUnsaved_StartsWithoutFlush()
        => Assert.Equal(ToolStartGateDecision.Start,
            ToolStartFlushLogic.Decide(hasUnsavedAnnotations: false, flushSucceeded: false, forceContinue: false));

    [Fact]
    public void FlushDecision_Success_Starts()
        => Assert.Equal(ToolStartGateDecision.Start,
            ToolStartFlushLogic.Decide(hasUnsavedAnnotations: true, flushSucceeded: true, forceContinue: false));

    [Fact]
    public void FlushDecision_Failure_Blocks()
        => Assert.Equal(ToolStartGateDecision.Blocked,
            ToolStartFlushLogic.Decide(hasUnsavedAnnotations: true, flushSucceeded: false, forceContinue: false));

    [Fact]
    public void FlushDecision_ForceContinue_Starts()
        => Assert.Equal(ToolStartGateDecision.Start,
            ToolStartFlushLogic.Decide(hasUnsavedAnnotations: true, flushSucceeded: false, forceContinue: true));

    // ---------------------------------------------------------------- FR-22 redact 警告触发条件

    [Fact]
    public void RedactWarning_SavedAnnotations_Triggers()
        => Assert.True(RedactWarningLogic.ShouldWarn(fileAnnotationCount: 3, unsavedAnnotationCount: 0));

    [Fact]
    public void RedactWarning_UnsavedAnnotationsOnly_Triggers()
        => Assert.True(RedactWarningLogic.ShouldWarn(fileAnnotationCount: 0, unsavedAnnotationCount: 1));

    [Fact]
    public void RedactWarning_NoAnnotations_DoesNotTrigger()
        => Assert.False(RedactWarningLogic.ShouldWarn(fileAnnotationCount: 0, unsavedAnnotationCount: 0));

    // ---------------------------------------------------------------- 编辑组启动门禁（不经 T7 队列）

    [Fact]
    public async Task EditorStartGate_Blocked_SkipsEngineAndSetsError()
    {
        using var workspace = new TempDirectory();
        var file = Path.Combine(workspace.Path, "样例.pdf");
        File.WriteAllBytes(file, [0x25, 0x50, 0x44, 0x46]); // 预检只查存在性

        var ran = false;
        var viewModel = new RotateViewModel(EditorTestHost.Tool("rotate"),
            (_, _, _, _) => { ran = true; return Task.CompletedTask; })
        {
            InputFile = file,
            OutputDir = workspace.Path,
            StartGate = _ => Task.FromResult(false),
        };

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.False(ran);
        Assert.Equal("批注保存失败，工具未启动。", viewModel.ErrorText);
    }

    [Fact]
    public async Task EditorStartGate_Allowed_RunsEngine()
    {
        using var workspace = new TempDirectory();
        var file = Path.Combine(workspace.Path, "样例.pdf");
        File.WriteAllBytes(file, [0x25, 0x50, 0x44, 0x46]);

        var ran = false;
        var viewModel = new RotateViewModel(EditorTestHost.Tool("rotate"),
            (_, _, _, _) => { ran = true; return Task.CompletedTask; })
        {
            InputFile = file,
            OutputDir = workspace.Path,
            StartGate = _ => Task.FromResult(true),
        };

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(ran);
        Assert.Equal("", viewModel.ErrorText);
    }

    /// <summary>测试用临时目录：释放时递归删除。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipdf-t10-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 句柄未完全释放时容忍残留，由系统临时目录清理兜底
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
