// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T2 选区浮菜单纯逻辑测试：选区状态机 / 菜单定位翻转 / 菜单状态机 / 命中 / 几何 / 子菜单路由 / 可用性。
using System.Collections.Generic;
using System.Linq;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Models;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

public sealed class SelectionInputControllerTests
{
    [Fact]
    public void Begin_NoDocument_IsRejected()
    {
        var controller = new SelectionInputController();
        Assert.False(controller.Begin(0, 10, 10, hasDocument: false, canAnnotate: true, hasTextLayer: true));
        Assert.Equal(SelectionGate.DisabledNoDocument, controller.Gate);
        Assert.False(controller.IsDragging);
    }

    [Fact]
    public void Begin_RotatedPage_IsRejected()
    {
        var controller = new SelectionInputController();
        Assert.False(controller.Begin(0, 10, 10, hasDocument: true, canAnnotate: false, hasTextLayer: true));
        Assert.Equal(SelectionGate.DisabledRotated, controller.Gate);
    }

    [Fact]
    public void Begin_NoTextLayer_AllowsRectangleButNotMenu()
    {
        var controller = new SelectionInputController();
        Assert.True(controller.Begin(2, 10, 10, hasDocument: true, canAnnotate: true, hasTextLayer: false));
        Assert.Equal(SelectionGate.DisabledNoTextLayer, controller.Gate);

        controller.Update(2, 60, 40);
        var result = controller.Complete();

        Assert.True(result.HasSelection);          // ST-23：无文本层页仍拉矩形
        Assert.False(result.CanShowMenu);          // 但不弹菜单
        Assert.Equal(2, result.PageIndex);
    }

    [Fact]
    public void Begin_TextLayer_AllowsMenuAfterRealDrag()
    {
        var controller = new SelectionInputController();
        Assert.True(controller.Begin(1, 20, 30, hasDocument: true, canAnnotate: true, hasTextLayer: true));
        controller.Update(1, 120, 48);
        var result = controller.Complete();

        Assert.True(result.HasSelection);
        Assert.True(result.CanShowMenu);
        Assert.Equal(SelectionGate.Enabled, result.Gate);
        Assert.Equal((20.0, 30.0, 100.0, 18.0), result.Rect);
    }

    [Fact]
    public void TinyDrag_IsTreatedAsClick()
    {
        var controller = new SelectionInputController();
        controller.Begin(0, 10, 10, hasDocument: true, canAnnotate: true, hasTextLayer: true);
        controller.Update(0, 10.5, 10.5);
        var result = controller.Complete();
        Assert.False(result.HasSelection);
        Assert.False(result.CanShowMenu);
    }

    [Fact]
    public void CrossPageDrag_DoesNotShowMenu()
    {
        var controller = new SelectionInputController();
        controller.Begin(0, 10, 10, hasDocument: true, canAnnotate: true, hasTextLayer: true);
        controller.Update(1, 40, 40); // 进入下一页
        var result = controller.Complete();

        Assert.True(result.IsCrossPage);
        Assert.False(result.HasSelection);
        Assert.False(result.CanShowMenu);
    }

    [Fact]
    public void MarkCrossPage_FromCaptureSample_DisablesMenu()
    {
        // 鼠标捕获下 pageIndex 不变，视图按越界显式标记跨页
        var controller = new SelectionInputController();
        controller.Begin(0, 10, 10, hasDocument: true, canAnnotate: true, hasTextLayer: true);
        controller.MarkCrossPage();
        controller.Update(0, 60, 40);
        var result = controller.Complete();

        Assert.True(result.IsCrossPage);
        Assert.False(result.CanShowMenu);
    }

    [Fact]
    public void Rect_NormalizesReverseDrag()
    {
        var controller = new SelectionInputController();
        controller.Begin(0, 100, 80, hasDocument: true, canAnnotate: true, hasTextLayer: true);
        controller.Update(0, 40, 20);
        var result = controller.Complete();
        Assert.Equal((40.0, 20.0, 60.0, 60.0), result.Rect);
    }
}

