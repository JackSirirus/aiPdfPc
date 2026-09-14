// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T14 划词翻译视图模型（纯逻辑，无 WPF 依赖，xunit 直接覆盖）：
//   - 卡片状态机：Loading → Done / Error（选中后点击「译」才进入 Loading，绝不选中即译）；
//   - 卡片堆叠上限 20（新卡置顶，超限丢弃最旧）；
//   - 无文本层态（F1b）：本页无可选文字时显式进入该态并隐藏卡片，不会静默无反应。
using AIPDF.Windows.Rendering;

namespace AIPDF.Windows.ViewModels.Translation;

/// <summary>划词卡片状态。</summary>
public enum SelectionTranslationState
{
    /// <summary>请求进行中。</summary>
    Loading,

    /// <summary>已拿到译文。</summary>
    Done,

    /// <summary>请求失败（未配置 / 网络 / 拒译等）。</summary>
    Error,
}

/// <summary>模板代码 → 中文展示标签（与 backend/translate/prompt.py 的 PRESET_TEMPLATES 对齐）。</summary>
public static class TranslationLabels
{
    /// <summary>模板代码转展示名；未知代码原样返回。</summary>
    public static string Template(string? code) => code switch
    {
        "academic" => "学术",
        "legal" => "法律",
        "medical" => "医学",
        "technical" => "技术",
        "generic" or null or "" => "通用",
        _ => code,
    };

    /// <summary>语言代码转展示名；未知代码原样返回。</summary>
    public static string Language(string? code) => code switch
    {
        "auto" or null or "" => "自动检测",
        "zh" => "中文",
        "en" => "英语",
        "ja" => "日语",
        "ko" => "韩语",
        "fr" => "法语",
        "de" => "德语",
        "es" => "西班牙语",
        "ru" => "俄语",
        _ => code,
    };

    /// <summary>粗粒度字数：有空白按词计数，否则（CJK）按字符计数。</summary>
    public static int CountWords(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return 0;
        if (value.Any(char.IsWhiteSpace))
        {
            return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return value.Length;
    }
}

/// <summary>
/// 一张划词结果卡片：原文 / 译文 / 语言对 + 模板 + 引擎来源；操作按钮由视图触发宿主处理。
/// </summary>
public sealed class SelectionTranslationCardViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private SelectionTranslationState _state = SelectionTranslationState.Loading;
    private string _translation = "";
    private string _errorMessage = "";
    private string _elapsedLabel = "";

    public SelectionTranslationCardViewModel(
        string sourceText,
        string sourceLang,
        string targetLang,
        string templateCode,
        string engineLabel,
        int pageIndex = -1,
        PdfRect rect = default)
    {
        SourceText = sourceText ?? "";
        SourceLang = sourceLang ?? "";
        TargetLang = targetLang ?? "";
        TemplateCode = templateCode ?? "";
        EngineLabel = engineLabel ?? "";
        PageIndex = pageIndex;
        Rect = rect;
        SourceMeta = $"{TranslationLabels.Language(SourceLang)} · {TranslationLabels.CountWords(SourceText)} 字";
        RouteLabel =
            $"{TranslationLabels.Template(TemplateCode)} · " +
            $"{TranslationLabels.Language(SourceLang)}→{TranslationLabels.Language(TargetLang)} · {EngineLabel}";
    }

    /// <summary>原文全文（仅本次会话内存，不写入任何持久化文件）。</summary>
    public string SourceText { get; }

    /// <summary>源语言代码。</summary>
    public string SourceLang { get; }

    /// <summary>目标语言代码。</summary>
    public string TargetLang { get; }

    /// <summary>模板代码。</summary>
    public string TemplateCode { get; }

    /// <summary>引擎来源展示名（本地 / 云端）。</summary>
    public string EngineLabel { get; }

    /// <summary>选区所在页（0 起；未知为 -1）。用于「替换标注」定位。</summary>
    public int PageIndex { get; }

    /// <summary>选区包围盒（PDF 点、左上原点；未知为 default）。「替换标注」写入 sidecar 时作为坐标。</summary>
    public PdfRect Rect { get; }

    /// <summary>原文标题右侧元信息（语言 · 字数）。</summary>
    public string SourceMeta { get; }

    /// <summary>原文与译文之间的路由行（模板 · 语言对 · 引擎）。</summary>
    public string RouteLabel { get; }

