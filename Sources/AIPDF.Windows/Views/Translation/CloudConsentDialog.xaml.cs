// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 F5 云端首次同意模态 code-behind：只负责收集「是否同意 / 是否记住」意图，不承载同意持久化与翻译逻辑。
// 同意勾选默认未勾选，「同意并继续」在勾选前禁用；「改用本地引擎」/「取消」返回未同意，
// 宿主据此不发起任何云端请求（真正落库同意由宿主经 TranslationConsentService.GrantConsent 完成）。
using System.Windows;
using AIPDF.Windows.Services;

namespace AIPDF.Windows.Views.Translation;

/// <summary>用户在 F5 模态上的选择。</summary>
public enum CloudConsentChoice
{
    /// <summary>明确同意，可继续云端调用（宿主随后按指纹授予同意）。</summary>
    Accepted,

    /// <summary>改用本地引擎（宿主据此回退本地；无本地运行时则提示去配置）。</summary>
    UseLocal,

    /// <summary>取消 / 关闭：不发送任何内容。</summary>
    Declined,
}

/// <summary>F5 模态结果：选择 + 是否「记住我的选择」（勾选则落设置跨会话，未勾选仅本次会话有效）。</summary>
public sealed record CloudConsentDecision(CloudConsentChoice Choice, bool Remember);

/// <summary>
/// 云端首次同意弹窗（F5 / 决策 #14/#15）。仅当用户主动勾选「我理解并同意…」并点「同意并继续」时返回
/// <see cref="CloudConsentChoice.Accepted"/>；否则返回「改用本地引擎」或「取消」，宿主不得发起云端请求。
/// </summary>
public partial class CloudConsentDialog : Window
{
    private CloudConsentChoice _choice = CloudConsentChoice.Declined;

    private CloudConsentDialog(TranslationConsentRequest request)
    {
        InitializeComponent();

        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "（未指定服务商）" : request.Provider.Trim();
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? "（未指定端点）" : request.Endpoint.Trim();
        ProviderText.Text = $"{provider} · {endpoint}";
        LeadText.Text =
            "整篇与批量翻译将调用云端 LLM。本提示按「服务商 + endpoint」记住：endpoint 主机名 / 协议 / 端口任一变更"
            + "必须重新同意；切换到不同服务商或更换密钥（指纹变化）也会重新提示。请明确知悉并同意以下各项：";
    }

    /// <summary>
    /// 模态展示同意框并返回用户选择。<c>request</c> 仅含目的地标识（不含密钥）。
    /// 取消 / 关闭窗口按「取消」处理，绝不当作同意。
    /// </summary>
    public static CloudConsentDecision Show(Window? owner, TranslationConsentRequest request)
    {
        var dialog = new CloudConsentDialog(request);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return new CloudConsentDecision(dialog._choice, dialog.RememberCheck.IsChecked == true);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (ConsentCheck.IsChecked != true)
        {
            return; // 未勾选安全网（按钮已禁用，此处兜底）
        }

        _choice = CloudConsentChoice.Accepted;
        DialogResult = true;
    }

    private void OnUseLocalClick(object sender, RoutedEventArgs e)
    {
        _choice = CloudConsentChoice.UseLocal;
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _choice = CloudConsentChoice.Declined;
        DialogResult = false;
    }
}
