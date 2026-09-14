// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T15 权限位判定单测（纯函数，不加载原生 PDFium）：非加密全位不受限；缺任一位即受限。
using AIPDF.Windows.Rendering;
using Xunit;

namespace AIPDF.Windows.Tests.Rendering;

public sealed class PdfiumPermissionTests
{
    [Fact]
    public void UnrestrictedMask_IsNotRestricted()
    {
        // 非加密文档 FPDF_GetDocPermissions 返回 0xFFFFFFFC（含全部相关位）。
        Assert.False(PdfiumRenderService.IsPermissionMaskRestricted(0xFFFFFFFC));
    }

    [Fact]
    public void MissingCopyBit_IsRestricted()
    {
        // 禁止复制（bit 4 = 0x10）即受限。
        Assert.True(PdfiumRenderService.IsPermissionMaskRestricted(0xFFFFFFFC & ~0x10u));
    }

    [Fact]
    public void EmptyMask_IsRestricted()
    {
        Assert.True(PdfiumRenderService.IsPermissionMaskRestricted(0));
    }

    [Fact]
    public void AllRelevantBits_IsNotRestricted()
    {
        Assert.False(PdfiumRenderService.IsPermissionMaskRestricted(PdfiumRenderService.PermissionRelevantBits));
    }
}
