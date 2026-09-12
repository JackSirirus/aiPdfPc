// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T6 打印进度视图模型（状态栏；FR-30 状态转录表：打印进度可取消）。
// 仅承载阶段① 预渲染的进度文案与活动态；取消令牌由宿主持有（本 VM 不直接取消）。
// 线程纪律（F3-BLK-1）：进度经 UI 线程创建的 Progress<T> 回封送后调用本 VM；
// 本 VM 不做线程切换、不触碰渲染/IO，可在无桌面会话下直测。
using AIPDF.Windows.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIPDF.Windows.ViewModels;

/// <summary>
/// 状态栏打印进度状态机：<see cref="Begin"/> → <see cref="Report"/>（可多次）→ <see cref="End"/>。
/// <see cref="IsActive"/> 控制状态栏进度区显隐，<see cref="Text"/> 为中文进度文案。
/// </summary>
public partial class PrintProgressViewModel : ObservableObject
{
    /// <summary>是否正在准备打印（状态栏进度区可见性）。</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>进度文案（如「正在准备打印 3 / 12 页…」）。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>本次作业选定页数（下限 1，避免除零/空文案）。</summary>
    private int _totalPages = 1;

    /// <summary>开始一次打印准备；<paramref name="totalPages"/> 为选定页数。</summary>
    public void Begin(int totalPages)
    {
        _totalPages = Math.Max(1, totalPages);
        Text = Format(0);
        IsActive = true;
    }

    /// <summary>进度上报（生产在 UI 线程调用）；未处于活动态时忽略迟到的回调。</summary>
    public void Report(PrintProgress progress)
    {
        if (!IsActive) return;
        Text = Format(Math.Clamp(progress.CompletedPages, 0, _totalPages));
    }

    /// <summary>结束一次打印准备（成功 / 取消 / 失败统一隐藏进度区并清空文案）。</summary>
    public void End()
    {
        IsActive = false;
        Text = string.Empty;
    }

    private string Format(int completedPages)
        => $"正在准备打印 {completedPages} / {_totalPages} 页…";
}
