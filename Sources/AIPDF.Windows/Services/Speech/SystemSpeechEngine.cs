// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T8 System.Speech 实现：系统本地语音合成（不联网、不上传文本）。
//
// S5 尖刺实测（.omo/evidence/v4-spike-tts.md）：
//   - System.Speech 8.0.0 在 .NET 8 与 --self-contained true 发布下均可用；
//   - 本机含 zh-CN（Microsoft Huihui Desktop）；
//   - SpeakProgress.CharacterPosition 是**喂入字符串内的 0 基偏移**（中文按词切分、标点跳过），
//     因此只要喂入的就是取矩形用的那个字符串，就能直接映射到页内矩形。
using System;
using System.Collections.Generic;
using System.Speech.Synthesis;

namespace AIPDF.Windows.Services.Speech;

/// <summary>基于 System.Speech 的语音引擎（构造失败即降级为「不可用」，不抛）。</summary>
public sealed class SystemSpeechEngine : ISpeechEngine
{
    private readonly SpeechSynthesizer? _synthesizer;
    private readonly List<string> _voices = [];

    public SystemSpeechEngine()
    {
        try
        {
            _synthesizer = new SpeechSynthesizer();
            foreach (var voice in _synthesizer.GetInstalledVoices())
            {
                if (voice.Enabled)
                {
                    _voices.Add(voice.VoiceInfo.Name);
                }
            }

            _synthesizer.SpeakProgress += (_, e) =>
                Progress?.Invoke(this, new SpeechProgressEventArgs(e.CharacterPosition, e.CharacterCount, e.Text));
            _synthesizer.SpeakCompleted += (_, _) => Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 无语音组件 / 平台不支持 / COM 初始化失败：降级为不可用（宿主据此禁用入口并提示）
            _synthesizer = null;
        }
    }

    public bool IsAvailable => _synthesizer is not null && _voices.Count > 0;

    public IReadOnlyList<string> Voices => _voices;

    public string? SelectedVoice { get; set; }

    public int Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, -10, 10);
            if (_synthesizer is not null)
            {
                try
                {
                    _synthesizer.Rate = _rate;
                }
                catch (Exception)
                {
                    // 语速设置失败不影响朗读
                }
            }
        }
    }

    public event EventHandler<SpeechProgressEventArgs>? Progress;

    public event EventHandler? Finished;

    private int _rate;

    public void SpeakAsync(string text)
    {
        if (_synthesizer is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (SelectedVoice is { Length: > 0 } voice)
        {
            try
            {
                _synthesizer.SelectVoice(voice);
            }
            catch (Exception)
            {
                // 语音不可用：退回系统默认
            }
        }

        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.SpeakAsync(text);
    }

    public void Pause()
    {
        try
        {
            _synthesizer?.Pause();
        }
        catch (Exception)
        {
            // 无朗读任务时 Pause 抛异常：忽略
        }
    }

    public void Resume()
    {
        try
        {
            _synthesizer?.Resume();
        }
        catch (Exception)
        {
            // 同上
        }
    }

    public void Stop()
    {
        try
        {
            _synthesizer?.SpeakAsyncCancelAll();
        }
        catch (Exception)
        {
            // 同上
        }
    }

    public void Dispose() => _synthesizer?.Dispose();
}
