// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T9【ST-04】模态密码框 code-behind：只做输入收集与行内错误展示，解锁判定委托宿主回调。
// 红线：密码不绑定到任何控件之外的状态、不落盘、不写日志、不进异常消息；本类不暴露 Password 属性。
using System.Windows;

namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 加密文档密码解锁对话框（【ST-04】）。构造时注入「尝试解锁」回调：
/// 回调返回 true 表示密码正确（文档已成功打开），对话框以 <see cref="Window.DialogResult"/>=true 关闭；
/// 返回 false 则保持打开、显示行内红字、<b>不清空输入、不限次数</b>。取消 / Esc → DialogResult=false。
/// </summary>
public partial class PasswordPromptDialog : Window
{
    private readonly Func<string, bool> _tryUnlock;

    /// <param name="fileName">展示用的文件名（不含密码等敏感信息）。</param>
    /// <param name="tryUnlock">用输入密码尝试打开文档；true = 成功。</param>
    public PasswordPromptDialog(string fileName, Func<string, bool> tryUnlock)
    {
        ArgumentNullException.ThrowIfNull(tryUnlock);
        InitializeComponent();
        _tryUnlock = tryUnlock;
        FileNameText.Text = fileName ?? string.Empty;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowError(ReaderStrings.PasswordDialogEmptyError);
            return;
        }

        if (_tryUnlock(password))
        {
            // 成功：宿主回调内完成 SessionCredentialStore 驻留；本对话框不保存、不暴露密码。
            DialogResult = true;
            return;
        }

        // 失败：行内红字提示，不清空输入、不限次数（ST-04）。
        ShowError(ReaderStrings.PasswordDialogWrongError);
        PasswordInput.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => DialogResult = false; // 取消 / Esc：宿主据此不产生标签

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
