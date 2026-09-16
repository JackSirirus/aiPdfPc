// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 批量翻译工具页 code-behind：只做拖拽转发；其余交互（添加 / 移除 / 浏览 / 执行 / 重试 / 按需打开）
// 全在 TranslationBatchViewModel 上，页面本身无副作用逻辑。
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Translation;

/// <summary>批量翻译工具页（DataContext = <see cref="TranslationBatchViewModel"/>）。</summary>
public partial class TranslationBatchPage : UserControl
{
    private readonly TranslationInlineProviderController _inlineController =
        new(new SettingsStore(), new TranslationCredentialStore());

    private bool _inlinePanelHooked;

    public TranslationBatchViewModel ViewModel { get; }

    public TranslationBatchPage(
        EngineBridge? bridge,
        Tool tool,
        ITranslationRuntimeConfigProvider? configProvider = null,
        Func<TranslationConsentRequest, Task<bool>>? confirmCloudConsentAsync = null)
    {
        InitializeComponent();
        ViewModel = new TranslationBatchViewModel(bridge, tool, configProvider, confirmCloudConsentAsync: confirmCloudConsentAsync);
        DataContext = ViewModel;

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

    private void OnInlineSaveKeyRequested(object? sender, string key)
        => _inlineController.SaveKey(ViewModel.Config, key);

    private async void OnInlineFetchModelsRequested(object? sender, EventArgs e)
        => await _inlineController.FetchModelsAsync(ViewModel.Config);

    private async void OnInlineTestConnectionRequested(object? sender, EventArgs e)
        => await _inlineController.TestConnectionAsync(ViewModel.Config);

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

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ViewModel.AddFiles(files);
        }

        e.Handled = true;
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
