// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V4 T13：smart 反色处理器（对标 SumatraPDF 的夜间模式）。
//
// 与「无脑全反色」的区别：smart 模式**只反色低饱和度像素**（灰阶文字 / 白底），彩色像素
// （照片、彩色图表）原样保留——否则整篇文档的照片会变成底片（S3 实测：照片区域平均差异
// 由 90.9 降到 7.0）。
//
// 纯像素变换、无副作用、可独立单测；调用方（渲染服务）负责把它放在后台线程并纳入缓存键。
using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPDF.Windows.Rendering;

/// <summary>阅读配色模式（V4 T13）。</summary>
public enum ColorInversionMode
{
    /// <summary>原样（默认；打印 / 导出路径必须用此值）。</summary>
    Off,

    /// <summary>整页反色（含图像）。</summary>
    Invert,

    /// <summary>智能反色：仅反色低饱和度像素，图像保持原样。</summary>
    SmartInvert,
}

/// <summary>阅读配色像素变换。</summary>
public static class ColorInversionProcessor
{
    /// <summary>
    /// smart 模式的饱和度阈值：低于等于此值视为「文字 / 背景」→ 反色；高于则视为「图像」→ 保留。
    /// 取值依据 S3 尖刺（.omo/evidence/v4-spike-recolor.md）：0.15 时照片几乎不被触碰，
    /// 而灰阶文字与白底正常反色。
    /// </summary>
    public const double SaturationThreshold = 0.15;

    /// <summary>超过此像素数时按行并行（大页 384dpi 下顺序循环约 72ms，并行可摊到多核）。</summary>
    private const int ParallelThresholdPixels = 1_000_000;

    /// <summary>
    /// 按模式变换位图；<see cref="ColorInversionMode.Off"/> 原样返回（不复制）。
    /// 返回的位图已 <c>Freeze()</c>，可跨线程使用。
    /// </summary>
    public static BitmapSource Apply(BitmapSource? source, ColorInversionMode mode)
    {
        if (source is null || mode == ColorInversionMode.Off)
        {
            return source!;
        }

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return source;
        }

        var stride = width * 4;
        var pixels = new byte[(long)stride * height <= int.MaxValue ? stride * height : 0];
        if (pixels.Length == 0)
        {
            return source;
        }

        converted.CopyPixels(pixels, stride, 0);

        if (mode == ColorInversionMode.Invert)
        {
            InvertAll(pixels);
        }
        else if (width * height >= ParallelThresholdPixels)
        {
            Parallel.For(0, height, y => InvertLowSaturationRow(pixels, y * stride, width));
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                InvertLowSaturationRow(pixels, y * stride, width);
            }
        }

        var result = BitmapSource.Create(
            width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static void InvertAll(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(255 - pixels[i]);
            pixels[i + 1] = (byte)(255 - pixels[i + 1]);
            pixels[i + 2] = (byte)(255 - pixels[i + 2]);
            // 第 4 字节是 alpha：不反色（否则透明区会变成不透明）
        }
    }

    /// <summary>反色一行中饱和度低于阈值的像素（BGRA，行内 offset 起）。</summary>
    private static void InvertLowSaturationRow(byte[] pixels, int offset, int width)
    {
        var end = offset + width * 4;
        for (var i = offset; i < end; i += 4)
        {
            int blue = pixels[i];
            int green = pixels[i + 1];
            int red = pixels[i + 2];

            var max = Math.Max(red, Math.Max(green, blue));
            if (max == 0)
            {
                // 纯黑（饱和度无定义）：当作文字反成白
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                continue;
            }

            var min = Math.Min(red, Math.Min(green, blue));
            if ((max - min) / (double)max > SaturationThreshold)
            {
                continue;   // 彩色像素（照片 / 图表）：保留原样
            }

            pixels[i] = (byte)(255 - blue);
            pixels[i + 1] = (byte)(255 - green);
            pixels[i + 2] = (byte)(255 - red);
        }
    }
}
