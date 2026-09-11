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

    /// <summary>
    /// 生成带 /Rotate 的合成 PDF（T2 旋转实证用）：每页 MediaBox 相同，/Rotate 取 0/90/180/270。
    /// 每页画主体色块 + 左上角深色小方块（旋转可见性标记）。
    /// </summary>
    public static byte[] CreateRotated(double widthPt, double heightPt, params int[] rotations)
    {
        if (rotations.Length == 0) rotations = new[] { 0 };

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, rotations.Length).Select(i => $"{3 + i * 2} 0 R"))}] /Count {rotations.Length} >>",
        };

        for (var i = 0; i < rotations.Length; i++)
        {
            var contentIndex = 4 + i * 2;
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(widthPt)} {Fmt(heightPt)}] /Rotate {rotations[i]} /Contents {contentIndex} 0 R >>");
            var content =
                $"q 0 0 1 rg 20 20 {Fmt(widthPt - 40)} {Fmt(heightPt - 40)} re f Q " +
                $"q 0 0 0 rg 0 0 40 40 re f Q";
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

    /// <summary>
    /// 带文字的合成 PDF：每页除色块外用标准 14 字体 Helvetica 写一行 ASCII 文本，
    /// 供 pypdf extract_text 校验（如加密/解锁往返后文本仍可读）。
    /// </summary>
    public static byte[] CreateWithText(params (double WidthPt, double HeightPt, string Text)[] pages)
    {
        // 对象布局：1 = Catalog，2 = Pages，页 i 的 Page 对象 = 3 + 2i，内容流 = 4 + 2i，字体对象 = 3 + 2×页数
        var fontObject = 3 + pages.Length * 2;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => $"{3 + i * 2} 0 R"))}] /Count {pages.Length} >>",
        };

        for (var i = 0; i < pages.Length; i++)
        {
            var (widthPt, heightPt, text) = pages[i];
            var contentIndex = 4 + i * 2;
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(widthPt)} {Fmt(heightPt)}] /Contents {contentIndex} 0 R /Resources << /Font << /F1 {fontObject} 0 R >> >> >>");
            var content = $"BT /F1 14 Tf 72 720 Td ({EscapePdfText(text)}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

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

    /// <summary>PDF 字符串字面量转义（反斜杠与括号）；文本请保持 ASCII（/Length 按 ASCII 字节计）。</summary>
    private static string EscapePdfText(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static int ByteLength(StringBuilder builder) => Encoding.ASCII.GetByteCount(builder.ToString());
}
