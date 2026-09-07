// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Models;
using Microsoft.Win32;

namespace AIPDF.Windows.Views.Security;

/// <summary>安全工具页共用的文件/文件夹选择与拖拽取值。对话框只在 code-behind 使用，不进 VM。</summary>
internal static class ToolFileDialogs
{
    public static IReadOnlyList<string> PickFiles(UserControl owner, Tool tool, bool multiple)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"为{tool.Name}选择文件",
            Filter = BuildFilter(tool),
            Multiselect = multiple,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Window.GetWindow(owner)) == true ? [.. dialog.FileNames] : [];
    }

    public static string? PickImage(UserControl owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择签名图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FileName : null;
    }

    public static string? PickFolder(UserControl owner)
    {
        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        return dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FolderName : null;
    }

    /// <summary>拖拽事件携带的路径列表；存在性与扩展名过滤由 VM 的 FilterExisting 负责。</summary>
    public static IReadOnlyList<string> GetDroppedPaths(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? [.. paths]
            : [];

    private static string BuildFilter(Tool tool)
    {
        var pattern = string.Join(";", tool.Extensions.Select(extension => "*." + extension.ToLowerInvariant()));
        var label = tool.Extensions.Contains("pdf", StringComparer.OrdinalIgnoreCase) ? "PDF 文件" : "图片文件";
        return $"{label}|{pattern}";
    }
}
