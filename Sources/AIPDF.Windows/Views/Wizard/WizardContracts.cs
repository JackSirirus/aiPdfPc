// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T4 向导骨架契约：步骤契约 + 文件暂存条目 + 提交快照 + 宿主适配口。
// 刻意与 5 个工具 VM 基类解耦（T8 才以最小注入点实现 <see cref="IWizardToolAdapter"/>）：
// T4 只定义“宿主看见什么”，既不重写参数 UI，也不改既有 AddFiles/执行语义。
using System.Collections.ObjectModel;

namespace AIPDF.Windows.Views.Wizard;

/// <summary>
/// 向导单步契约（标题 + 内容元素 + 内联校验【ST-25】）。
/// 默认步骤语义 = 统一 3 步「文件暂存 → 参数 → 执行结果」（裁决 Q2）；
/// 参数步内容复用现有工具页 UI 实例（裁决 Q1 / D16），逐工具例外（workflow 多步）经适配口追加。
/// </summary>
public interface IWizardStep
{
    /// <summary>步骤标题（步骤条展示）。</summary>
    string Title { get; }

    /// <summary>步骤内容元素（参数步 = 承载的工具页；文件暂存/结果步由模态宿主自建，可为 null）。</summary>
    object? Content { get; }

    /// <summary>是否为终态步（执行结果步）：终态步不可再前进。</summary>
    bool IsFinal { get; }

    /// <summary>内联校验：返回错误文案（null 放行）。</summary>
    string? Validate();
}

/// <summary>通用步骤实现：以委托提供校验，供默认 3 步与 workflow 追加步复用。</summary>
public sealed class WizardStep : IWizardStep
{
    private readonly Func<string?>? _validate;

    public WizardStep(string title, Func<string?>? validate = null, object? content = null, bool isFinal = false)
    {
        Title = title;
        _validate = validate;
        Content = content;
        IsFinal = isFinal;
    }

    public string Title { get; }

    public object? Content { get; }

    public bool IsFinal { get; }

    public string? Validate() => _validate?.Invoke();
}

/// <summary>
/// 向导文件暂存条目（仅保存路径，不读内容）。当前文档自动入选置顶并标记「当前文档」【ST-26】。
/// </summary>
public sealed class WizardStagedFile
{
    public WizardStagedFile(string path, bool isCurrentDocument = false)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        IsCurrentDocument = isCurrentDocument;
    }

    /// <summary>规范化后的完整路径。</summary>
    public string Path { get; }

    /// <summary>文件名（列表展示）。</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>是否为宿主的当前文档（徽标「当前文档」）。</summary>
    public bool IsCurrentDocument { get; }

    /// <summary>徽标文案（非当前文档为空）。</summary>
    public string? Badge => IsCurrentDocument ? "当前文档" : null;
}

/// <summary>
/// 提交快照：批量提交对每个队列项在提交时刻固化 <see cref="Files"/> / <see cref="Options"/> / <see cref="OutputDir"/>，
/// 延迟任务不得读取被后续修改的 VM 状态（review M2）。构造即深拷贝，之后不可变。
/// </summary>
public sealed class WizardSubmitItem
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyOptions =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    public WizardSubmitItem(
        string name,
        IEnumerable<string> files,
        IReadOnlyDictionary<string, object?>? options,
        string outputDir)
    {
        ArgumentNullException.ThrowIfNull(files);
        Name = string.IsNullOrWhiteSpace(name) ? "任务" : name;
        Files = [.. files];
        Options = options is null
            ? EmptyOptions
            : new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(options, StringComparer.Ordinal));
        OutputDir = outputDir ?? "";
    }

    /// <summary>队列展示名（工具名）。</summary>
    public string Name { get; }

    /// <summary>提交时刻固化的文件列表。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>提交时刻固化的引擎选项。</summary>
    public IReadOnlyDictionary<string, object?> Options { get; }

    /// <summary>提交时刻固化的输出目录（父目录，引擎在其下新建任务子目录）。</summary>
    public string OutputDir { get; }
}

