// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 共享配置面板（T13）：阅读器「整篇」Tab（T15）与批量翻译工具页（T21）复用同一 UserControl。
// v1.2 T15：模型供应商改为「内联就地展开」（折叠 → 展开 → 完成/收起，不弹窗、不跳设置页）。
//   - 展开 / 收起由 TranslationConfigViewModel.Inline（TranslationProviderInlineViewModel）驱动；
//   - Key 保存 / 拉取模型 / 测试连接涉及 IO 与凭据，面板只上抛事件，宿主接线（密钥仅经 stdin 传递）。
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.ViewModels.Translation;

namespace AIPDF.Windows.Views.Translation;

/// <summary>
/// 共享翻译配置面板。DataContext 为 <see cref="TranslationConfigViewModel"/>：
/// 宿主用 <c>DataContext="{Binding Config}"</c> 注入即可，控制内部不持有宿主 VM，保证整篇 Tab 与批量页同源复用。
/// </summary>
public partial class TranslationConfigPanel : UserControl
{
    /// <summary>
    /// 点击展开态「保存」：请求把输入框中的 API Key 写入当前供应商（宿主接线系统凭据管理器；
    /// 事件参数为输入值，密钥不落设置文件、仅经 stdin 传递）。
    /// </summary>
    public event EventHandler<string>? SaveKeyRequested;

    /// <summary>
    /// 点击展开态「拉取模型」：请求从当前选中供应商拉取模型列表（宿主调用 T5 LocalModelFetcher，
    /// 结果经 <see cref="TranslationConfigViewModel.SetFetchedModels"/> 回填）。
    /// </summary>
    public event EventHandler? FetchModelsRequested;

    /// <summary>点击展开态「测试连接」：请求验证当前选中供应商的连通性（宿主接线）。</summary>
    public event EventHandler? TestConnectionRequested;

    /// <summary>XAML 加载用默认构造。</summary>
    public TranslationConfigPanel()
    {
        InitializeComponent();
    }

    /// <summary>便捷构造：直接注入配置视图模型（等价于手动设置 DataContext）。</summary>
    public TranslationConfigPanel(TranslationConfigViewModel config)
        : this()
        => DataContext = config;

    /// <summary>当前共享配置视图模型（面板 DataContext）。</summary>
    private TranslationConfigViewModel? Config => DataContext as TranslationConfigViewModel;

    /// <summary>折叠 ↔ 展开内联配置块；任务运行中为 no-op（<see cref="TranslationProviderInlineViewModel.Toggle"/>）。</summary>
    private void OnInlineToggleClick(object sender, RoutedEventArgs e)
    {
        if (Config is { } config)
        {
            config.Inline.Toggle();
        }
    }

    /// <summary>完成：收起内联块（显示值已随双向绑定写回共享 VM，由宿主在需要时持久化）。</summary>
    private void OnInlineCompleteClick(object sender, RoutedEventArgs e)
        => Config?.Inline.Complete();

    /// <summary>保存密钥：把输入值上抛给宿主（空值不触发，避免误清空已保存凭据）。</summary>
    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        var value = ApiKeyBox.Password;
        if (value.Length > 0)
        {
            SaveKeyRequested?.Invoke(this, value);
        }
    }

    /// <summary>拉取模型：上抛给宿主执行 IO 并回填。</summary>
    private void OnFetchModelsClick(object sender, RoutedEventArgs e)
        => FetchModelsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>测试连接：上抛给宿主执行。</summary>
    private void OnTestConnectionClick(object sender, RoutedEventArgs e)
        => TestConnectionRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>端口建议 chip：把内联地址的端口段替换为建议端口（仅便利填充，不探测端口）。</summary>
    private void OnLocalPortSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: var tag } || Config is not { } config)
        {
            return;
        }

        var port = tag switch
        {
            int value => value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => -1,
        };

        if (port > 0)
        {
            config.Inline.ApplyPortSuggestion(port);
        }
    }
}

/// <summary>
/// 内联供应商三个动作（保存密钥 / 拉取模型 / 测试连接）的宿主执行器（T15 修复）：
/// 把 <see cref="TranslationConfigPanel"/> 上抛的事件真正接到服务实例
/// （<see cref="SettingsStore"/> + <see cref="TranslationCredentialStore"/> + <see cref="LocalModelFetcher"/>）。
/// <para>
/// 目的地口径：本地取内联地址（须 loopback）；云端取设置中同身份键的已保存端点。
/// 密钥只在「输入框 → 系统凭据管理器」间传递，绝不进入设置 / 日志 / 异常文本。
/// </para>
/// </summary>
internal sealed class TranslationInlineProviderController
{
    private readonly SettingsStore _settings;
    private readonly TranslationCredentialStore _credentials;
    private readonly Func<LocalModelFetcher> _fetcherFactory;

    /// <summary>生产构造：宿主注入设置 / 凭据存储；<paramref name="fetcherFactory"/> 供测试注入内存 HTTP 替身。</summary>
    public TranslationInlineProviderController(
        SettingsStore settings,
        TranslationCredentialStore credentials,
        Func<LocalModelFetcher>? fetcherFactory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _fetcherFactory = fetcherFactory ?? (() => new LocalModelFetcher());
    }