    /// <summary>当前状态。</summary>
    public SelectionTranslationState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(IsLoading));
            Raise(nameof(IsDone));
            Raise(nameof(IsError));
            Raise(nameof(IsRetranslatable));
        }
    }

    /// <summary>译文。</summary>
    public string Translation
    {
        get => _translation;
        private set { if (_translation != value) { _translation = value; Raise(nameof(Translation)); } }
    }

    /// <summary>失败原因（可直接展示给用户）。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (_errorMessage != value) { _errorMessage = value; Raise(nameof(ErrorMessage)); } }
    }

    /// <summary>耗时标签（如 "1.2s"）；仅成功时非空。</summary>
    public string ElapsedLabel
    {
        get => _elapsedLabel;
        private set { if (_elapsedLabel != value) { _elapsedLabel = value; Raise(nameof(ElapsedLabel)); } }
    }

    public bool IsLoading => State == SelectionTranslationState.Loading;

    public bool IsDone => State == SelectionTranslationState.Done;

    public bool IsError => State == SelectionTranslationState.Error;

    /// <summary>是否允许重译（加载中禁用，避免重复请求）。</summary>
    public bool IsRetranslatable => State != SelectionTranslationState.Loading;

    /// <summary>进入加载态（重译时复用同一张卡片）。</summary>
    public void BeginLoading()
    {
        Translation = "";
        ErrorMessage = "";
        ElapsedLabel = "";
        State = SelectionTranslationState.Loading;
    }

    /// <summary>填入译文并进入完成态。</summary>
    public void Complete(string translation, double elapsedSeconds)
    {
        Translation = translation ?? "";
        ErrorMessage = "";
        ElapsedLabel = elapsedSeconds >= 0
            ? elapsedSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : "";
        State = SelectionTranslationState.Done;
    }

    /// <summary>进入失败态并携带可读原因。</summary>
    public void Fail(string message)
    {
        Translation = "";
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "翻译失败，请稍后重试。" : message;
        ElapsedLabel = "";
        State = SelectionTranslationState.Error;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// 「划词」Tab 的面板模型：卡片堆叠（新卡置顶，上限 20）+ 无文本层态（F1b）+ 空态。
/// </summary>
public sealed class SelectionTranslationPanelViewModel : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>卡片堆叠上限（设计：划词 Tab 最多保留 20 条）。</summary>
    public const int MaxCards = 20;

    private bool _noTextLayer;

    /// <summary>按时间倒序的卡片集合（索引 0 为最新）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<SelectionTranslationCardViewModel> Cards { get; } = [];

    /// <summary>当前页无文本层 → 划词不可用（F1b）。</summary>
    public bool IsNoTextLayer
    {
        get => _noTextLayer;
        private set
        {
            if (_noTextLayer == value) return;
            _noTextLayer = value;
            Raise(nameof(IsNoTextLayer));
            Raise(nameof(ShowEmptyHint));
        }
    }

    /// <summary>是否有卡片。</summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>是否显示初始空态提示（无卡片且非无文本层态）。</summary>
    public bool ShowEmptyHint => !HasCards && !IsNoTextLayer;

    /// <summary>进入无文本层态（隐藏卡片堆叠，展示替代路径）。</summary>
    public void EnterNoTextLayer() => IsNoTextLayer = true;

    /// <summary>退出无文本层态（选中/翻译成功/切换文档后恢复）。</summary>
    public void ExitNoTextLayer() => IsNoTextLayer = false;

    /// <summary>新增一张卡片（置顶）；超过 <see cref="MaxCards"/> 时丢弃最旧一张。</summary>
    public SelectionTranslationCardViewModel AddCard(
        string sourceText, string sourceLang, string targetLang, string templateCode, string engineLabel,
        int pageIndex = -1, PdfRect rect = default)
    {
        var card = new SelectionTranslationCardViewModel(
            sourceText, sourceLang, targetLang, templateCode, engineLabel, pageIndex, rect);
        Cards.Insert(0, card);
        while (Cards.Count > MaxCards)
        {
            Cards.RemoveAt(Cards.Count - 1);
        }

        IsNoTextLayer = false;
        Raise(nameof(HasCards));
        Raise(nameof(ShowEmptyHint));
        return card;
    }

    /// <summary>清空全部卡片（切换文档时调用；不写回源文件）。</summary>
    public void Clear()
    {
        Cards.Clear();
        Raise(nameof(HasCards));
        Raise(nameof(ShowEmptyHint));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