public sealed class FloatingMenuLayoutTests
{
    [Fact]
    public void Compute_PrefersBelowWhenRoom()
    {
        var p = FloatingMenuLayout.Compute((100, 100, 120, 16), (800, 600), (180, 150));
        Assert.True(p.Below);
        Assert.Equal(100 + 16 + FloatingMenuLayout.DefaultGap, p.Top);
    }

    [Fact]
    public void Compute_FlipsAboveWhenNearBottom()
    {
        var p = FloatingMenuLayout.Compute((100, 560, 120, 16), (800, 600), (180, 150));
        Assert.False(p.Below);
        Assert.Equal(560 - FloatingMenuLayout.DefaultGap - 150, p.Top);
    }

    [Fact]
    public void Compute_ClampsHorizontallyIntoViewport()
    {
        var left = FloatingMenuLayout.Compute((0, 100, 20, 16), (800, 600), (180, 150));
        Assert.Equal(FloatingMenuLayout.DefaultMargin, left.Left);

        var right = FloatingMenuLayout.Compute((780, 100, 20, 16), (800, 600), (180, 150));
        Assert.Equal(800 - 180 - FloatingMenuLayout.DefaultMargin, right.Left);
    }

    [Fact]
    public void Compute_DegenerateViewport_ReturnsMargin()
    {
        var p = FloatingMenuLayout.Compute((0, 0, 0, 0), (0, 0), (180, 150));
        Assert.Equal(FloatingMenuLayout.DefaultMargin, p.Left);
        Assert.Equal(FloatingMenuLayout.DefaultMargin, p.Top);
    }
}

public sealed class SelectionMenuStateTests
{
    [Fact]
    public void OpenThenSubmenuThenBack_TransitionsCorrectly()
    {
        var state = new SelectionMenuState();
        Assert.False(state.IsOpen);

        state.Open();
        Assert.Equal(MenuMode.Selection, state.Mode);

        state.OpenToolSubmenu();
        Assert.Equal(MenuMode.ToolSubmenu, state.Mode);

        Assert.True(state.Back());
        Assert.Equal(MenuMode.Selection, state.Mode);

        Assert.True(state.Back());
        Assert.Equal(MenuMode.Closed, state.Mode);
    }

    [Fact]
    public void SubModes_AreIgnoredWhenClosed()
    {
        var state = new SelectionMenuState();
        state.OpenToolSubmenu();
        state.OpenNoteEditor();
        state.OpenColorPalette();
        Assert.Equal(MenuMode.Closed, state.Mode);
        Assert.False(state.Back());
    }

    [Fact]
    public void HoverRecolor_ReturnsToHoverThenClosed()
    {
        var state = new SelectionMenuState();
        state.OpenHover();
        Assert.Equal(MenuMode.HoverActions, state.Mode);

        state.OpenColorPalette();
        Assert.Equal(MenuMode.ColorPalette, state.Mode);

        Assert.True(state.Back());
        Assert.Equal(MenuMode.Selection, state.Mode); // Back 统一回落主菜单
        Assert.True(state.Back());
        Assert.Equal(MenuMode.Closed, state.Mode);
    }

    [Fact]
    public void NoteEditor_TransitionFromOpen()
    {
        var state = new SelectionMenuState();
        state.Open();
        state.OpenNoteEditor();
        Assert.Equal(MenuMode.NoteEditor, state.Mode);
        state.Close();
        Assert.Equal(MenuMode.Closed, state.Mode);
    }
}

public sealed class AnnotationHitTestAndPaletteTests
{
    [Fact]
    public void HitTest_ReturnsTopmostMatch_AndNullWhenMiss()
    {
        var overlays = new List<AnnotationOverlay>
        {
            new("a", AnnotationKind.Highlight, null, 10, 10, 50, 12, null!, null!),
            new("b", AnnotationKind.Highlight, null, 12, 12, 50, 12, null!, null!),
        };

        Assert.Equal("b", AnnotationHitTester.HitTest(overlays, 30, 15));
        Assert.Null(AnnotationHitTester.HitTest(overlays, 200, 200));
    }

    [Fact]
    public void HitTest_UsesPadForThinHighlights()
    {
        var overlays = new List<AnnotationOverlay>
        {
            new("thin", AnnotationKind.Highlight, null, 10, 10, 50, 2, null!, null!),
        };
        Assert.Equal("thin", AnnotationHitTester.HitTest(overlays, 30, 13.5, pad: 2.0));
    }

