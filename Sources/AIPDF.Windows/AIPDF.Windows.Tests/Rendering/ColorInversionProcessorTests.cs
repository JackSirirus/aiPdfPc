// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T13 阅读配色测试：
//   - 处理器：Off 原样、Invert 全反、SmartInvert 只反低饱和度像素（照片/彩色图形保留）、alpha 不动；
//   - 渲染服务：**缓存键必须含配色模式**（S3 高危点：漏了会串色，夜间模式拿到白底位图）。
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIPDF.Windows.Rendering;
using AIPDF.Windows.Tests.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Rendering;

/// <summary>阅读配色（smart 反色）。</summary>
public sealed class ColorInversionProcessorTests
{
    // 三个像素：白底 / 黑字 / 纯红（模拟照片里的彩色像素）
    private static BitmapSource Sample() => FromBgra(3, 1,
    [
        255, 255, 255, 255,   // 白（背景）
        0, 0, 0, 255,         // 黑（文字）
        0, 0, 255, 255,       // 红（BGRA 顺序：B=0,G=0,R=255）
    ]);

    private static BitmapSource FromBgra(int width, int height, byte[] pixels)
        => BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);

    private static byte[] ToBgra(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] pixels, int index)
        => (pixels[index * 4], pixels[index * 4 + 1], pixels[index * 4 + 2], pixels[index * 4 + 3]);

    [Fact]
    public void Apply_Off_ReturnsSameInstance()
    {
        var source = Sample();
        Assert.Same(source, ColorInversionProcessor.Apply(source, ColorInversionMode.Off));
    }

    [Fact]
    public void Apply_Invert_InvertsRgbAndKeepsAlpha()
    {
        var result = ToBgra(ColorInversionProcessor.Apply(Sample(), ColorInversionMode.Invert));

        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Pixel(result, 0));       // 白 → 黑
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Pixel(result, 1)); // 黑 → 白
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)255), Pixel(result, 2));   // 红 → 青
    }

    [Fact]
    public void Apply_SmartInvert_InvertsGrayscale_ButKeepsSaturatedColors()
    {
        var result = ToBgra(ColorInversionProcessor.Apply(Sample(), ColorInversionMode.SmartInvert));

        // 白底 → 黑底；黑字 → 白字（饱和度 0）
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Pixel(result, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Pixel(result, 1));
        // 纯红（饱和度 1.0 > 阈值）→ **原样保留**（照片不会被反成底片）
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Pixel(result, 2));
    }

    [Fact]
    public void Apply_SmartInvert_KeepsAlphaTransparent()
    {
        // 半透明像素：alpha 不得被反色（否则透明区会变不透明）
        var source = FromBgra(1, 1, [255, 255, 255, 64]);
        var result = ToBgra(ColorInversionProcessor.Apply(source, ColorInversionMode.SmartInvert));
        Assert.Equal(64, Pixel(result, 0).A);
    }

    [Fact]
    public async Task RenderService_CacheKeyIncludesColorMode_SoModesDoNotShareBitmaps()
    {
        // S3 高危点：缓存键原本是 (page, dpi, zoom) —— 缺配色维度会让夜间模式命中白底位图。
        using var service = new PdfiumRenderService();
        var docId = service.OpenDocument(Corpus.Mixed100);

        var off = await service.RenderPageAsync(docId, 0, 96, 100);
        var smart = await service.RenderPageAsync(docId, 0, 96, 100, ColorInversionMode.SmartInvert);

        Assert.NotSame(off, smart);
        Assert.NotEqual(ToBgra(off), ToBgra(smart));

        // 同模式再次请求必须命中缓存（返回同一实例）
        Assert.Same(off, await service.RenderPageAsync(docId, 0, 96, 100));
        Assert.Same(smart, await service.RenderPageAsync(docId, 0, 96, 100, ColorInversionMode.SmartInvert));

        // 两种模式各 miss 一次，随后各 hit 一次
        var stats = service.GetLruStats(docId);
        Assert.Equal(2, stats.Hits);
        Assert.Equal(2, stats.Misses);
    }
}
