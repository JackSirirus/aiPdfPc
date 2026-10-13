// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T9 批注属性面板视图模型：选中批注后持续可调颜色 / 线宽 / 不透明度
// （对标 Foxit 的右侧 Format 面板；此前只有一次性浮菜单）。
//
// 改动经 <see cref="Changed"/> 交回宿主，由宿主走**既有保存链路**（记录替换 → 防抖 + 快速通道
// → 引擎 Delete+Add），因此不新增任何引擎动作类型。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AIPDF.Windows.Annotations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>回复请求（V4 T10）：宿主据此创建回复记录并走既有保存链路。</summary>
public sealed record AnnotationReplyRequest(string ParentId, string Text);

/// <summary>批注属性（颜色 / 线宽 / 不透明度）。</summary>
public sealed partial class AnnotationPropertiesViewModel : ObservableObject
{
    /// <summary>可选的描边/填充色板（与既有悬停改色同一组，保持一致）。</summary>
    public static IReadOnlyList<string> Palette { get; } =
        ["#FFFF00", "#A6E22E", "#5AC8FA", "#FF9EC4", "#E11D48", "#000000"];

    /// <summary>当前选中的批注（null = 无选中）。</summary>
    [ObservableProperty]
    private AnnotationRecord? _record;

    /// <summary>用户主动关闭面板后不再自动弹出（会话内记忆）。</summary>
    [ObservableProperty]
    private bool _isSuppressed;

    /// <summary>是否有选中批注。</summary>
    public bool HasSelection => Record is not null;

    /// <summary>是否显示线宽（仅手写 / 形状有效：引擎的 add_ink_annot / add_shape_annot 接收 width）。</summary>
    public bool SupportsLineWidth => Record?.Kind is AnnotationKind.Ink or AnnotationKind.Shape;

    /// <summary>是否显示不透明度（仅文本标记有效：引擎 add_text_markup_annot 接收 opacity）。</summary>
    public bool SupportsOpacity
        => Record?.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.Strikeout;

    /// <summary>面板是否可见（有选中且未被用户关闭）。</summary>
    public bool IsVisible => HasSelection && !IsSuppressed;

    /// <summary>当前颜色（无选中时为 null）。</summary>
    public string? Color => Record?.Color;

    /// <summary>当前线宽（无选中时为默认值）。</summary>
    public double LineWidth => Record?.LineWidth ?? AnnotationRecord.DefaultLineWidth;

    /// <summary>当前不透明度（无选中时为 1.0）。</summary>
    public double Opacity => Record?.Opacity ?? 1.0;

    /// <summary>属性已变更（宿主据此写回记录并走保存链路）。</summary>
    public event EventHandler<AnnotationRecord>? Changed;

    // ---- V4 T10：回复线程 ----

    /// <summary>当前选中批注的回复线程（按父 Id 过滤；保持宿主传入顺序）。</summary>
    public ObservableCollection<AnnotationRecord> Replies { get; } = [];

    /// <summary>是否有回复（UI 据此显示线程区）。</summary>
    public bool HasReplies => Replies.Count > 0;

    /// <summary>回复请求（宿主据此创建记录）。</summary>
    public event EventHandler<AnnotationReplyRequest>? ReplyRequested;

    /// <summary>V4 T10：由宿主刷新选中项的回复线程（选中变化、保存/读回后调用）。</summary>
    public void SetReplies(IEnumerable<AnnotationRecord>? replies)
    {
        Replies.Clear();
        foreach (var reply in replies ?? [])
        {
            Replies.Add(reply);
        }

        OnPropertyChanged(nameof(HasReplies));
    }

    /// <summary>V4 T10：提交一条回复（空内容/无选中忽略）。</summary>
    public void RequestReply(string? text)
    {
        if (Record is not { } record || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ReplyRequested?.Invoke(this, new AnnotationReplyRequest(record.Id, text.Trim()));
    }

    /// <summary>选中/取消选中某条批注（宿主在悬停命中变化时调用）。</summary>
    public void Select(AnnotationRecord? record) => Record = record;

    /// <summary>改色（FR-13）。</summary>
    public void ApplyColor(string hex)
    {
        if (Record is { } record && !string.IsNullOrWhiteSpace(hex))
        {
            Apply(record.WithColor(hex));
        }
    }

    /// <summary>改线宽（仅手写 / 形状有效）。</summary>
    public void ApplyLineWidth(double width)
    {
        if (Record is { } record)
        {
            Apply(record.WithLineWidth(width));
        }
    }

    /// <summary>改不透明度（仅文本标记有效）。</summary>
    public void ApplyOpacity(double opacity)
    {
        if (Record is { } record)
        {
            Apply(record.WithOpacity(opacity));
        }
    }

    /// <summary>用户关闭面板（会话内记忆，不再自动弹出）。</summary>
    public void Dismiss()
    {
        IsSuppressed = true;
        Record = null;
    }

    /// <summary>重新允许自动弹出（宿主在「显示属性」入口里调用）。</summary>
    public void AllowAutoOpen() => IsSuppressed = false;

    private void Apply(AnnotationRecord updated)
    {
        Record = updated;
        Changed?.Invoke(this, updated);
    }

    partial void OnRecordChanged(AnnotationRecord? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(SupportsLineWidth));
        OnPropertyChanged(nameof(SupportsOpacity));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(LineWidth));
        OnPropertyChanged(nameof(Opacity));
    }

    partial void OnIsSuppressedChanged(bool value) => OnPropertyChanged(nameof(IsVisible));
}
