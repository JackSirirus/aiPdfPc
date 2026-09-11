// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 T10 批注 × 工具协作（FR-21/22）：工具启动前的空/纯决策逻辑，宿主 UI 与队列共用。
//
// 设计约束（计划 Must-NOT）：
//  - flush 本身不入 T7 队列（AnnotationSaveService 独立快速通道）；本文件只做决策，不发起 flush；
//  - 不静默跳过 flush 失败：失败必须交给调用方弹出「强制继续（批注可能丢失）/取消」；
//  - redact 警告文案必须显式包含「表单/目录也会被清除」（M-12）。

namespace AIPDF.Windows.Services;

/// <summary>工具启动门禁决策（FR-21）。</summary>
public enum ToolStartGateDecision
{
    /// <summary>无未保存批注，或 flush 成功，或失败后用户选择强制继续 → 放行启动。</summary>
    Start,

    /// <summary>存在未保存批注且 flush 失败、用户未选择强制继续 → 阻止启动。</summary>
    Blocked,
}

/// <summary>
/// FR-21 工具执行前强制 flush 的纯决策函数（可单测）：
/// 无未保存批注直接放行；有则必须 flush 成功；失败后仅当用户明确「强制继续」才放行。
/// </summary>
public static class ToolStartFlushLogic
{
    /// <param name="hasUnsavedAnnotations">当前文档是否有未保存批注（含删除待提交）。</param>
    /// <param name="flushSucceeded">强制 flush（跳过防抖、最多 30s）是否成功。</param>
    /// <param name="forceContinue">flush 失败后用户是否选择「强制继续（批注可能丢失）」。</param>
    public static ToolStartGateDecision Decide(
        bool hasUnsavedAnnotations, bool flushSucceeded, bool forceContinue)
        => !hasUnsavedAnnotations || flushSucceeded || forceContinue
            ? ToolStartGateDecision.Start
            : ToolStartGateDecision.Blocked;
}

/// <summary>
/// FR-22 redact UI 警告触发条件（纯函数，AC-22.1/22.2）：
/// 文档存在批注（无论是否已保存）即需警告；引擎 redact 会删除全部批注（Q2 裁决）。
/// </summary>
public static class RedactWarningLogic
{
    /// <param name="fileAnnotationCount">已内嵌到 PDF 文件内的批注数（不含 Widget 表单字段）。</param>
    /// <param name="unsavedAnnotationCount">当前会话叠加层中尚未保存的批注数。</param>
    public static bool ShouldWarn(int fileAnnotationCount, int unsavedAnnotationCount)
        => fileAnnotationCount > 0 || unsavedAnnotationCount > 0;
}
