// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · 供应商身份键（T8）：把「显示名 / 稳定 id / 自定义名」收敛为唯一的定位键。
//   - 凭据寻址（TranslationCredentialStore.TargetName）、同意匹配（TranslationConsentService）与
//     记录迁移（TranslationIdentityMigrator / SettingsStore.NormalizeConsents）必须共用本类，
//     保证「写入用哪个键，就读哪个键」——消除 v1.1 「凭据大小写敏感 / 同意大小写不敏感」的不对称；
//   - 内置预设（含本地服务）：显示名或 id 均解析为表内规范稳定 id（大小写不敏感）；
//   - 自定义供应商：无预设行 → 收敛为去首尾空白的原名（统一小写，与同意的历史大小写不敏感口径一致）；
//   - 本类不接触密钥，只做纯字符串身份归一。
namespace AIPDF.Windows.Services;

/// <summary>
/// 供应商身份键解析：<see cref="Canonical"/>（名称 → 稳定 id / 规范名）与
/// <see cref="For(TranslationProviderConfig)"/>（配置 → 稳定 id 优先）。
/// </summary>
public static class TranslationProviderIdentity
{
    /// <summary>
    /// 把供应商显示名 / 稳定 id / 自定义名收敛为定位键：
    /// 命中内置预设（id 或显示名，大小写不敏感，含本地服务）→ 返回规范稳定 id；
    /// 未命中（自定义）→ 去首尾空白后统一小写。空输入返回空串。
    /// </summary>
    public static string Canonical(string? provider)
    {
        var value = provider?.Trim() ?? "";
        if (value.Length == 0) return "";

        return TranslationProviderPreset.FindIdByName(value) ?? value.ToLowerInvariant();
    }

    /// <summary>
    /// 一条连接配置的定位键：显式稳定 id 优先（经预设表收敛/规范化），否则回退其显示名。
    /// 迁移后 <see cref="TranslationProviderConfig.PresetId"/> 已由 T2 回填，故旧数据也能拿到稳定 id。
    /// </summary>
    public static string For(TranslationProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var presetId = config.PresetId?.Trim() ?? "";
        return presetId.Length > 0 ? Canonical(presetId) : Canonical(config.Provider);
    }
}