    /// <summary>保存 API Key：按当前目的地写入系统凭据管理器（空值不动作，避免误清空已存凭据）。</summary>
    public void SaveKey(TranslationConfigViewModel config, string? key)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!TryResolveEndpoint(config, out var endpoint, out var error))
        {
            config.Inline.SetOperationMessage(error, isError: true);
            return;
        }

        try
        {
            _credentials.Store(config.SelectedProvider ?? "", endpoint, key);
            config.Inline.SetOperationMessage("API Key 已保存到系统凭据管理器。");
        }
        catch (ArgumentException)
        {
            config.Inline.SetOperationMessage("请先填写有效的 API 地址，再保存密钥。", isError: true);
        }
        catch (CredentialStoreUnavailableException credentialError)
        {
            config.Inline.SetOperationMessage(credentialError.Message, isError: true);
        }
    }

    /// <summary>拉取模型：调用 T5 <see cref="LocalModelFetcher"/>，成功后回填当前供应商模型列表。</summary>
    public async Task FetchModelsAsync(TranslationConfigViewModel config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!TryResolveEndpoint(config, out var endpoint, out var error))
        {
            config.Inline.SetOperationMessage(error, isError: true);
            return;
        }

        try
        {
            var models = await _fetcherFactory()
                .FetchAsync(endpoint, ReadStoredKey(config, endpoint), config.Protocol)
                .ConfigureAwait(true);

            config.SetFetchedModels(models); // 现有 API：回填 AvailableModels 并在未选模型时选中首个
            config.Inline.SetOperationMessage(
                models.Count > 0 ? $"已拉取 {models.Count} 个模型。" : "连接成功，但服务端未返回任何模型。");
        }
        catch (LocalModelFetchException fetchError)
        {
            config.Inline.SetOperationMessage(fetchError.Message, isError: true);
        }
        catch (ArgumentException argumentError)
        {
            config.Inline.SetOperationMessage(argumentError.Message, isError: true);
        }
        catch (CredentialStoreUnavailableException credentialError)
        {
            config.Inline.SetOperationMessage(credentialError.Message, isError: true);
        }
    }

    /// <summary>测试连接：对当前目的地做一次最小模型列表请求（不改变模型列表，仅报告连通性）。</summary>
    public async Task TestConnectionAsync(TranslationConfigViewModel config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!TryResolveEndpoint(config, out var endpoint, out var error))
        {
            config.Inline.SetOperationMessage(error, isError: true);
            return;
        }

        try
        {
            await _fetcherFactory()
                .FetchAsync(endpoint, ReadStoredKey(config, endpoint), config.Protocol)
                .ConfigureAwait(true);
            config.Inline.SetOperationMessage("测试连接通过。");
        }
        catch (Exception exception)
            when (exception is LocalModelFetchException or ArgumentException or CredentialStoreUnavailableException)
        {
            config.Inline.SetOperationMessage($"测试连接失败：{exception.Message}", isError: true);
        }
    }

    /// <summary>
    /// 解析当前目的地端点：本地用内联地址（loopback 校验由 inline VM 负责）；云端用设置中同身份键的端点。
    /// 解析失败时 <paramref name="error"/> 为可直接展示的原因。
    /// </summary>
    internal bool TryResolveEndpoint(TranslationConfigViewModel config, out string endpoint, out string error)
    {
        if (config.IsLocalProvider)
        {
            endpoint = config.Inline.LocalEndpoint?.Trim() ?? "";
            if (config.Inline.LocalEndpointError is { } localError)
            {
                error = localError;
                return false;
            }

            if (endpoint.Length == 0)
            {
                error = $"请填写本机 OpenAI 兼容地址，例如 {TranslationProviderInlineViewModel.LocalDefaultEndpoint}。";
                return false;
            }

            error = "";
            return true;
        }

        endpoint = ResolveCloudEndpoint(config, _settings);
        if (endpoint.Length == 0)
        {
            error = "尚未在「设置 → 翻译」中保存该供应商的 API 地址，请先保存端点后再试。";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>云端目的地：按稳定身份键在设置中定位已保存供应商的端点（自定义按规范名匹配）。</summary>
    internal static string ResolveCloudEndpoint(TranslationConfigViewModel config, SettingsStore settings)
    {
        var selected = config.SelectedProvider?.Trim() ?? "";
        if (selected.Length == 0)
        {
            return "";
        }

        foreach (var provider in settings.Load().TranslationProviders)
        {
            if (string.Equals(TranslationProviderIdentity.For(provider), selected, StringComparison.OrdinalIgnoreCase))
            {
                return provider.Endpoint?.Trim() ?? "";
            }
        }

        return "";
    }

    /// <summary>读取当前目的地已保存的密钥（凭据管理器不可用时视为未保存，不阻断拉取 / 测试）。</summary>
    private string? ReadStoredKey(TranslationConfigViewModel config, string endpoint)
    {
        try
        {
            return _credentials.Get(config.SelectedProvider ?? "", endpoint);
        }
        catch (CredentialStoreUnavailableException)
        {
            return null;
        }
    }
}