/// <summary>
/// 单个队列项的执行结果（执行结果步展示；宿主据此决定开新标签 / Toast+打开文件夹）。
/// </summary>
public sealed class WizardItemResult
{
    public WizardItemResult(bool ok, IReadOnlyList<string>? outputs = null, string? error = null, string? outputDir = null)
    {
        Ok = ok;
        Outputs = outputs ?? [];
        Error = error;
        OutputDir = outputDir;
    }

    /// <summary>是否成功。</summary>
    public bool Ok { get; }

    /// <summary>引擎输出文件路径（位于新建任务子目录内）。</summary>
    public IReadOnlyList<string> Outputs { get; }

    /// <summary>失败原因（成功为 null）。</summary>
    public string? Error { get; }

    /// <summary>引擎返回的任务输出目录（可为 null）。</summary>
    public string? OutputDir { get; }

    /// <summary>失败结果工厂。</summary>
    public static WizardItemResult Failure(string? error) => new(false, error: error);
}

/// <summary>
/// 向导宿主与工具 VM 之间的适配口（T8 由 5 个 VM 基类实现）。
/// 参数步复用既有工具页/VM：参数编辑仍在工具页内完成，本接口只负责
/// “按暂存文件 + 当前参数生成提交快照”与“执行单个快照项”，不重写参数 UI。
/// </summary>
public interface IWizardToolAdapter
{
    /// <summary>参数步内联校验【ST-25】：返回错误文案（null 放行）。</summary>
    string? ValidateParameters(IReadOnlyList<string> stagedFiles);

    /// <summary>
    /// 提交时快照：多文件工具返回 1 项（一个多文件队列任务）；单文件工具返回 N 项（每文件一个任务）。
    /// 实现方必须在该方法内读取当前参数/输出目录并交给 <see cref="WizardSubmitItem"/> 固化。
    /// </summary>
    IReadOnlyList<WizardSubmitItem> BuildSubmitItems(IReadOnlyList<string> stagedFiles);

    /// <summary>执行单个已固化快照项（生产内调 EngineBridge；不得回读 VM 可变状态）。</summary>
    Task<WizardItemResult> ExecuteItemAsync(WizardSubmitItem item, CancellationToken token);

    /// <summary>
    /// workflow 多步例外（裁决 Q2）：额外参数步标题。默认 0（→ 统一 3 步），
    /// workflow 适配器返回其多步标题；每步复用 <see cref="ValidateParameters"/> 校验。
    /// </summary>
    IReadOnlyList<string> AdditionalStepTitles() => [];
}

/// <summary>提交项去重键：逐项唯一（VM+路径/索引），绝不用 <c>this</c>（review M2 / ToolTaskQueue.cs:229-249）。</summary>
public readonly record struct WizardDedupKey(string ToolId, string Paths, int Index);

/// <summary>向导执行结果处置（D5 / ST-29）：PDF → 开新标签；否则 Toast + 「打开文件夹」。</summary>
public enum WizardResultAction
{
    /// <summary>全部输出为 PDF：逐个打开新标签。</summary>
    OpenTabs,

    /// <summary>含非 PDF 输出：Toast 提示 + 「打开文件夹」入口。</summary>
    ToastAndOpenFolder,
}

/// <summary>向导结果处置策略（纯逻辑，供宿主与测试共用）。</summary>
public static class WizardResultPolicy
{
    /// <summary>全部输出为 .pdf → 开新标签；否则（含非 PDF 或混合）→ Toast + 打开文件夹。</summary>
    public static WizardResultAction Decide(IReadOnlyList<string>? outputs, bool anyFailed = false)
    {
        if (anyFailed || outputs is null || outputs.Count == 0)
        {
            return WizardResultAction.ToastAndOpenFolder;
        }

        foreach (var output in outputs)
        {
            if (!string.Equals(System.IO.Path.GetExtension(output), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return WizardResultAction.ToastAndOpenFolder;
            }
        }

        return WizardResultAction.OpenTabs;
    }
}
