// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T4：状态栏 savepill 视图模型（FR-19 四态；SVG 文案映射纯逻辑可测，不依赖 WPF 控件）。
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.Annotations;

/// <summary>
/// savepill 四态展示（已保存 / 保存中 / 保存失败·点击重试 / 只读）。
/// 状态变化由 <see cref="AnnotationSaveService.StateChanged"/> 驱动；重试经 <see cref="RetryRequested"/> 交宿主。
/// </summary>
public partial class SavePillViewModel : ObservableObject
{
    /// <summary>当前文档路径（null = 无非文档标签，pill 隐藏）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private string? _documentPath;

    /// <summary>当前保存状态。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    [NotifyPropertyChangedFor(nameof(IsRetryEnabled))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private SavePillState _state = SavePillState.Saved;

    /// <summary>连续失败建议另存为（FR-16）时置位，供宿主/提示条消费。</summary>
    [ObservableProperty]
    private bool _shouldSuggestSaveAs;

    /// <summary>最近一次失败诊断（tooltip 用；可为 null）。</summary>
    [ObservableProperty]
    private string? _errorDetail;

    /// <summary>仅当有激活文档标签时显示。</summary>
    public bool IsVisible => DocumentPath is not null;

    /// <summary>失败态可点击重试。</summary>
    public bool IsRetryEnabled => State == SavePillState.Failed;

    /// <summary>保存中（可用于禁用交互/动画）。</summary>
    public bool IsBusy => State == SavePillState.Saving;

    /// <summary>状态文案（ST-08/20/21/22）。</summary>
    public string Text => State switch
    {
        SavePillState.Saved => "已保存",
        SavePillState.Saving => "保存中…",
        SavePillState.Failed => "保存失败 · 点击重试",
        SavePillState.ReadOnly => "只读",
        _ => string.Empty,
    };

    /// <summary>失败 pill 被点击：宿主执行强制 flush 重试。</summary>
    public event EventHandler? RetryRequested;

    [RelayCommand]
    private void Retry() => RetryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>状态变更入口（宿主 marshal 回 UI 线程后调用）。</summary>
    public void Apply(SavePillState state, bool shouldSuggestSaveAs, string? errorDetail)
    {
        State = state;
        ShouldSuggestSaveAs = shouldSuggestSaveAs;
        ErrorDetail = errorDetail;
    }
}
