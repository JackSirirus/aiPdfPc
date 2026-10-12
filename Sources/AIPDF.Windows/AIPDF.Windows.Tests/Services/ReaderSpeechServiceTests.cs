// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T8 朗读服务测试：逐页推进、空页跳过、进度转发、停止不误报完成、暂停/继续。
// 用假引擎（真实 System.Speech 需要音频设备与已装语音，不适合作为自动化依赖）。
using System;
using System.Collections.Generic;
using AIPDF.Windows.Services.Speech;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

/// <summary>朗读服务（纯逻辑，无音频）。</summary>
public sealed class ReaderSpeechServiceTests
{
    private sealed class FakeEngine : ISpeechEngine
    {
        public List<string> Spoken { get; } = [];

        public bool IsAvailable { get; set; } = true;

        public IReadOnlyList<string> Voices { get; set; } = ["Microsoft Huihui Desktop"];

        public string? SelectedVoice { get; set; }

        public int Rate { get; set; }

        public bool Paused { get; private set; }

        public int StopCount { get; private set; }

        public event EventHandler<SpeechProgressEventArgs>? Progress;

        public event EventHandler? Finished;

        public void SpeakAsync(string text) => Spoken.Add(text);

        public void Pause() => Paused = true;

        public void Resume() => Paused = false;

        /// <summary>模拟真实引擎：取消同样会触发 SpeakCompleted。</summary>
        public void Stop()
        {
            StopCount++;
            Finished?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseProgress(int position, int count, string text)
            => Progress?.Invoke(this, new SpeechProgressEventArgs(position, count, text));

        public void RaiseFinished() => Finished?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private static (ReaderSpeechService Service, FakeEngine Engine) Build()
    {
        var engine = new FakeEngine();
        return (new ReaderSpeechService(engine), engine);
    }

    [Fact]
    public void Start_SpeaksFromGivenPage_AndSkipsEmptyPages()
    {
        var (service, engine) = Build();
        using (service)
        {
            var pages = new[] { "第一页正文", "   ", "", "第四页正文" };

            Assert.True(service.Start(pages, 0));
            Assert.Equal(SpeechState.Speaking, service.State);
            Assert.Equal(0, service.CurrentPageIndex);
            Assert.Equal(["第一页正文"], engine.Spoken);

            // 读完第 0 页 → 跳过 1/2 两个空页 → 直接读第 3 页
            engine.RaiseFinished();
            Assert.Equal(3, service.CurrentPageIndex);
            Assert.Equal(["第一页正文", "第四页正文"], engine.Spoken);
        }
    }

    [Fact]
    public void Start_RespectsStartPage()
    {
        var (service, engine) = Build();
        using (service)
        {
            Assert.True(service.Start(["甲", "乙", "丙"], 1));
            Assert.Equal(1, service.CurrentPageIndex);
            Assert.Equal(["乙"], engine.Spoken);
        }
    }

    [Fact]
    public void Start_ReturnsFalse_WhenEngineUnavailable_OrNoSpeakableText()
    {
        var (service, engine) = Build();
        using (service)
        {
            engine.IsAvailable = false;
            Assert.False(service.Start(["正文"], 0));
            Assert.Equal(SpeechState.Idle, service.State);
            Assert.Empty(engine.Spoken);

            engine.IsAvailable = true;
            Assert.False(service.Start(["  ", ""], 0));   // 全空文本：不得静默开始
            Assert.Equal(SpeechState.Idle, service.State);
        }
    }

    [Fact]
    public void Progress_IsForwarded_AndRecorded()
    {
        var (service, engine) = Build();
        using (service)
        {
            SpeechProgressEventArgs? seen = null;
            service.Progress += (_, e) => seen = e;

            service.Start(["测试一二三。Hello world 456."], 0);
            engine.RaiseProgress(2, 3, "一二三");

            Assert.NotNull(seen);
            Assert.Equal(2, seen!.CharacterPosition);
            Assert.Equal(3, seen.CharacterCount);
            Assert.Equal("一二三", seen.Text);
            Assert.Same(seen, service.LastProgress);
        }
    }

    [Fact]
    public void Finished_AdvancesThroughPages_ThenRaisesFinishedOnce()
    {
        var (service, engine) = Build();
        using (service)
        {
            var finished = 0;
            service.Finished += (_, _) => finished++;

            service.Start(["甲", "乙"], 0);
            engine.RaiseFinished();                       // 第 0 页读完 → 第 1 页
            Assert.Equal(1, service.CurrentPageIndex);
            Assert.Equal(SpeechState.Speaking, service.State);
            Assert.Equal(0, finished);

            engine.RaiseFinished();                       // 第 1 页读完 → 整篇结束
            Assert.Equal(SpeechState.Idle, service.State);
            Assert.Equal(1, finished);
            Assert.Equal(-1, service.CurrentPageIndex);
            Assert.Null(service.LastProgress);
        }
    }

    [Fact]
    public void Stop_DoesNotReportFinished_EvenThoughEngineRaisesIt()
    {
        var (service, engine) = Build();
        using (service)
        {
            var finished = 0;
            service.Finished += (_, _) => finished++;

            service.Start(["甲", "乙"], 0);
            service.Stop();

            Assert.Equal(SpeechState.Idle, service.State);
            Assert.Equal(0, finished);                    // 主动停止不得误报「读完了」
            Assert.Equal(1, engine.StopCount);
            Assert.Equal(-1, service.CurrentPageIndex);
        }
    }

    [Fact]
    public void Progress_AfterStop_IsIgnored()
    {
        var (service, engine) = Build();
        using (service)
        {
            var progress = 0;
            service.Progress += (_, _) => progress++;

            service.Start(["甲"], 0);
            service.Stop();
            engine.RaiseProgress(0, 1, "甲");             // 停止后到达的尾部事件

            Assert.Equal(0, progress);
            Assert.Null(service.LastProgress);
        }
    }

    [Fact]
    public void PauseResume_TogglesState_AndIgnoresInvalidTransitions()
    {
        var (service, engine) = Build();
        using (service)
        {
            var changes = 0;
            service.StateChanged += (_, _) => changes++;

            service.Resume();                             // Idle 下无效
            Assert.Equal(SpeechState.Idle, service.State);

            service.Start(["甲"], 0);
            service.Pause();
            Assert.Equal(SpeechState.Paused, service.State);
            Assert.True(engine.Paused);

            service.Resume();
            Assert.Equal(SpeechState.Speaking, service.State);
            Assert.False(engine.Paused);

            service.Stop();
            Assert.Equal(SpeechState.Idle, service.State);
            Assert.Equal(4, changes);                     // Speaking/Paused/Speaking/Idle
        }
    }

    [Fact]
    public void VoiceAndRate_AreForwardedToEngine()
    {
        var (service, engine) = Build();
        using (service)
        {
            service.Voice = "Microsoft Zira Desktop";
            service.Rate = 3;

            Assert.Equal("Microsoft Zira Desktop", engine.SelectedVoice);
            Assert.Equal("Microsoft Zira Desktop", service.Voice);
            Assert.Equal(3, service.Rate);
            Assert.Equal(["Microsoft Huihui Desktop"], service.Voices);
        }
    }
}
