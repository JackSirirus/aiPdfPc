// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T8 左栏面板接管状态机（纯逻辑，可脱离 UI 测试）：
//   普通切换 Activate(key)；Ctrl+F 接管 OpenSearch()（记住当前面板）；Esc/✕ ExitSearch() 恢复原面板。
// 面板键与 MainWindow 面板切换器 Tag（thumbs/outline/bookmarks/search）一致【D6】【R17】。

namespace AIPDF.Windows.ViewModels;

/// <summary>
/// 左栏面板显示状态：当前面板键 + 「进入搜索前」的恢复键。
/// 无 UI 依赖：MainWindow 依据 <see cref="ActiveKey"/> 决定 ReaderPanelHost 内容与 RadioButton 选中。
/// </summary>
public sealed class ReaderPanelState
{
    public const string Thumbs = "thumbs";
    public const string Outline = "outline";
    public const string Bookmarks = "bookmarks";
    public const string Search = "search";

    /// <summary>搜索接管前的面板键；仅当当前为搜索面板时 <see cref="ExitSearch"/> 用其恢复。</summary>
    private string _restoreKey = Thumbs;

    /// <summary>当前激活面板键（默认缩略图）。</summary>
    public string ActiveKey { get; private set; } = Thumbs;

    /// <summary>当前是否处于搜索面板（Esc/✕ 是否应被本状态机消费）。</summary>
    public bool IsSearchActive => ActiveKey == Search;

    /// <summary>进入搜索前的面板键（观测/测试用）。</summary>
    public string RestoreKey => _restoreKey;

    /// <summary>用户手动切换面板：直接置为 <paramref name="key"/>。若从搜索切走，不保留搜索恢复语义。</summary>
    public void Activate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ActiveKey = key;
    }

    /// <summary>Ctrl+F / 点击搜索入口：记住当前面板并切到搜索（已处于搜索时仅幂等，不覆盖恢复键）。</summary>
    public void OpenSearch()
    {
        if (ActiveKey != Search)
        {
            _restoreKey = ActiveKey;
        }
        ActiveKey = Search;
    }

    /// <summary>Esc / 面板 ✕：若正处搜索则恢复原面板并返回 true；否则不动作返回 false。</summary>
    public bool ExitSearch()
    {
        if (ActiveKey != Search)
        {
            return false;
        }

        ActiveKey = _restoreKey;
        return true;
    }
}
