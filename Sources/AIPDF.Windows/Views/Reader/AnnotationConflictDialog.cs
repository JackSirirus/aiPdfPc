// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T11 冲突对话框（模态三选一：覆盖保存 / 另存为… / 取消）+ 宿主保存交互适配器。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIPDF.Windows.Annotations;

namespace AIPDF.Windows.Views.Reader;

/// <summary>冲突对话框选择（AC-18.2）。</summary>
internal enum ConflictDialogResult
{
    Overwrite,
    SaveAs,
    Cancel,
}

/// <summary>
/// 代码构建的三按钮模态框（与 <see cref="ConfirmationDialog"/> 同范式，避免 XAML wpftmp 耦合）。
/// 主文案固定为「文件自打开后已被外部程序修改」，副文案给出「上次保存时间」。
/// </summary>
internal sealed class AnnotationConflictDialog : Window
{
    private ConflictDialogResult _result = ConflictDialogResult.Cancel;

    private AnnotationConflictDialog(string message, string subtitle)
    {
        Title = "文件已被外部程序修改";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 440;
        MaxWidth = 620;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Opacity = 0.85,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(MakeButton("覆盖保存", ConflictDialogResult.Overwrite, isDefault: true, isCancel: false));
        buttons.Children.Add(MakeButton("另存为…", ConflictDialogResult.SaveAs, isDefault: false, isCancel: false));
        buttons.Children.Add(MakeButton("取消", ConflictDialogResult.Cancel, isDefault: false, isCancel: true));
        panel.Children.Add(buttons);

        Content = panel;
    }

    private Button MakeButton(string text, ConflictDialogResult result, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 10, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = true; // 任意按钮都关窗；结果经 _result 返回
        };
        return button;
    }

    /// <summary>弹出三按钮模态框；窗口关闭（X/Esc/取消）按「取消」处理。</summary>
    public static ConflictDialogResult Show(Window? owner, string message, string subtitle)
    {
        var dialog = new AnnotationConflictDialog(message, subtitle);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog._result;
    }
}

/// <summary>
/// 生产环境的 <see cref="IAnnotationSavePrompt"/>：把后台保存线程的冲突回调 marshal 回 UI 线程弹窗。
/// 保存服务 await 本实现，故对话框天然阻塞引擎提交。
/// </summary>
public sealed class WpfAnnotationSavePrompt : IAnnotationSavePrompt
{
    private readonly Window _owner;

    public WpfAnnotationSavePrompt(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public Task<ConflictResolution> ResolveExternalModificationAsync(
        AnnotationConflict conflict, CancellationToken cancellationToken)
        => _owner.Dispatcher.InvokeAsync(
            () => ShowConflict(conflict), DispatcherPriority.Normal, cancellationToken).Task;

    public Task<string?> RequestSaveAsPathAsync(string sourcePath, CancellationToken cancellationToken)
        => _owner.Dispatcher.InvokeAsync(
            () => ShowTargetInUse(sourcePath), DispatcherPriority.Normal, cancellationToken).Task;

    private ConflictResolution ShowConflict(AnnotationConflict conflict)
    {
        // 副文案「上次保存时间」= 当前文件 mtime（本地时区），AC-18.1。
        var lastSaved = conflict.CurrentModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var result = AnnotationConflictDialog.Show(
            _owner,
            "文件自打开后已被外部程序修改",
            $"上次保存时间：{lastSaved}\n继续保存会覆盖外部程序写入的改动。");

        return result switch
        {
            ConflictDialogResult.Overwrite => ConflictResolution.Overwrite,
            ConflictDialogResult.SaveAs => PickSaveAsPath(conflict.Path) is { } path
                ? ConflictResolution.SaveAsTo(path)
                : ConflictResolution.Cancel, // 另存为对话框被取消 → 视为取消本次保存
            _ => ConflictResolution.Cancel,
        };
    }

    private string? ShowTargetInUse(string sourcePath)
    {
        // T3 os.replace 被占用：复用「另存为」回退（借用通用确认框的两按钮形态）。
        var proceed = ConfirmationDialog.Show(
            _owner,
            "文件被占用",
            "目标文件正被其他程序占用，无法写入。",
            "请关闭正在使用该文件的程序后重试，或另存为副本。",
            "另存为…",
            "取消");
        return proceed ? PickSaveAsPath(sourcePath) : null;
    }

    /// <summary>弹出系统「另存为」对话框；返回规范化全路径，取消返回 null。</summary>
    private string? PickSaveAsPath(string sourcePath)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            Filter = "PDF 文档 (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(sourcePath) + " (副本).pdf",
            InitialDirectory = Path.GetDirectoryName(sourcePath),
        };

        return dialog.ShowDialog(_owner) == true ? Path.GetFullPath(dialog.FileName) : null;
    }
}
