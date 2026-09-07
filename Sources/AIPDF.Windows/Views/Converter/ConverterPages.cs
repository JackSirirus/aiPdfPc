// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Converter;

/// <summary>
/// 转换组页面解析器（Wave 5 并行约定）：MainWindow 启动时把
/// <see cref="TryCreate"/> 注册进 ToolPageRegistry，格式转换组的 11 个工具
/// （图片 / Office / 网址 / PDF-A / Markdown）由此认领，其余工具交给其他组。
/// </summary>
public static class ConverterPages
{
    /// <summary>MainWindow 启动时注册一行；与其他工具组共用 ToolPageRegistry 的解析器顺序约定。</summary>
    public static void Register() => ToolPageRegistry.AddResolver(TryCreate);

    public static FrameworkElement? TryCreate(Tool tool, EngineBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return ConverterToolPages.CreateViewModel(tool, bridge) switch
        {
            ImagesToPdfViewModel viewModel => new ImagesToPdfPage(viewModel),
            PdfToImagesViewModel viewModel => new PdfToImagesPage(viewModel),
            WordToPdfViewModel viewModel => new WordToPdfPage(viewModel),
            PptToPdfViewModel viewModel => new PptToPdfPage(viewModel),
            ExcelToPdfViewModel viewModel => new ExcelToPdfPage(viewModel),
            PdfToWordViewModel viewModel => new PdfToWordPage(viewModel),
            PdfToPptViewModel viewModel => new PdfToPptPage(viewModel),
            PdfToExcelViewModel viewModel => new PdfToExcelPage(viewModel),
            HtmlToPdfViewModel viewModel => new HtmlToPdfPage(viewModel),
            PdfaViewModel viewModel => new PdfaPage(viewModel),
            MarkdownViewModel viewModel => new MarkdownPage(viewModel),
            _ => null,
        };
    }
}
