// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T1 批注渲染语料定位：缺失时经仓库 .venv PyMuPDF 调 tmp/t1-annot-corpus/gen_annot_corpus.py 自动生成
// （与 T2Corpus 同模式；全部合成文件，不读个人文档）。
using System.Diagnostics;
using System.IO;

namespace AIPDF.Windows.Tests.Rendering;

/// <summary>批注渲染语料（Print 旗标高亮 / FreeText）。</summary>
internal static class T1AnnotCorpus
{
    private static readonly Lazy<string> LazyRoot = new(FindRepoRoot);
    private static readonly Lazy<bool> LazyEnsured = new(EnsureCore);

    public static string RepoRoot => LazyRoot.Value;

    public static string CorpusDir => Path.Combine(RepoRoot, "tmp", "t1-annot-corpus", "corpus");

    public static string PrintTrue => Path.Combine(CorpusDir, "annot-print-true.pdf");
    public static string PrintFalse => Path.Combine(CorpusDir, "annot-print-false.pdf");
    public static string FreeText => Path.Combine(CorpusDir, "annot-freetext.pdf");

    /// <summary>确保语料存在（缺失时生成一次）。</summary>
    public static void Ensure()
    {
        if (!LazyEnsured.Value)
            throw new InvalidOperationException("T1 批注语料生成失败，详见内部异常/输出。");
    }

    private static bool EnsureCore()
    {
        if (File.Exists(PrintTrue) && File.Exists(PrintFalse) && File.Exists(FreeText))
            return true;

        var script = Path.Combine(RepoRoot, "tmp", "t1-annot-corpus", "gen_annot_corpus.py");
        if (!File.Exists(script))
            throw new FileNotFoundException($"gen_annot_corpus.py 不存在: {script}（tmp/ 不入库，需本机存在）");

        var psi = new ProcessStartInfo(FindPython(), $"\"{script}\" \"{CorpusDir}\"")
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
            throw new InvalidOperationException($"批注语料生成失败 ({p.ExitCode})\n{stdout}\n{stderr}");
        return File.Exists(PrintTrue) && File.Exists(PrintFalse) && File.Exists(FreeText);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tmp", "t1-annot-corpus", "gen_annot_corpus.py")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("未找到仓库根（tmp/t1-annot-corpus/gen_annot_corpus.py 的祖先目录）");
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
