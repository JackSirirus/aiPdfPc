// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T8 向导接入（<see cref="AIPDF.Windows.Views.Wizard.IWizardToolAdapter"/>）的共享最小助手：
// 把各工具 VM 的“提交时刻参数/输出目录”固化为快照项，并把引擎结果归一为向导结果。
// 刻意只做数据搬运，不改任何既有执行语义、参数 UI 或引擎 operation；也不自建队列。
using System.IO;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Views.Wizard;

namespace AIPDF.Windows.Tools;

/// <summary>
/// 5 个工具 VM 基类实现 <see cref="IWizardToolAdapter"/> 时的共享纯逻辑（可单测）：
/// 输出目录解析（复用工具页“默认输出目录 = 源文件旁”的既有语义）、快照构建、引擎结果归一。
/// </summary>
internal static class WizardAdapterSupport
{
    /// <summary>
    /// 解析向导提交使用的输出目录：优先用户/页面已选的有效目录；为空时回退到首个暂存文件所在目录
    /// （与各工具页 <c>AddFiles</c> 的默认行为一致，最终仍由引擎在其下新建 AIPDF-日期-编号 任务子目录）。
    /// 返回错误文案（null 放行），并给出 <paramref name="resolved"/>。
    /// </summary>
    public static string? ResolveOutputDir(string configured, IReadOnlyList<string> stagedFiles, out string resolved)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);

        var trimmed = configured?.Trim() ?? "";
        if (trimmed.Length > 0)
        {
            resolved = trimmed;
            return Path.IsPathRooted(trimmed) && Directory.Exists(trimmed) ? null : "请选择有效的输出文件夹。";
        }

        if (stagedFiles.Count > 0
            && Path.GetDirectoryName(stagedFiles[0]) is { Length: > 0 } directory
            && Directory.Exists(directory))
        {
            resolved = directory;
            return null;
        }

        resolved = "";
        return "请选择有效的输出文件夹。";
    }

    /// <summary>
    /// 提交时刻快照：多文件工具返回 1 项（一个多文件队列任务）；单文件工具返回 N 项
    /// （每文件一个任务）。参数/输出目录由调用方在提交时刻读取并固化（review M2 / Q8）。
    /// </summary>
    public static IReadOnlyList<WizardSubmitItem> BuildItems(
        Tool tool,
        IReadOnlyList<string> stagedFiles,
        IReadOnlyDictionary<string, object?> options,
        string outputDir)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(stagedFiles);

        if (tool.Multiple)
        {
            return [new WizardSubmitItem(tool.Name, stagedFiles, options, outputDir)];
        }

        return [.. stagedFiles.Select(file => new WizardSubmitItem(tool.Name, [file], options, outputDir))];
    }

    /// <summary>引擎结果归一：成功带输出文件/任务目录，失败带引擎原文（业务错误不抛异常）。</summary>
    public static WizardItemResult ToResult(EngineResult result)
        => result.Ok
            ? new WizardItemResult(true, [.. result.Outputs], null, result.OutputDir)
            : WizardItemResult.Failure(result.Error);

    /// <summary>
    /// 由已固化快照构造引擎请求。执行期只读 <see cref="WizardSubmitItem"/>，绝不回读 VM 可变状态
    /// （review M2：提交后修改 VM 不影响已入队项）。
    /// </summary>
    public static EngineRequest BuildRequest(string operationId, WizardSubmitItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new EngineRequest
        {
            Operation = operationId,
            Files = [.. item.Files],
            Options = new Dictionary<string, object?>(item.Options, StringComparer.Ordinal),
            OutputDir = item.OutputDir,
        };
    }
}
