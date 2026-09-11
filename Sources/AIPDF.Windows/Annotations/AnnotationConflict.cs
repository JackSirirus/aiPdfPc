// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T11 外部修改冲突检测（PRD 3.5.6 FR-18 / R10）的纯逻辑契约。
//
// V2 判定口径（Q4 裁决）：只比对打开时记录的 size/mtime 基线，**不含 SHA-256 内容比对**。
// 已知限制（M-5）：同 size 同 mtime 的外部编辑无法检出（FAT/exFAT 2s 粒度、网络盘更不可靠）。
// 因此只承诺「显著外部修改可检出」，不承诺「不漏报」。
using AIPDF.Windows.Services;

namespace AIPDF.Windows.Annotations;

/// <summary>基线 → 当前的比对结论（供对话框文案与测试断言）。</summary>
public enum ExternalModificationKind
{
    /// <summary>size/mtime 均一致：无冲突。</summary>
    None,

    /// <summary>仅文件大小变化。</summary>
    SizeChanged,

    /// <summary>仅修改时间变化。</summary>
    ModifiedTimeChanged,

    /// <summary>大小与修改时间都变化。</summary>
    SizeAndModifiedTimeChanged,
}

/// <summary>
/// size/mtime 基线对比（纯函数，无 IO）。字节大小或最后写入时间任一不等即判定外部修改。
/// </summary>
public static class AnnotationConflictDetector
{
    /// <summary>对比基线 stamp 与当前 stamp 的 size/mtime（忽略键，仅用原始字段）。</summary>
    public static ExternalModificationKind Compare(FileStateStamp baseline, FileStateStamp current)
        => Compare(baseline.Size, baseline.ModifiedTicks, current.Size, current.ModifiedTicks);

    /// <summary>对比基线/当前的原始 size 与 mtime ticks。</summary>
    public static ExternalModificationKind Compare(
        long baselineSize, long baselineModifiedTicks, long currentSize, long currentModifiedTicks)
    {
        var sizeChanged = baselineSize != currentSize;
        var timeChanged = baselineModifiedTicks != currentModifiedTicks;
        return (sizeChanged, timeChanged) switch
        {
            (true, true) => ExternalModificationKind.SizeAndModifiedTimeChanged,
            (true, false) => ExternalModificationKind.SizeChanged,
            (false, true) => ExternalModificationKind.ModifiedTimeChanged,
            _ => ExternalModificationKind.None,
        };
    }
}

/// <summary>
/// 冲突上下文：打开/上次保存后的基线状态与检测时的当前文件状态。
/// <see cref="CurrentModified"/> 为「上次保存时间」文案的数据源（宿主格式化本地时区）。
/// </summary>
public sealed record AnnotationConflict(
    string Path,
    long BaselineSize,
    DateTimeOffset BaselineModified,
    long CurrentSize,
    DateTimeOffset CurrentModified,
    ExternalModificationKind Kind);

/// <summary>冲突对话框三选一（FR-18 AC-18.2）。</summary>
public enum ConflictChoice
{
    /// <summary>覆盖保存：继续原子就地写入，接受覆盖外部修改。</summary>
    Overwrite,

    /// <summary>另存为：写新路径、原文件不变，成功后状态/基线切到新路径。</summary>
    SaveAs,

    /// <summary>取消：不写盘，批注叠加层保留，可重试。</summary>
    Cancel,
}

/// <summary>冲突解决结果；<see cref="ConflictChoice.SaveAs"/> 必须携带目标路径。</summary>
public sealed record ConflictResolution(ConflictChoice Choice, string? SaveAsPath = null)
{
    public static ConflictResolution Overwrite { get; } = new(ConflictChoice.Overwrite);

    public static ConflictResolution Cancel { get; } = new(ConflictChoice.Cancel);

    public static ConflictResolution SaveAsTo(string path) => new(ConflictChoice.SaveAs, path);
}

/// <summary>
/// 宿主提供的保存交互（T11）：检测到外部修改时弹模态三选一对话框；
/// 目标被占用（T3 <c>target_in_use</c>）时请求「另存为」目标路径。
/// 实现必须在 UI 线程弹窗并阻塞保存提交（服务 await 本接口）。
/// </summary>
public interface IAnnotationSavePrompt
{
    /// <summary>外部修改冲突对话框（覆盖保存 / 另存为… / 取消）。</summary>
    Task<ConflictResolution> ResolveExternalModificationAsync(
        AnnotationConflict conflict, CancellationToken cancellationToken);

    /// <summary>目标被占用时的「另存为…/取消」；返回所选新路径，取消返回 null。</summary>
    Task<string?> RequestSaveAsPathAsync(string sourcePath, CancellationToken cancellationToken);
}
