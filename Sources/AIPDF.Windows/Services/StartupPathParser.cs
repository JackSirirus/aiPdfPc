// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.IO;

namespace AIPDF.Windows.Services;

/// <summary>
/// 启动参数解析（FR-32 / 4.7.1）：从命令行或 <c>.pdf</c> 关联传入的参数里挑出可打开的 PDF。
/// 纯逻辑、可单测：文件存在性以委托注入，不在解析层触碰磁盘，避免测试依赖真实文件。
/// 多文件逐一返回（宿主每文件开一个标签）；同路径（忽略大小写）去重。
/// </summary>
public static class StartupPathParser
{
    private const string PdfExtension = ".pdf";

    /// <summary>
    /// 过滤启动参数：仅保留「.pdf 扩展名 + 存在 + 去重」的规范化全路径。
    /// 空白项、非 PDF、不存在文件、非法路径、带内嵌引号的异常项一律跳过，绝不抛异常阻断启动。
    /// </summary>
    /// <param name="args">命令行参数（<see cref="System.Windows.StartupEventArgs.Args"/>）。</param>
    /// <param name="fileExists">存在性判定（生产传 <see cref="File.Exists"/>；测试注入假实现）。</param>
    public static IReadOnlyList<string> Collect(IEnumerable<string> args, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(fileExists);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // 关联注册命令串是 "<exe>" "%1"，个别启动器会把引号一并带进参数；防御性剥离。
            var candidate = raw.Trim().Trim('"');
            if (candidate.Length == 0
                || !candidate.EndsWith(PdfExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                continue; // 非法路径字符等：忽略该项
            }

            if (!fileExists(fullPath) || !seen.Add(fullPath))
            {
                continue;
            }

            result.Add(fullPath);
        }

        return result;
    }
}
