// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T9 批注属性面板测试：选中能力矩阵（线宽/不透明度按类型）、改色/线宽/不透明度触发 Changed、
// 关闭后不再自动弹出、以及模型层的钳制与内容等价。
using System.Collections.Generic;
using System.Linq;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

/// <summary>批注属性面板（纯逻辑，无 UI）。</summary>
public sealed class AnnotationPropertiesViewModelTests
{
    private static AnnotationRecord Highlight()
        => AnnotationRecord.CreateHighlight(0, new PdfRect(10, 20, 110, 40));

    private static AnnotationRecord Ink()
        => AnnotationRecord.CreateInk(0, [new[] { new PdfPoint(0, 0), new PdfPoint(5, 5) }]);

    [Fact]
    public void Select_ExposesKindSpecificCapabilities()
    {
        var viewModel = new AnnotationPropertiesViewModel();
        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.HasSelection);

        // 文本标记：可调不透明度，不可调线宽（引擎的 add_text_markup_annot 不接收 width）
        viewModel.Select(Highlight());
        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.SupportsOpacity);
        Assert.False(viewModel.SupportsLineWidth);

        // 手写：可调线宽，不可调不透明度（引擎的 add_ink_annot 不接收 opacity）
        viewModel.Select(Ink());
        Assert.True(viewModel.SupportsLineWidth);
        Assert.False(viewModel.SupportsOpacity);

        viewModel.Select(null);
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void Apply_UpdatesRecordAndRaisesChanged()
    {
        var viewModel = new AnnotationPropertiesViewModel();
        viewModel.Select(Highlight());
        var raised = new List<AnnotationRecord>();
        viewModel.Changed += (_, record) => raised.Add(record);

        viewModel.ApplyColor("#123456");
        Assert.Equal("#123456", viewModel.Color);
        Assert.Equal("#123456", raised[^1].Color);

        viewModel.ApplyOpacity(0.25);
        Assert.Equal(0.25, viewModel.Opacity, 3);
        Assert.Equal(0.25, raised[^1].Opacity, 3);

        viewModel.Select(Ink());
        viewModel.ApplyLineWidth(4);
        Assert.Equal(4, viewModel.LineWidth, 3);
        Assert.Equal(4, raised[^1].LineWidth, 3);

        // 无选中时三个 Apply 都必须是安全的空操作
        viewModel.Select(null);
        var count = raised.Count;
        viewModel.ApplyColor("#000000");
        viewModel.ApplyOpacity(0.5);
        viewModel.ApplyLineWidth(2);
        Assert.Equal(count, raised.Count);
    }

    [Fact]
    public void Dismiss_SuppressesAutoOpen_UntilAllowed()
    {
        var viewModel = new AnnotationPropertiesViewModel();
        viewModel.Select(Highlight());
        Assert.True(viewModel.IsVisible);

        viewModel.Dismiss();
        Assert.True(viewModel.IsSuppressed);
        Assert.False(viewModel.IsVisible);
        Assert.Null(viewModel.Record);

        // 已关闭：再次选中也不自动弹出（会话内记忆）
        viewModel.Select(Highlight());
        Assert.False(viewModel.IsVisible);

        viewModel.AllowAutoOpen();
        Assert.True(viewModel.IsVisible);
    }

    [Fact]
    public void Palette_IsNonEmptyAndDistinct()
    {
        Assert.NotEmpty(AnnotationPropertiesViewModel.Palette);
        Assert.Equal(
            AnnotationPropertiesViewModel.Palette.Count,
            AnnotationPropertiesViewModel.Palette.Distinct().Count());
    }

    // ---------------------------------------------------------------- 模型层钳制

    [Fact]
    public void WithLineWidthAndOpacity_ClampToAllowedRange()
    {
        var ink = Ink();

        Assert.Equal(AnnotationRecord.MinLineWidth, ink.WithLineWidth(-5).LineWidth, 3);
        Assert.Equal(AnnotationRecord.MaxLineWidth, ink.WithLineWidth(999).LineWidth, 3);
        Assert.Equal(0.05, ink.WithOpacity(0).Opacity, 3);
        Assert.Equal(1.0, ink.WithOpacity(9).Opacity, 3);

        // 不透明度参与内容等价（保存链路据此判断是否需要重写）
        Assert.False(ink.ContentEquals(ink.WithOpacity(0.25)));
        Assert.False(ink.ContentEquals(ink.WithLineWidth(ink.LineWidth + 1)));

        // 文本标记默认半透明（与引擎 add_text_markup_annot 的 0.5 一致）
        Assert.Equal(0.5, Highlight().Opacity, 3);
        Assert.Equal(1.0, ink.Opacity, 3);
    }
}
