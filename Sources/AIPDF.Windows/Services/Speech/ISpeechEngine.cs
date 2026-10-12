// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T8 朗读（TTS）契约：把「系统语音合成」抽象成可替换接口，便于纯单测
// （真实 System.Speech 需要音频设备与已安装语音，且不能作为 CI 依赖）。
using System;
using System.Collections.Generic;

namespace AIPDF.Windows.Services.Speech;

/// <summary>朗读状态。</summary>
public enum SpeechState
{
    /// <summary>未朗读。</summary>
    Idle,

    /// <summary>朗读中。</summary>
    Speaking,

    /// <summary>已暂停（可继续）。</summary>
    Paused,
}

/// <summary>朗读进度（字符区间，索引基于**喂入该页的同一字符串**）。</summary>
public sealed class SpeechProgressEventArgs(int characterPosition, int characterCount, string text) : EventArgs
{
    /// <summary>字符起始位置（0 基，喂入字符串内偏移）。</summary>
    public int CharacterPosition { get; } = characterPosition;

    /// <summary>字符数（词/句粒度）。</summary>
    public int CharacterCount { get; } = characterCount;

    /// <summary>本次朗读的文本片段。</summary>
    public string Text { get; } = text;
}

/// <summary>系统语音合成引擎（可替换，便于单测与无语音环境降级）。</summary>
public interface ISpeechEngine : IDisposable
{
    /// <summary>是否可用（有已启用的语音）。false 时宿主必须禁用朗读入口并给出提示，不得静默无声。</summary>
    bool IsAvailable { get; }

    /// <summary>已启用的语音名列表。</summary>
    IReadOnlyList<string> Voices { get; }

    /// <summary>当前语音（null = 系统默认）。</summary>
    string? SelectedVoice { get; set; }

    /// <summary>语速（-10 ~ 10，0 为正常）。</summary>
    int Rate { get; set; }

    /// <summary>朗读进度（词/句粒度）。</summary>
    event EventHandler<SpeechProgressEventArgs>? Progress;

    /// <summary>一段文本朗读完成（含被取消——调用方需自行区分主动停止）。</summary>
    event EventHandler? Finished;

    /// <summary>异步朗读一段文本（会取消上一段）。</summary>
    void SpeakAsync(string text);

    /// <summary>暂停。</summary>
    void Pause();

    /// <summary>继续。</summary>
    void Resume();

    /// <summary>停止并取消。</summary>
    void Stop();
}
