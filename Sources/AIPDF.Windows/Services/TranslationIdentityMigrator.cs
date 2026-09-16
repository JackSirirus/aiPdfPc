// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · 供应商身份迁移（T8）：v1.1 按「显示名」寻址的凭据 / 同意一次性迁到稳定身份键。
//   - 凭据：Enumerate(AIPDF-Translate-*) → 按设置里的连接配置（Provider + Endpoint + T2 回填的 PresetId）
//     计算身份键 → 复制到新 target + 删除旧 target（见 TranslationCredentialStore.MigrateToCanonicalTargets）；
//   - 同意：AppSettings.TranslationConsents 的 Provider 字段按同源映射改写为稳定身份键；
//   - 幂等：Load 归一化已收敛的部分跳过；重复调用不产生新条目、不改写已规范的记录；
//   - 红线：本类不读、不写、不记录密钥本体；只操作目标名与同意记录（其只含指纹）。
using System.Collections.Generic;

namespace AIPDF.Windows.Services;

/// <summary>一次身份迁移的动作计数（不含任何密钥 / 指纹信息）：凭据重键条数 + 同意改写条数。</summary>
public readonly record struct TranslationIdentityMigrationResult(int Credentials, int Consents)
{
    /// <summary>是否发生了实际迁移动作（false 表示已全部规范化，可安全跳过）。</summary>
    public bool Changed => Credentials > 0 || Consents > 0;
}

/// <summary>
/// v1.1 → v1.2 翻译身份迁移器：把按显示名寻址的凭据与同意记录迁到
/// <see cref="TranslationProviderIdentity"/> 的稳定身份键（T8）。
/// 生产在应用启动时调用一次；测试注入内存后端验证密钥不丢失与幂等。
/// </summary>
public sealed class TranslationIdentityMigrator
{
    private readonly SettingsStore _settings;
    private readonly TranslationCredentialStore _credentials;

    /// <summary>注入设置存储与凭据存储（与运行时共用同一寻址口径）。</summary>
    public TranslationIdentityMigrator(SettingsStore settings, TranslationCredentialStore credentials)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    /// <summary>
    /// 执行迁移：以设置里的连接配置为权威身份映射（Provider + Endpoint + T2 回填的 PresetId），
    /// 迁移凭据 target 与同意记录。返回计数；重复调用幂等（第二次通常 0/0）。
    /// </summary>
    public TranslationIdentityMigrationResult Migrate()
    {
        // Load 已执行归一化：T2 回填 PresetId、同意 provider 收敛为规范名 → 本方法据此建映射。
        var settings = _settings.Load();

        var credentialMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var consentMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in settings.TranslationProviders)
        {
            var name = provider.Provider?.Trim() ?? "";
            if (name.Length == 0) continue;

            var identity = TranslationProviderIdentity.For(provider);
            if (identity.Length == 0) continue;

            // 旧凭据 target 使用未收敛的显示名（Trim 后逐字保留大小写）。
            credentialMap[name] = identity;

            // 同意记录（Load 后 provider 为规范名）按「规范名 + origin」定位到稳定身份键。
            var destination = TranslationProviderIdentity.Canonical(name)
                + "\n" + TranslationConsent.NormalizeEndpoint(provider.Endpoint);
            consentMap[destination] = identity;
        }

        var migratedConsents = 0;
        foreach (var consent in settings.TranslationConsents)
        {
            if (!consentMap.TryGetValue(consent.Provider + "\n" + consent.Endpoint, out var identity)) continue;
            if (string.Equals(consent.Provider, identity, StringComparison.Ordinal)) continue;

            consent.Provider = identity;
            migratedConsents++;
        }

        if (migratedConsents > 0)
        {
            _settings.Save(settings); // Save 会再归一化并去重（同目的地保留最近一条）
        }

        // 设置里没有对应配置的旧凭据（供应商已移除）：按预设表名称解析兜底，仍迁到稳定 id。
        var migratedCredentials = _credentials.MigrateToCanonicalTargets(
            name => credentialMap.TryGetValue(name, out var identity)
                ? identity
                : TranslationProviderIdentity.Canonical(name));

        return new TranslationIdentityMigrationResult(migratedCredentials, migratedConsents);
    }
}
