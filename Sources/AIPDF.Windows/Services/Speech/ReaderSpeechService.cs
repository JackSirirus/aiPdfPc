// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T8 朗读服务：逐页朗读 + 进度回调（供页内高亮与自动滚动）+ 暂停/继续/停止/语速/语音。
//
// 逐页而非整篇：一是可让高亮跟随当前页，二是空文本页（扫描件）可跳过并给出明确提示，
// 三是长文档不必一次性把全文交给合成器。
using System;
using System.Collections.Generic;

namespace AIPDF.Windows.Services.Speech;

/// <summary>朗读服务（与 UI 无关，便于纯单测）。</summary>
public sealed class ReaderSpeechService : IDisposable
{
    private readonly ISpeechEngine _engine;
    private IReadOnlyList<string> _pages = [];
    private int _pageIndex = -1;
    private bool _stopping;

    public ReaderSpeechService(ISpeechEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.Progress += OnEngineProgress;
        _engine.Finished += OnEngineFinished;
    }

    /// <summary>是否可用（无已启用语音时为 false——宿主必须禁用入口并提示，不得静默无声）。</summary>
    public bool IsAvailable => _engine.IsAvailable;

    /// <summary>已启用语音列表。</summary>
    public IReadOnlyList<string> Voices => _engine.Voices;

    /// <summary>当前语音（null = 系统默认）。</summary>
    public string? Voice
    {
        get => _engine.SelectedVoice;
        set => _engine.SelectedVoice = value;
    }

    /// <summary>语速（-10 ~ 10）。</summary>
    public int Rate
    {
        get => _engine.Rate;
        set => _engine.Rate = value;
    }

    /// <summary>当前朗读状态。</summary>
    public SpeechState State { get; private set; } = SpeechState.Idle;

    /// <summary>当前朗读页（0 基；Idle 时为 -1）。</summary>
    public int CurrentPageIndex => _pageIndex;

    /// <summary>当前页的字符进度（0 基，基于喂入该页的字符串）。</summary>
    public SpeechProgressEventArgs? LastProgress { get; private set; }

    /// <summary>字符级进度（宿主据此做页内高亮与自动滚动）。</summary>
    public event EventHandler<SpeechProgressEventArgs>? Progress;

    /// <summary>整篇朗读结束（自然结束；主动 Stop 不触发）。</summary>
    public event EventHandler? Finished;

    /// <summary>状态变化（宿主据此更新按钮可用性）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 从 <paramref name="startPageIndex"/>（0 基）开始逐页朗读。
    /// 返回 false 表示没有可朗读的文本（引擎不可用，或从该页起全为空文本）。
    /// </summary>
    public bool Start(IReadOnlyList<string> pageTexts, int startPageIndex)
    {
        if (!_engine.IsAvailable)
        {
            return false;
        }

        _pages = pageTexts ?? [];
        var page = FindNextSpeakable(Math.Max(0, startPageIndex));
        if (page < 0)
        {
            return false;
        }

        SpeakPage(page);
        return true;
    }

    /// <summary>暂停（仅朗读中有效）。</summary>
    public void Pause()
    {
        if (State != SpeechState.Speaking)
        {
            return;
        }

        _engine.Pause();
        SetState(SpeechState.Paused);
    }

    /// <summary>继续（仅暂停态有效）。</summary>
    public void Resume()
    {
        if (State != SpeechState.Paused)
        {
            return;
        }

        _engine.Resume();
        SetState(SpeechState.Speaking);
    }

    /// <summary>停止（幂等；不触发 <see cref="Finished"/>）。</summary>
    public void Stop()
    {
        if (State == SpeechState.Idle)
        {
            return;
        }

        // 引擎取消会同步触发 Finished：用标志区分「主动停止」与「自然读完」。
        _stopping = true;
        _engine.Stop();
        _stopping = false;

        _pages = [];
        _pageIndex = -1;
        LastProgress = null;
        SetState(SpeechState.Idle);
    }

    private void SpeakPage(int pageIndex)
    {
        _pageIndex = pageIndex;
        LastProgress = null;
        _engine.SpeakAsync(_pages[pageIndex]);
        SetState(SpeechState.Speaking);
    }

    private void OnEngineProgress(object? sender, SpeechProgressEventArgs e)
    {
        if (State == SpeechState.Idle)
        {
            return; // 停止后到达的尾部事件：忽略
        }

        LastProgress = e;
        Progress?.Invoke(this, e);
    }

    private void OnEngineFinished(object? sender, EventArgs e)
    {
        if (_stopping || State == SpeechState.Idle)
        {
            return;
        }

        var next = FindNextSpeakable(_pageIndex + 1);
        if (next < 0)
        {
            _pages = [];
            _pageIndex = -1;
            LastProgress = null;
            SetState(SpeechState.Idle);
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        SpeakPage(next);
    }

    /// <summary>从 <paramref name="from"/> 起找第一个非空白页；找不到返回 -1。</summary>
    private int FindNextSpeakable(int from)
    {
        for (var index = Math.Max(0, from); index < _pages.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(_pages[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private void SetState(SpeechState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _engine.Progress -= OnEngineProgress;
        _engine.Finished -= OnEngineFinished;
        _engine.Dispose();
    }
}
