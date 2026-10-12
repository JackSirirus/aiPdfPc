// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T4 图章测试：模型工厂（预置/图片/优先级）、预置表与引擎一致性、内容等价、
// 叠加层预览（预置显示标签、图片解码为冻结位图、坏图不阻断）。
using System;
using System.Linq;
using AIPDF.Windows.Annotations;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Reader;

/// <summary>图章批注（预置文字图章与图片图章）的模型与预览投影（纯逻辑，无 UI）。</summary>
public sealed class AnnotationStampTests
{
    private const double A4WidthPt = 595;
    private static double DisplayWidth => A4WidthPt * PageLayoutCalculator.DpiScale;

    /// <summary>1×1 PNG（有效的最小 PNG，用于图片图章）。</summary>
    private static byte[] OnePixelPng => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    // ---------------------------------------------------------------- 模型工厂

    [Fact]
    public void CreateStamp_Preset_StoresToken_AndDefaultsToStampColor()
    {
        var record = AnnotationRecord.CreateStamp(1, new PdfRect(10, 20, 110, 60), "approved");

        Assert.Equal(AnnotationKind.Stamp, record.Kind);
        Assert.Equal(1, record.PageIndex);
        Assert.Equal("approved", record.StampToken);
        Assert.Null(record.StampImage);
        Assert.Equal(StampPresets.DefaultColor, record.Color);
        Assert.Equal(new PdfRect(10, 20, 110, 60), record.Rect);
    }

    [Fact]
    public void CreateStamp_Image_TakesPrecedenceOverToken()
    {
        var image = OnePixelPng;

        var record = AnnotationRecord.CreateStamp(0, new PdfRect(0, 0, 10, 10), "draft", image);

        Assert.Null(record.StampToken);          // 图片优先，token 不落库
        Assert.Same(image, record.StampImage);
        Assert.Equal(AnnotationKind.Stamp, record.Kind);
    }

    [Fact]
    public void CreateStamp_WithoutTokenOrImage_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => AnnotationRecord.CreateStamp(0, new PdfRect(0, 0, 10, 10)));
    }

    [Fact]
    public void StampPresets_MatchEngineTokenSet()
    {
        // 引擎侧 STAMP_PRESETS 共 14 项（backend/engine.py）；两侧 token 必须一致
        Assert.Equal(14, StampPresets.All.Count);
        Assert.Equal(StampPresets.All.Count, StampPresets.All.Select(p => p.Token).Distinct().Count());
        Assert.All(StampPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));

        Assert.Equal("APPROVED", StampPresets.LabelFor("approved"));
        Assert.Equal("TOP SECRET", StampPresets.LabelFor("topsecret"));
        Assert.Equal("APPROVED", StampPresets.LabelFor("APPROVED"));   // 大小写不敏感
        Assert.Equal("UNKNOWN-X", StampPresets.LabelFor("unknown-x")); // 未知 token 原样大写（仅预览）
        Assert.Equal("STAMP", StampPresets.LabelFor(null));
    }

    [Fact]
    public void ContentEquals_ComparesStampTokenAndImage()
    {
        var preset = AnnotationRecord.CreateStamp(0, new PdfRect(0, 0, 10, 10), "approved") with { Id = "s" };
        Assert.True(preset.ContentEquals(preset with { State = AnnotationState.Saved })); // State 不参与
        Assert.False(preset.ContentEquals(preset with { StampToken = "draft" }));
        Assert.False(preset.ContentEquals(preset with { StampImage = OnePixelPng }));

        var image = AnnotationRecord.CreateStamp(0, new PdfRect(0, 0, 10, 10), image: OnePixelPng) with { Id = "i" };
        Assert.True(image.ContentEquals(image with { StampImage = OnePixelPng.ToArray() }));  // 按内容比较
        Assert.False(image.ContentEquals(image with { StampImage = [1, 2, 3] }));
    }

    // ---------------------------------------------------------------- 叠加层预览

    [Fact]
    public void Overlay_PresetStamp_ShowsLabel_AndStaysOffGeometryLayer()
    {
        var record = AnnotationRecord.CreateStamp(0, new PdfRect(10, 20, 210, 80), "confidential");

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        Assert.Equal(AnnotationKind.Stamp, overlay.Kind);
        Assert.Equal("CONFIDENTIAL", overlay.Label);
        Assert.Null(overlay.Image);
        Assert.False(overlay.UsesGeometryLayer);   // 走独立 StampLayer（BoxLayer 无文字承载能力）
        Assert.Null(overlay.Geometry);
    }

    [Fact]
    public void Overlay_ImageStamp_DecodesFrozenPreviewBitmap()
    {
        var record = AnnotationRecord.CreateStamp(0, new PdfRect(10, 20, 210, 80), image: OnePixelPng);

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        Assert.Null(overlay.Label);
        Assert.NotNull(overlay.Image);
        Assert.True(overlay.Image!.IsFrozen, "预览位图必须冻结以复用");
    }

    [Fact]
    public void Overlay_BrokenImageBytes_DoesNotThrow_AndDegradesToEmptyPreview()
    {
        var record = AnnotationRecord.CreateStamp(0, new PdfRect(0, 0, 10, 10), image: [1, 2, 3, 4]);

        var overlay = AnnotationOverlay.FromRecord(record, A4WidthPt, DisplayWidth);

        // 坏图不阻断叠加层（真正下发时引擎会显式报「图章图片格式不受支持」）
        Assert.Null(overlay.Image);
        Assert.Null(overlay.Label);
        Assert.Equal(AnnotationKind.Stamp, overlay.Kind);
    }
}