    [Fact]
    public void Palette_ContainsDefaultYellowFirst()
    {
        Assert.Equal(AnnotationRecord.DefaultHighlightColor, AnnotationPalette.Colors[0].Hex);
        Assert.True(AnnotationPalette.IsPreset(AnnotationRecord.DefaultHighlightColor));
        Assert.False(AnnotationPalette.IsPreset("#123456"));
    }

    [Fact]
    public void GeometryUnion_ComputesBoundingBox()
    {
        var union = AnnotationGeometry.Union([
            new PdfRect(10, 20, 60, 32),
            new PdfRect(12, 40, 58, 52),
        ]);
        Assert.Equal(new PdfRect(10, 20, 60, 52), union);
        Assert.Equal(default, AnnotationGeometry.Union([]));
    }

    [Fact]
    public void CharBoxToPdfRect_FlipsToTopLeftOrigin()
    {
        // PDF 左下原点 (left=10,right=60,bottom=700,top=712)，页高 800 →
        // 左上原点 top = 800-712 = 88, bottom = 800-700 = 100
        var rect = PdfiumTextService.CharBoxToPdfRect(10, 60, 700, 712, 800);
        Assert.Equal(new PdfRect(10, 88, 60, 100), rect);
    }
}

public sealed class ToolSubmenuRoutingTests
{
    [Fact]
    public void Catalog_HasExactlyFourFixedToolsWithPageRangeFlags()
    {
        Assert.Equal(4, ToolSubmenuCatalog.Items.Count);
        Assert.True(ToolSubmenuCatalog.Find("split")!.Value.SupportsPageRange);
        Assert.True(ToolSubmenuCatalog.Find("rotate")!.Value.SupportsPageRange);
        Assert.False(ToolSubmenuCatalog.Find("compress")!.Value.SupportsPageRange);
        Assert.False(ToolSubmenuCatalog.Find("pdf_to_word")!.Value.SupportsPageRange);
        Assert.Null(ToolSubmenuCatalog.Find("merge"));
    }

    [Fact]
    public void SubmenuTools_RouteExactlyLikeExistingToolChannels()
    {
        // V3 T4：固定四项均为多文件向导工具，路由到 Wizard（不再是 V1/V2 的临时标签兜底）。
        var tools = ToolCatalog.Load();
        foreach (var id in ToolSubmenuCatalog.ToolIds)
        {
            var tool = tools.Single(t => t.Id == id);
            Assert.Equal(ToolChannel.Wizard, ToolChannels.ChannelFor(tool));
            Assert.False(ToolChannels.RoutesToRightDrawer(tool));
        }
    }

    [Fact]
    public void CheckUnavailableTool_IsDisabledWithReason()
    {
        var original = ToolAvailabilityProbe.OfficeDependencyProbe;
        try
        {
            ToolAvailabilityProbe.OfficeDependencyProbe = _ => "需要安装 LibreOffice";
            var pdfToWord = ToolAvailabilityProbe.Check("pdf_to_word");
            Assert.False(pdfToWord.Available);
            Assert.Equal("需要安装 LibreOffice", pdfToWord.DisabledReason);

            // 非 LibreOffice 依赖的固定工具不受影响
            Assert.True(ToolAvailabilityProbe.Check("compress").Available);
            Assert.True(ToolAvailabilityProbe.Check("split").Available);
            Assert.True(ToolAvailabilityProbe.Check("rotate").Available);
        }
        finally
        {
            ToolAvailabilityProbe.OfficeDependencyProbe = original;
        }
    }

    [Fact]
    public void CheckAvailableTool_IsEnabled()
    {
        var original = ToolAvailabilityProbe.OfficeDependencyProbe;
        try
        {
            ToolAvailabilityProbe.OfficeDependencyProbe = _ => null;
            var availability = ToolAvailabilityProbe.Check("pdf_to_word");
            Assert.True(availability.Available);
            Assert.Null(availability.DisabledReason);
        }
        finally
        {
            ToolAvailabilityProbe.OfficeDependencyProbe = original;
        }
    }
}
