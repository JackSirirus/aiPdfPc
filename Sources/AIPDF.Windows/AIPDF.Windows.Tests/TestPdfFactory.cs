// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Globalization;
using System.Text;

namespace AIPDF.Windows.Tests;

/// <summary>生成合成 PDF（纯 ASCII 手工构造，无需外部依赖），页尺寸可指定。</summary>
internal static class TestPdfFactory
{
    /// <summary>按给定页尺寸序列生成多页 PDF；每页画一个色块以便渲染结果非空。</summary>
    public static byte[] Create(params (double WidthPt, double HeightPt)[] pages)
    {
        // 对象布局：1 = Catalog，2 = Pages，页 i 的 Page 对象 = 3 + 2i，内容流 = 4 + 2i
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => $"{3 + i * 2} 0 R"))}] /Count {pages.Length} >>",
        };

        for (var i = 0; i < pages.Length; i++)
        {
            var (widthPt, heightPt) = pages[i];
            var contentIndex = 4 + i * 2;
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(widthPt)} {Fmt(heightPt)}] /Contents {contentIndex} 0 R >>");
            var color = i % 2 == 0 ? "0 0 1" : "0 1 0";
            var content = $"q {color} rg 20 20 {Fmt(widthPt - 40)} {Fmt(heightPt - 40)} re f Q";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = ByteLength(builder);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefOffset = ByteLength(builder);
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static int ByteLength(StringBuilder builder) => Encoding.ASCII.GetByteCount(builder.ToString());
}
