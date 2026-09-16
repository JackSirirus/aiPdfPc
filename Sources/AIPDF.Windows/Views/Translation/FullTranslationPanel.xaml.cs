// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15「整篇」Tab 面板 code-behind：只做文件夹选择；翻译逻辑全部在 FullTranslationViewModel。
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.ViewModels.Translation;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 阅读器右栏「整篇」Tab 面板。DataContext 为 <see cref="FullTranslationViewModel"/>（宿主注入）；
/// 内部配置区复用 <see cref="TranslationConfigPanel"/>（DataContext = VM.Config）。
/// </summary>
public partial class FullTranslationPanel : UserControl
{
    private readonly TranslationInlineProviderController _inlineController =
        new(new SettingsStore(), new TranslationCredentialStore());

    private bool _inlinePanelHooked;

    /// <summary>XAML 加载用默认构造。</summary>
    public FullTranslationPanel()
    {
        InitializeComponent();

        // v1.2 T15 修复：内联供应商入口的「保存 / 拉取 / 测试」此前只有事件无订阅者（死按钮）。
        // 子面板在 XAML 中无 x:Name，故按逻辑树查找后接线；Loaded 兜底以防模板延迟实例化。
        HookInlinePanel();
        Loaded += (_, _) => HookInlinePanel();
    }

    /// <summary>订阅内联配置面板的三个动作事件，接到设置 / 凭据 / LocalModelFetcher 服务实例。</summary>
    private void HookInlinePanel()
    {
        if (_inlinePanelHooked || FindDescendant<TranslationConfigPanel>(this) is not { } panel)
        {
            return;
        }

        panel.SaveKeyRequested += OnInlineSaveKeyRequested;
        panel.FetchModelsRequested += OnInlineFetchModelsRequested;
        panel.TestConnectionRequested += OnInlineTestConnectionRequested;
        _inlinePanelHooked = true;
    }

    private TranslationConfigViewModel? InlineConfig => (DataContext as FullTranslationViewModel)?.Config;

    private void OnInlineSaveKeyRequested(object? sender, string key)
    {
        if (InlineConfig is { } config)
        {
            _inlineController.SaveKey(config, key);
        }
    }

    private async void OnInlineFetchModelsRequested(object? sender, EventArgs e)
    {
        if (InlineConfig is { } config)
        {
            await _inlineController.FetchModelsAsync(config);
        }
    }

    private async void OnInlineTestConnectionRequested(object? sender, EventArgs e)
    {
        if (InlineConfig is { } config)
        {
            await _inlineController.TestConnectionAsync(config);
        }
    }

    /// <summary>按逻辑树查找类型为 <typeparamref name="T"/> 的后代（子面板在 XAML 中无 x:Name）。</summary>
    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependency)
            {
                continue;
            }

            if (dependency is T match)
            {
                return match;
            }

            if (FindDescendant<T>(dependency) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>选择输出文件夹（引擎会在其下新建任务目录，不覆盖源文件）。</summary>
    private void OnBrowseOutputDirClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FullTranslationViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        var current = viewModel.OutputDir?.Trim() ?? "";
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog() == true)
        {
            viewModel.OutputDir = dialog.FolderName;
        }
    }
}
