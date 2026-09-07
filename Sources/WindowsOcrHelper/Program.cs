// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors

// Windows.Media.Ocr 版 OCR helper，CLI 契约镜像上游 Sources/VisionHelper/main.swift。
// 引擎（backend/engine.py）按位置参数调用：
//   WindowsOcrHelper.exe ocr <image>      → stdout 输出框数组 JSON（归一化坐标）
//   WindowsOcrHelper.exe image <in> <out> → 任意图片重编码为 PNG（load_image 兜底）
//   WindowsOcrHelper.exe scan <in> <out>  → Windows 无透视校正，退化为重编码 PNG
// 另有维护/自检模式：
//   WindowsOcrHelper.exe --list-languages  → 可用 OCR 语言列表 JSON
//   WindowsOcrHelper.exe --selftest        → 对内置小图执行 OCR 并输出 JSON
// 无参数时按 stdin JSON 请求运行（备用协议，与上游引擎主协议一致）。
//
// 失败时写 stderr 并返回非零退出码，引擎 run_process 据此抛 UserError，
// 避免 OCR 失败被静默当作「无文本」输出。

using System.Text;
using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WindowsOcrHelper;

internal static class Program
{
    // 与上游 VisionHelper 对齐：OCR 结果每行输出一个框，坐标为相对图像宽高的
    // 归一化值（左上角原点），text 为该行完整文本。
    private sealed record OcrBox(string text, double x, double y, double w, double h);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 紧凑单行输出，与 Swift 端 JSONSerialization 默认一致。
        WriteIndented = false,
    };

    // 与上游 VisionHelper 的语言顺序一致：优先简体中文、繁体中文，其次英文。
    private static readonly string[] PreferredLanguages =
    {
        "zh-Hans", "zh-Hans-CN", "zh-CN",
        "zh-Hant", "zh-Hant-TW", "zh-TW",
        "en-US", "en-GB", "en",
    };

    private static int Main(string[] args)
    {
        // 引擎在 win32 下按 UTF-8 解码 stdout；强制 UTF-8 保证中文 OCR 文本不乱码。
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WindowsOcrHelper: " + ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return RunFromStdin();
        }
        switch (args[0])
        {
            case "--list-languages":
            case "-l":
                return ListLanguages();
            case "--selftest":
            case "-s":
                return SelfTest();
            case "ocr":
                RequireArgCount(args, 2, "ocr <image>");
                return Ocr(args[1]);
            case "image":
            case "scan":
                // Windows 无 Apple Vision 透视校正，scan 退化为纯格式转换（重编码 PNG）。
                RequireArgCount(args, 3, $"{args[0]} <input> <output>");
                return Reencode(args[1], args[2]);
            default:
                Console.Error.WriteLine(
                    "用法: WindowsOcrHelper ocr <image> | image <in> <out> | scan <in> <out> | --list-languages | --selftest");
                return 2;
        }
    }

    private static void RequireArgCount(string[] args, int count, string usage)
    {
        if (args.Length < count)
        {
            throw new ArgumentException($"参数不足，用法: {usage}");
        }
    }

    // ocr <image>：与上游 VisionHelper `ocr` 模式完全对齐，stdout 输出框数组 JSON。
    private static int Ocr(string imagePath)
    {
        var engine = CreateEngine();
        using var bitmap = LoadBitmap(imagePath);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var boxes = BuildBoxes(result, bitmap);
        Console.Out.Write(JsonSerializer.Serialize(boxes, JsonOpts));
        return 0;
    }

    // image/scan：重编码为 PNG。Windows 无透视校正能力，scan 即 image 的等价操作。
    private static int Reencode(string inPath, string outPath)
    {
        using var bitmap = LoadBitmap(inPath);
        using var fs = File.Create(outPath);
        using var stream = fs.AsRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask().GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    // --list-languages：输出可用 OCR 语言列表 JSON，供 UI 语言下拉使用。
    private static int ListLanguages()
    {
        var langs = OcrEngine.AvailableRecognizerLanguages
            .OrderBy(l => l.LanguageTag, StringComparer.OrdinalIgnoreCase)
            .Select(l => new { tag = l.LanguageTag, name = l.DisplayName })
            .ToList();
        Console.Out.Write(JsonSerializer.Serialize(langs, JsonOpts));
        return 0;
    }

    // --selftest：对内置小图执行 OCR，输出 ok/语言/合并文本/框数组。
    private static int SelfTest()
    {
        var engine = CreateEngine();
        using var bitmap = DecodeEmbedded();
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var boxes = BuildBoxes(result, bitmap);
        var text = string.Join(" ", boxes.Select(b => b.text));
        Console.Out.Write(JsonSerializer.Serialize(new
        {
            ok = true,
            language = engine.RecognizerLanguage.LanguageTag,
            text = text,
            boxes = boxes,
        }, JsonOpts));
        return 0;
    }

    // 无参数时按 stdin JSON 请求运行：{"mode":"ocr","image":"..."}
    // 或 {"mode":"image"|"scan","image":"...","output":"..."}。
    private static int RunFromStdin()
    {
        var input = Console.In.ReadToEnd();
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("无参数且 stdin 为空。用法: WindowsOcrHelper ocr <image> ...");
        }
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        var mode = root.GetProperty("mode").GetString() ?? "ocr";
        var image = root.GetProperty("image").GetString() ?? "";
        return mode switch
        {
            "ocr" => Ocr(image),
            "image" or "scan" => Reencode(image, root.GetProperty("output").GetString() ?? ""),
            _ => throw new ArgumentException($"未知 mode: {mode}"),
        };
    }

    private static OcrEngine CreateEngine()
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        // 优先匹配：先精确，再按主语言子标签前缀匹配（兼容 zh-Hans-CN vs zh-Hans 差异）。
        foreach (var pref in PreferredLanguages)
        {
            var match = available.FirstOrDefault(l =>
                l.LanguageTag.Equals(pref, StringComparison.OrdinalIgnoreCase) ||
                l.LanguageTag.StartsWith(pref + "-", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(match);
                if (engine is not null)
                {
                    return engine;
                }
            }
        }
        var profile = OcrEngine.TryCreateFromUserProfileLanguages();
        if (profile is not null)
        {
            return profile;
        }
        foreach (var lang in available)
        {
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is not null)
            {
                return engine;
            }
        }
        throw new InvalidOperationException(
            "本机没有可用的 OCR 识别器语言。请在「设置 → 时间和语言 → 语言」中安装 OCR 语言包。");
    }

    private static SoftwareBitmap LoadBitmap(string path)
    {
        using var fs = File.OpenRead(path);
        using var stream = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();
    }

    private static List<OcrBox> BuildBoxes(OcrResult result, SoftwareBitmap bitmap)
    {
        double width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var boxes = new List<OcrBox>();
        foreach (var line in result.Lines)
        {
            var words = line.Words;
            if (words.Count == 0)
            {
                continue;
            }
            // 行框 = 该行所有单词外接矩形的并集，与上游 Vision 每行一个 observation 对齐。
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var word in words)
            {
                var r = word.BoundingRect;
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);
            }
            boxes.Add(new OcrBox(
                line.Text,
                Math.Round(minX / width, 6),
                Math.Round(minY / height, 6),
                Math.Round((maxX - minX) / width, 6),
                Math.Round((maxY - minY) / height, 6)));
        }
        return boxes;
    }

    // 内置自检图：白底黑字大号 "AIPDF TEST"（Arial 80px），可被任一英文字体 OCR 稳定识别。
    private static SoftwareBitmap DecodeEmbedded()
    {
        var bytes = Convert.FromBase64String(SelfTestPngBase64);
        using var ms = new MemoryStream(bytes);
        using var stream = ms.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();
    }

    private const string SelfTestPngBase64 =
        "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";
}
