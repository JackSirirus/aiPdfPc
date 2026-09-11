// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V2 跨任务契约（BLK-1）：就地保存改变 size/mtime 后，按旧键 → 新键迁移每文件阅读状态。
//
// T4 只调用本接口，不直接操作状态文件内容（schema 由 T5 ReadingStateStore 拥有）。
// 迁移须原子：先写新键成功，再删旧键；崩溃时旧键仍可回退。T5 未落地前用 no-op 实现。
namespace AIPDF.Windows.Services;

/// <summary>阅读状态键迁移契约。实现由 T5 ReadingStateStore 提供。</summary>
public interface IReadingStateMigrator
{
    /// <summary>
    /// 把 <paramref name="oldKey"/> 对应的阅读状态（页码/缩放/面板/书签）迁移到
    /// <paramref name="newKey"/>，并删除旧键。实现须原子（写新成功再删旧）。
    /// </summary>
    Task MigrateAsync(string oldKey, string newKey);
}

/// <summary>无状态迁移（T5 未接线时的默认实现）：仅记录调用，不落盘。</summary>
public sealed class NullReadingStateMigrator : IReadingStateMigrator
{
    /// <summary>最后一次收到的 (旧键, 新键)，供诊断/测试断言。</summary>
    public (string OldKey, string NewKey)? LastMigration { get; private set; }

    public Task MigrateAsync(string oldKey, string newKey)
    {
        LastMigration = (oldKey, newKey);
        return Task.CompletedTask;
    }
}
