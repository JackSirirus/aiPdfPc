// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T2 合成语料定位与自动生成（全部合成文件，不读个人文档）。语料 PDF 落 tmp/（不提交）。
using System.Diagnostics;
using System.IO;

namespace AIPDF.Windows.Tests.Search;

/// <summary>
/// T2 测试语料：缺失时经仓库 .venv PyMuPDF 调 tmp/t2-corpus/gen_corpus.py 自动生成
/// （与 spike 的 Corpus.Ensure 同模式）。
/// </summary>
internal static class T2Corpus
{
    private static readonly Lazy<string> LazyRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> LazyEnsured = new(EnsureCore);

    /// <summary>仓库根目录（从测试输出目录向上找包含 tmp/t2-corpus/gen_corpus.py 的祖先）。</summary>
    public static string RepoRoot => LazyRoot.Value;

    public static string CorpusDir => Path.Combine(RepoRoot, "tmp", "t2-corpus", "corpus");

    public static string Cjk500 => Path.Combine(CorpusDir, "t2-cjk500.pdf");
    public static string Scanned => Path.Combine(CorpusDir, "t2-scanned.pdf");
    public static string Mixed => Path.Combine(CorpusDir, "t2-mixed.pdf");
    public static string Garbage => Path.Combine(CorpusDir, "t2-garbage.pdf");

    /// <summary>语料标记锚点（与 gen_corpus.py 保持一致）。</summary>
    public const string CjkFixedMarker = "青苹果检索标记";
    public static readonly int[] CjkFixturePages = { 10, 110, 310 };
    public const string EnglishMarker = "ZEBRA-QUARTZ";
    public static readonly int[] EnglishFixturePages = { 5, 200 };

    /// <summary>确保语料存在（缺失时生成一次）。</summary>
    public static void Ensure()
    {
        if (!LazyEnsured.Value)
            throw new InvalidOperationException("T2 语料生成失败，详见内部异常/输出。");
    }

    private static bool EnsureCore()
    {
        if (File.Exists(Cjk500) && File.Exists(Scanned) && File.Exists(Mixed) && File.Exists(Garbage))
            return true;

        var script = Path.Combine(RepoRoot, "tmp", "t2-corpus", "gen_corpus.py");
        if (!File.Exists(script))
            throw new FileNotFoundException($"gen_corpus.py 不存在: {script}（tmp/ 不入库，需本机存在）");

        var python = FindPython();
        var psi = new ProcessStartInfo(python, $"\"{script}\" \"{CorpusDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"语料生成失败 ({p.ExitCode})\n{stdout}\n{stderr}");
        return File.Exists(Cjk500) && File.Exists(Scanned) && File.Exists(Mixed) && File.Exists(Garbage);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tmp", "t2-corpus", "gen_corpus.py")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("未找到仓库根（tmp/t2-corpus/gen_corpus.py 的祖先目录）");
    }

    private static string FindPython()
    {
        var dir = new DirectoryInfo(RepoRoot);
        while (dir is not null)
        {
            var venvPy = Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPy)) return venvPy;
            var venvPyUnix = Path.Combine(dir.FullName, ".venv", "bin", "python");
            if (File.Exists(venvPyUnix)) return venvPyUnix;
            dir = dir.Parent!;
        }
        return "python";
    }
}
