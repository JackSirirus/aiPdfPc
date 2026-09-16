// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 T10 翻译面板内联快捷入口纯逻辑（设计参考 P27–P31）：
//   - 折叠态：显示「供应商 · 模型」+「更换 ▾」；展开态：头部「模型供应商」+「收起 ▾」；
//   - 展开只在非运行中允许：运行中（IsRunning）强制收起并禁用入口（P31，防「一半用 A、一半用 B」的隐蔽错配）；
//   - 供应商 / 模型下拉、Key 保存、拉取 / 测试按钮的宿主接线见 T15；本 VM 不碰 IO、不碰凭据、不写设置。
using System.ComponentModel;
using AIPDF.Windows.Services;

namespace AIPDF.Windows.ViewModels.Translation;

/// <summary>
/// 面板内联供应商快捷入口的展开 / 收起状态机（只读跟随 <see cref="TranslationProviderReadout"/>）。
/// 展开态内部的供应商 / 模型下拉与 Key / 拉取 / 测试动作由 T15 接线，本类型只负责门禁与展示文案。
/// </summary>
public sealed class TranslationProviderInlineViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>折叠态入口文案（设计 P27）。</summary>
    public const string CollapsedActionLabel = "更换 ▾";

    /// <summary>展开态收起文案（设计 P28）。</summary>
    public const string ExpandedActionLabel = "收起 ▴";

    /// <summary>展开态头部标题（设计 P28）。</summary>
    public const string ExpandedHeaderLabel = "模型供应商";

    /// <summary>任务运行中提示（设计 P31 文案；宿主在入口处展示，提示先取消任务）。</summary>
    public const string RunningHint = "翻译任务运行中，请先取消任务后再修改供应商配置。";

    /// <summary>
    /// 本地服务默认 API 地址（与设置页 <c>SettingsDialog.LocalDefaultEndpoint</c> 同口径：Ollama 默认端口 + /v1 版本段）。
    /// </summary>
    public const string LocalDefaultEndpoint = "http://127.0.0.1:11434/v1";

    /// <summary>
    /// 端口建议 chip（设计稿 S2 注④：仅便利填充，不探测、不扫描端口）。
    /// </summary>
    public sealed record PortSuggestion(int Port, string Name)
    {
        /// <summary>chip 展示文案（<c>端口 名称</c>）。</summary>
        public string Label => $"{Port} {Name}";
    }

    /// <summary>本地服务端口建议（Ollama / LM Studio / vLLM / llama.cpp 常见默认端口）。</summary>
    private static readonly IReadOnlyList<PortSuggestion> DefaultPortSuggestions =
    [
        new(11434, "Ollama"),
        new(1234, "LM Studio"),
        new(8000, "vLLM"),
        new(8080, "llama.cpp"),
    ];

    private readonly TranslationProviderReadout _readout;
    private bool _isRunning;
    private bool _isExpanded;
    private string _localEndpoint = LocalDefaultEndpoint;
    private string _operationMessage = "";
    private bool _operationIsError;

    public TranslationProviderInlineViewModel(TranslationProviderReadout readout)
    {
        _readout = readout ?? throw new ArgumentNullException(nameof(readout));
        _readout.PropertyChanged += OnReadoutChanged;
    }

    /// <summary>
    /// 翻译任务是否运行中（宿主设置）。运行中禁止展开 / 切换，已展开则强制收起；
    /// 与设置页的「移除供应商」前置动作（P31 先取消任务）保持一致。
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            if (value)
            {
                IsExpanded = false;
            }

            Raise(nameof(IsRunning));
            Raise(nameof(IsExpandable));
            Raise(nameof(CanChangeProvider));
            Raise(nameof(IsConfigEnabled));
            Raise(nameof(ShowRunningHint));
        }
    }

    /// <summary>展开入口是否可用（运行中为 false）。</summary>
    public bool IsExpandable => !IsRunning;

    /// <summary>是否允许更换供应商（与 <see cref="IsExpandable"/> 同义，供按钮 IsEnabled 绑定）。</summary>
    public bool CanChangeProvider => !IsRunning;

    /// <summary>展开态配置块是否可编辑（运行中只读）。</summary>
    public bool IsConfigEnabled => !IsRunning;

    /// <summary>是否展示「先取消任务」提示。</summary>
    public bool ShowRunningHint => IsRunning;

    /// <summary>运行中提示文案。</summary>
    public string RunningHintText => RunningHint;

    /// <summary>
    /// 本地服务 API 地址（仅本地供应商展开态显示；默认 Ollama loopback 模板）。
    /// 变更时实时刷新 loopback 校验；非 loopback / 格式错由 <see cref="LocalEndpointError"/> 呈现，不静默放行。
    /// </summary>
    public string LocalEndpoint
    {
        get => _localEndpoint;
        set
        {
            var normalized = value ?? "";
            if (string.Equals(_localEndpoint, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _localEndpoint = normalized;
            Raise(nameof(LocalEndpoint));
            Raise(nameof(LocalEndpointError));
            Raise(nameof(HasLocalEndpointError));
        }
    }

    /// <summary>本地服务端口建议 chips（仅便利填充，不探测、不扫描端口）。</summary>
    public IReadOnlyList<PortSuggestion> PortSuggestions => DefaultPortSuggestions;

    /// <summary>
    /// 本地地址校验错误文案；null 表示合法或未填写。
    /// 复用 <see cref="TranslationProviderConfig.IsLoopbackEndpoint"/>：非 loopback 地址不能以「本地服务」名义使用。
    /// </summary>
    public string? LocalEndpointError
    {
        get
        {
            var raw = _localEndpoint.Trim();
            if (raw.Length == 0)
            {
                return null; // 未填写：由提交动作给出「请填写地址」的引导，避免一打开就报错
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "地址格式无效；示例：" + LocalDefaultEndpoint + "（须以 http:// 或 https:// 开头）。";
            }

            if (!TranslationProviderConfig.IsLoopbackEndpoint(raw))
            {
                return TranslationProviderConfig.LoopbackViolationMessage(raw)
                    + " 非 loopback 地址不能以「本地服务」名义使用（明文 http 仅限本机）。";
            }

            return null;
        }
    }

    /// <summary>本地地址是否有校验错误（错误行显隐）。</summary>
    public bool HasLocalEndpointError => LocalEndpointError is not null;

    /// <summary>操作结果提示（保存密钥 / 拉取模型 / 测试连接；不承载密钥本身）。</summary>
    public string OperationMessage => _operationMessage;

    /// <summary>是否存在操作结果提示（提示行显隐）。</summary>
    public bool HasOperationMessage => _operationMessage.Length > 0;

    /// <summary>操作结果是否为错误（错误配色展示）。</summary>
    public bool IsOperationError => _operationIsError;

    /// <summary>
    /// 设置操作结果提示（宿主在保存 / 拉取 / 测试后回调）。文案绝不包含 API Key 或其它密钥。
    /// </summary>
    public void SetOperationMessage(string? message, bool isError = false)
    {
        _operationMessage = (message ?? "").Trim();
        _operationIsError = isError;
        Raise(nameof(OperationMessage));
        Raise(nameof(HasOperationMessage));
        Raise(nameof(IsOperationError));
    }

    /// <summary>清空操作结果提示。</summary>
    public void ClearOperationMessage() => SetOperationMessage(null);

    /// <summary>
    /// 点击端口建议 chip：把当前地址的端口段替换为建议端口；当前地址非法时回落为 127.0.0.1 模板
    /// （复用设置页 <c>ApplyPortSuggestionToAddress</c> 口径：仅改端口 / 保留主机与路径，不探测端口）。
    /// </summary>
    public void ApplyPortSuggestion(int port) => LocalEndpoint = ApplyPortToAddress(_localEndpoint, port);

    /// <summary>
    /// 端口建议 chip 的地址替换逻辑（纯函数，便于单测）：合法 http(s) 绝对地址仅替换端口段并保留主机与路径；
    /// 否则回落为 <c>http://127.0.0.1:{port}/v1</c> 模板（与设置页同口径）。
    /// </summary>
    internal static string ApplyPortToAddress(string? current, int port)
    {
        var value = current?.Trim() ?? "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var builder = new UriBuilder(uri) { Port = port };
            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return $"http://127.0.0.1:{port}/v1";
    }

    /// <summary>是否处于展开态（只能经 <see cref="Expand"/> / <see cref="Toggle"/> / <see cref="Collapse"/> 改变）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(IsCollapsed));
            Raise(nameof(HeaderLabel));
            Raise(nameof(ActionLabel));
        }
    }

    /// <summary>是否处于折叠态。</summary>
    public bool IsCollapsed => !IsExpanded;

    /// <summary>折叠态摘要（只读跟随：供应商 · 模型）。</summary>
    public string SummaryLabel => _readout.SummaryLabel;

    /// <summary>当前供应商显示名（只读跟随）。</summary>
    public string ProviderLabel => _readout.DisplayLabel;

    /// <summary>协议徽章（只读跟随）。</summary>
    public string ProtocolBadge => _readout.ProtocolBadge;

    /// <summary>头部文案：折叠 → 「显示名 · 模型」；展开 → 「模型供应商」。</summary>
    public string HeaderLabel => IsExpanded ? ExpandedHeaderLabel : SummaryLabel;

    /// <summary>展开入口文案：折叠 → 「更换 ▾」；展开 → 「收起 ▴」。</summary>
    public string ActionLabel => IsExpanded ? ExpandedActionLabel : CollapsedActionLabel;

    /// <summary>尝试展开；运行中返回 false 且不改变状态。</summary>
    public bool Expand()
    {
        if (!IsExpandable)
        {
            return false;
        }

        IsExpanded = true;
        return true;
    }

    /// <summary>收起（任何状态都允许；未保存改动的丢弃由宿主处理）。</summary>
    public void Collapse() => IsExpanded = false;

    /// <summary>切换展开 / 收起；运行中为 no-op（返回 false）。</summary>
    public bool Toggle()
    {
        if (IsExpanded)
        {
            Collapse();
            return true;
        }

        return Expand();
    }

    /// <summary>保存完成：回到折叠态并展示新供应商（宿主负责 Toast 与写回）。</summary>
    public void Complete() => Collapse();

    /// <summary>取消：回到折叠态（宿主负责还原未保存改动）。</summary>
    public void Cancel() => Collapse();

    /// <summary>反订阅只读来源。</summary>
    public void Dispose() => _readout.PropertyChanged -= OnReadoutChanged;

    private void OnReadoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(SummaryLabel));
        Raise(nameof(ProviderLabel));
        Raise(nameof(ProtocolBadge));
        Raise(nameof(HeaderLabel));
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
