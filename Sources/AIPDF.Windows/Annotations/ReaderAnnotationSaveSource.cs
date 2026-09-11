// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4：ReaderViewModel → IAnnotationSaveSource 适配器。
// 保存服务经此拉取未保存记录、应用保存结果；重载由宿主的 close-first 协调器负责（不在此处）。
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Annotations;

/// <summary>把阅读视图模型适配为批注保存来源（每个文档标签一个实例）。</summary>
public sealed class ReaderAnnotationSaveSource : IAnnotationSaveSource
{
    private readonly ReaderViewModel _viewModel;

    public ReaderAnnotationSaveSource(ReaderViewModel viewModel, string filePath, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _viewModel = viewModel;
        FilePath = System.IO.Path.GetFullPath(filePath);
        Password = password;
    }

    public string FilePath { get; }

    public string? Password { get; }

    /// <summary>只读判定交给服务（查文件属性）；适配器不额外声明。</summary>
    public bool IsReadOnly => false;

    public int CurrentPageNumber => _viewModel.CurrentPageNumber;

    public double CurrentZoomPercent => _viewModel.ZoomPercent;

    public IReadOnlyList<AnnotationRecord> GetDirtyRecords() => _viewModel.GetAllRecords();

    public void ApplySaveSuccess(IReadOnlyList<string> clearedIds, IReadOnlyList<AnnotationRecord> retainedRecords)
        => _viewModel.RemoveAnnotationsById(clearedIds);

    public void ApplySaveFailure(IReadOnlyList<AnnotationRecord> submittedRecords)
    {
        // 失败保留叠加层与记录（Must-NOT：不清空失败叠加层）。
        // 记录状态可选转 Failed，但 ContentEquals 忽略 State，故不改写记录本身。
    }
}
