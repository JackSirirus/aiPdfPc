// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T10 供应商只读跟随（划词卡片 / 面板内联入口共用）：
//   - 把「当前生效供应商」投影为只读展示（显示名 / 模型 / 协议徽章），自身没有选择或切换入口；
//   - 数据来自宿主注入的快照函数；源对象实现 INotifyPropertyChanged 时自动重读（共享选择变化 → 标签同步）；
//   - T15 接入面板内联选择时，只需把快照函数指向共享 TranslationConfigViewModel 的选中项；
//   - 本类型不写设置、不碰凭据、不做任何 IO。
using System.ComponentModel;
using AIPDF.Windows.Services;

namespace AIPDF.Windows.ViewModels.Translation;

/// <summary>当前生效供应商的只读快照：稳定 id / 显示名 / 模型 / 协议（<c>openai</c> | <c>anthropic</c> | 空）。</summary>
public sealed record TranslationProviderSnapshot(string ProviderId, string DisplayName, string Model, string Protocol);

/// <summary>
/// 当前生效供应商的只读展示（划词卡片与面板内联入口共用）。
/// 仅暴露读取面：<see cref="DisplayLabel"/> / <see cref="SummaryLabel"/> / <see cref="ProtocolBadge"/>；
/// <b>不提供任何选择 / 切换 API</b>（划词无独立供应商选择入口，切换只发生在设置页或面板内联块）。
/// </summary>
public sealed class TranslationProviderReadout : INotifyPropertyChanged, IDisposable
{
    /// <summary>未配置任何供应商时的展示文案（沿用 v1.1 划词卡片口径）。</summary>
    public const string UnconfiguredLabel = "未配置";

    /// <summary>OpenAI 协议徽章文案（别名；唯一来源 <see cref="TranslationProviderCompliance.OpenAiLabel"/>）。</summary>
    public const string OpenAiBadge = TranslationProviderCompliance.OpenAiLabel;

    /// <summary>Anthropic 协议徽章文案（别名；唯一来源 <see cref="TranslationProviderCompliance.AnthropicLabel"/>）。</summary>
    public const string AnthropicBadge = TranslationProviderCompliance.AnthropicLabel;

    private readonly Func<TranslationProviderSnapshot>? _snapshot;
    private INotifyPropertyChanged? _source;
    private bool _disposed;

    /// <summary>
    /// 创建只读投影。<paramref name="snapshot"/> 为快照函数（可为 null，此时仅作占位）；
    /// <paramref name="source"/> 为共享选择来源，其 <c>PropertyChanged</c> 会触发 <see cref="Refresh"/>。
    /// 构造后不主动读取快照（避免启动期访问设置 / 凭据）：首次展示前调用一次 <see cref="Refresh"/>。
    /// </summary>
    public TranslationProviderReadout(
        Func<TranslationProviderSnapshot>? snapshot = null,
        INotifyPropertyChanged? source = null)
    {
        _snapshot = snapshot;
        Attach(source);
    }

    /// <summary>稳定供应商 id（如 <c>openai</c> / <c>local</c>；未配置为空串）。</summary>
    public string ProviderId { get; private set; } = "";

    /// <summary>供应商显示名（快照缺失时按稳定 id 回退预设显示名；未配置为空串）。</summary>
    public string DisplayName { get; private set; } = "";

    /// <summary>当前模型名（未配置为空串）。</summary>
    public string Model { get; private set; } = "";

    /// <summary>当前协议（小写；未配置为空串）。</summary>
    public string Protocol { get; private set; } = "";

    /// <summary>是否已有可展示的供应商。</summary>
    public bool HasProvider => DisplayName.Length > 0 || ProviderId.Length > 0;

    /// <summary>只读显示名（未配置 → 「未配置」）。</summary>
    public string DisplayLabel => HasProvider ? DisplayName : UnconfiguredLabel;

    /// <summary>协议徽章（<c>openai</c> → OpenAI 兼容；<c>anthropic</c> → Anthropic 兼容；未知为空串）。</summary>
    public string ProtocolBadge => Badge(Protocol);

    /// <summary>是否展示协议徽章。</summary>
    public bool HasProtocolBadge => ProtocolBadge.Length > 0;

    /// <summary>折叠态摘要：显示名 · 模型（无模型时只显示名；未配置 → 「未配置」）。</summary>
    public string SummaryLabel => !HasProvider
        ? UnconfiguredLabel
        : (Model.Length > 0 ? $"{DisplayName} · {Model}" : DisplayName);

    /// <summary>是否 Anthropic 协议（徽章样式 / 提示用）。</summary>
    public bool IsAnthropic
        => string.Equals(Protocol, TranslationProviderConfig.ProtocolAnthropic, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否本地服务（稳定 id <c>local</c> 优先，旧数据回退显示名，与运行时判定同源）。</summary>
    public bool IsLocal => TranslationProviderConfig.IsLocalService(ProviderId, DisplayName);

    /// <summary>协议值 → 徽章文案（空 / 未知 → 空串，不臆造协议）。</summary>
    public static string Badge(string? protocol) => protocol?.Trim().ToLowerInvariant() switch
    {
        TranslationProviderConfig.ProtocolAnthropic => AnthropicBadge,
        TranslationProviderConfig.ProtocolOpenAi => OpenAiBadge,
        _ => "",
    };

    /// <summary>重新读取快照并广播变化（宿主在共享选择变化 / 打开划词前调用）。</summary>
    public void Refresh()
    {
        if (_disposed || _snapshot is null)
        {
            return;
        }

        Apply(_snapshot());
    }

    /// <summary>换绑共享选择来源（重复挂接同一来源不重复订阅）。</summary>
    public void Attach(INotifyPropertyChanged? source)
    {
        if (_disposed || ReferenceEquals(_source, source))
        {
            return;
        }

        if (_source is not null)
        {
            _source.PropertyChanged -= OnSourcePropertyChanged;
        }

        _source = source;

        if (_source is not null)
        {
            _source.PropertyChanged += OnSourcePropertyChanged;
        }
    }

    /// <summary>反订阅共享来源（窗口关闭 / 测试清理用）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source is not null)
        {
            _source.PropertyChanged -= OnSourcePropertyChanged;
            _source = null;
        }
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Apply(TranslationProviderSnapshot snapshot)
    {
        var providerId = snapshot.ProviderId?.Trim() ?? "";
        var displayName = snapshot.DisplayName?.Trim() ?? "";
        if (displayName.Length == 0)
        {
            // 只拿到稳定 id（共享 VM 的 SelectedProvider）时回退预设显示名；自定义供应商保留空名。
            displayName = TranslationProviderPreset.Find(providerId)?.DisplayName ?? "";
        }

        var model = snapshot.Model?.Trim() ?? "";
        var protocol = snapshot.Protocol?.Trim().ToLowerInvariant() ?? "";

        if (providerId == ProviderId && displayName == DisplayName && model == Model && protocol == Protocol)
        {
            return;
        }

        ProviderId = providerId;
        DisplayName = displayName;
        Model = model;
        Protocol = protocol;

        Raise(nameof(ProviderId));
        Raise(nameof(DisplayName));
        Raise(nameof(Model));
        Raise(nameof(Protocol));
        Raise(nameof(HasProvider));
        Raise(nameof(DisplayLabel));
        Raise(nameof(ProtocolBadge));
        Raise(nameof(HasProtocolBadge));
        Raise(nameof(SummaryLabel));
        Raise(nameof(IsAnthropic));
        Raise(nameof(IsLocal));
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
