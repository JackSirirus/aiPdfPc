# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
# AIPDF · Windows 用户级安装脚本（镜像 scripts/install.py 的安装语义）。
# 流程：前置检查 → 校验目标 → 暂存复制 → 布局断言 → 备份旧版 → 原子换入 →
#       写安装信息 → 开始菜单快捷方式；任一步失败自动回滚并以非 0 退出码结束。
# 用法（从仓库根或构建产物 dist\aipdf-windows 内运行）：
#   powershell -ExecutionPolicy Bypass -File Install.ps1                 安装到 %LOCALAPPDATA%\AIPDF
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -Check          只做环境检查
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -InstallDir D:\dir [-ShortcutDir D:\menu]
#                                                                        指定安装目录（供测试沙箱）
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -Uninstall [-InstallDir D:\dir]
#                                                                        卸载（删除安装目录与快捷方式；
#                                                                        列出翻译凭据数量，确认后清理）
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -Uninstall -SkipCredentialCleanup
#                                                                        卸载但保留翻译凭据
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -NoAssociation   不登记 .pdf 打开方式
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -AssociationProgId X -RegistryRoot R
#                                                                        关联落点覆盖（测试用临时 ProgId/隔离根）
#   powershell -ExecutionPolicy Bypass -File Install.ps1 -CredentialPrefix P
#                                                                        翻译凭据前缀覆盖（测试用隔离前缀）
# 说明：仅安装到当前用户目录，不写 HKLM，不需要管理员；不下载 Python/LibreOffice，
#       只检查并给出指引。文件关联仅写 HKCU\Software\Classes：登记 ProgId 的
#       shell\open\command 与 .pdf 的「打开方式」候选，不写 UserChoice、不覆盖用户默认程序。
#       本文件包含中文，PowerShell 5.1 要求 UTF-8 带 BOM 编码保存。
# 模式：来源含 engine\python\python.exe 时判定为自包含安装包（Python 与 .NET
#       Desktop Runtime 随包内置），跳过两项系统前置检查；否则保持框架依赖模式，
#       强制检查系统 Python 3.12+ 与 .NET 8 Desktop Runtime。
#Requires -Version 5.1
param(
    [string]$InstallDir = "",
    [string]$ShortcutDir = "",
    [switch]$Check,
    [switch]$Uninstall,
    [switch]$NoShortcut,
    # 文件关联（FR-31，仅 HKCU\Software\Classes，免管理员）。
    # 默认 ProgId = AIPDF.Pdf；测试传入临时 ProgId（AIPDF.assoc.test.<guid>）并测后清理。
    [string]$AssociationProgId = "AIPDF.Pdf",
    [string]$RegistryRoot = "HKCU:\Software\Classes",
    [switch]$NoAssociation,
    # v1.1 翻译凭据（设计 §凭据命名与卸载清理）：
    # -Uninstall 默认枚举 AIPDF-Translate-* 通用凭据，列出数量并经用户确认后逐条删除；
    # -SkipCredentialCleanup 跳过清理（凭据保留；自动化/测试与「只想删程序」的场景）；
    # -CredentialPrefix 覆盖前缀（测试用隔离前缀；默认与 TranslationCredentialStore.TargetPrefix 一致）。
    [switch]$SkipCredentialCleanup,
    [string]$CredentialPrefix = "AIPDF-Translate-"
)

$ErrorActionPreference = "Stop"

# 统一输出编码为 UTF-8，保证被重定向捕获时中文不乱码（宿主不支持时静默跳过）。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Exit-Fail([string[]]$Lines) {
    foreach ($line in $Lines) { [Console]::Error.WriteLine($line) }
    exit 1
}

# ---------------------------------------------------------------------------
# 依赖探测（探测顺序与 scripts\build.ps1、backend\engine.py 保持一致）
# ---------------------------------------------------------------------------

function Find-Python {
    # 处理引擎运行时依赖。候选顺序：py 启动器 → python → python3（排除商店垫片）。
    $candidates = @()
    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) { $candidates += , @($pyLauncher.Source, @("-3")) }
    foreach ($name in @("python", "python3")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source -notmatch "WindowsApps") {
            $candidates += , @($cmd.Source, @())
        }
    }
    foreach ($candidate in $candidates) {
        try {
            $extraArgs = $candidate[1]
            $version = & $candidate[0] @extraArgs -c "import sys;print('%d.%d.%d'%sys.version_info[:3])" 2>$null
            if ($LASTEXITCODE -eq 0 -and "$version" -match '^(\d+)\.(\d+)') {
                if ([int]$Matches[1] -eq 3 -and [int]$Matches[2] -ge 12) {
                    return , @($candidate[0], $version)
                }
            }
        }
        catch { }
    }
    return $null
}

function Find-DesktopRuntime {
    # 框架依赖的 WPF 程序需要 Microsoft.WindowsDesktop.App 8.x；
    # dotnet 常不在 PATH（按用户安装），探测顺序与 build.ps1 的 Find-DotNet 一致。
    $paths = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $paths += $cmd.Source }
    if ($env:DOTNET_ROOT) { $paths += (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    $paths += (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    $paths += "C:\Program Files\dotnet\dotnet.exe"
    foreach ($path in $paths) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            $runtimes = & $path --list-runtimes 2>$null
            if ($LASTEXITCODE -eq 0 -and ($runtimes | Where-Object { "$_" -match '^Microsoft\.WindowsDesktop\.App 8\.' })) {
                return $path
            }
        }
    }
    return $null
}

function Test-LibreOffice {
    # 可选依赖：镜像 backend\engine.py find_office() 的 win32 探测优先级。
    if ($env:AIPDF_OFFICE -and (Test-Path -LiteralPath $env:AIPDF_OFFICE)) { return $true }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base -and (Test-Path -LiteralPath (Join-Path $base "LibreOffice\program\soffice.exe"))) { return $true }
    }
    # 注册表发现：覆盖自定义安装目录（与 backend\engine.py find_office 的注册表探测保持一致）。
    foreach ($root in @("HKLM:\SOFTWARE\LibreOffice\UNO\InstallPath",
                        "HKLM:\SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath",
                        "HKCU:\Software\LibreOffice\UNO\InstallPath")) {
        $dir = (Get-ItemProperty -Path $root -ErrorAction SilentlyContinue).'(default)'
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir "soffice.exe"))) { return $true }
    }
    if (Get-Command soffice -ErrorAction SilentlyContinue) { return $true }
    return $false
}

# ---------------------------------------------------------------------------
# 安装布局
# ---------------------------------------------------------------------------

function Resolve-SourceLayout {
    # 安装源 = dist 布局（app\、engine\、helper\、README-Windows.txt）。
    # 三种放置形态：dist 根内、dist\scripts\（发布形态）、仓库根（开发形态）。
    $candidates = @(
        $PSScriptRoot,
        (Join-Path $PSScriptRoot ".."),
        (Join-Path $PSScriptRoot "dist\aipdf-windows")
    )
    foreach ($candidate in $candidates) {
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath (Join-Path $full "app\AIPDF.Windows.exe")) {
            return $full
        }
    }
    return $null
}

function Assert-TargetSafe([string]$Path) {
    # 镜像 install.py validate_existing：不接管链接，不覆盖无法识别的内容。
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.LinkType) {
        Exit-Fail @("目标位置是一个链接（$($item.LinkType)），请手动选择其他安装位置。")
    }
    if (-not $item.PSIsContainer) {
        Exit-Fail @("目标位置已存在同名文件，不会覆盖，请用 -InstallDir 选择其他位置。")
    }
    $isAipdf = (Test-Path -LiteralPath (Join-Path $Path "app\AIPDF.Windows.exe")) -or
               (Test-Path -LiteralPath (Join-Path $Path "install-info.json"))
    if (-not $isAipdf) {
        Exit-Fail @(
            "目标位置已存在非 AIPDF 目录，为避免覆盖未知数据，安装已停止。",
            "请确认内容后手动清理，或用 -InstallDir 指定其他位置。"
        )
    }
}

function New-BackupLeaf([string]$Parent) {
    $stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
    $leaf = "AIPDF.bak-$stamp"
    $n = 2
    while (Test-Path -LiteralPath (Join-Path $Parent $leaf)) {
        $leaf = "AIPDF.bak-$stamp-$n"
        $n++
    }
    return $leaf
}

function New-StartShortcut([string]$LinkPath, [string]$TargetPath) {
    $linkDir = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $linkDir)) {
        New-Item -ItemType Directory -Path $linkDir -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $exe = Join-Path $TargetPath "app\AIPDF.Windows.exe"
        $lnk = $shell.CreateShortcut($LinkPath)
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = Join-Path $TargetPath "app"
        $lnk.IconLocation = "$exe,0"
        $lnk.Description = "AIPDF - 本地 PDF 工作台"
        $lnk.Save()
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

# ---------------------------------------------------------------------------
# 文件关联（FR-31 / PRD 4.7.2）：仅写 HKCU\Software\Classes，免管理员。
#   ProgId\<default>              = 友好名
#   ProgId\shell\open\command     = "<安装目录>\app\AIPDF.Windows.exe" "%1"
#   .pdf\OpenWithProgids\<ProgId> = 非破坏式候选（绝不写 .pdf 默认值 / UserChoice）
# 不强制覆盖用户既有默认程序（UserChoice）；安装完仅提示可在系统设置中更改。
# ---------------------------------------------------------------------------

function Get-AssociationCommand([string]$ExePath) {
    # 与单实例转发/命令行解析一致的命令串；"%1" 保留引号以兼容含空格路径。
    return '"' + $ExePath + '" "%1"'
}

function Register-PdfAssociation([string]$ProgId, [string]$Root, [string]$ExePath) {
    $progKey = Join-Path $Root $ProgId
    New-Item -Path $progKey -Force | Out-Null
    Set-Item -Path $progKey -Value "AIPDF PDF 文档"

    $cmdKey = Join-Path $progKey "shell\open\command"
    New-Item -Path $cmdKey -Force | Out-Null
    Set-Item -Path $cmdKey -Value (Get-AssociationCommand $ExePath)

    # 非破坏式登记：只加入 .pdf 的「打开方式」候选，不写 .pdf 默认值、不碰 UserChoice。
    $openWithKey = Join-Path $Root ".pdf\OpenWithProgids"
    New-Item -Path $openWithKey -Force | Out-Null
    New-ItemProperty -Path $openWithKey -Name $ProgId -Value ([byte[]]@()) -PropertyType Binary -Force | Out-Null
}

function Unregister-PdfAssociation([string]$ProgId, [string]$Root) {
    # 先移除「打开方式」候选（避免残留；不删 .pdf 键本身，可能含其他程序登记）。
    $openWithKey = Join-Path $Root ".pdf\OpenWithProgids"
    if (Test-Path -LiteralPath $openWithKey) {
        Remove-ItemProperty -Path $openWithKey -Name $ProgId -ErrorAction SilentlyContinue
    }
    # 再删 ProgId 树（shell\open\command、友好名等一并清除）。
    $progKey = Join-Path $Root $ProgId
    if (Test-Path -LiteralPath $progKey) {
        Remove-Item -Path $progKey -Recurse -Force
    }
}

# ---------------------------------------------------------------------------
# 翻译凭据清理（v1.1 决策「凭据命名与卸载清理」）
#   应用把云端翻译 API Key 写入 Windows 凭据管理器的通用凭据，命名空间为
#   AIPDF-Translate-{provider}-{sha256(endpoint origin) 前 8 位 hex}
#   （见 Sources/AIPDF.Windows/Services/TranslationCredentialStore.cs）。
#   卸载时只清理该前缀：先枚举并列出数量与名称，经用户确认（y/N，默认 N）后逐条删除；
#   全程只接触凭据「目标名」，不读取、不输出密钥内容，也绝不触碰其他凭据。
#   正常升级（同名覆盖安装）不经过本函数，凭据保留（决策：重装/升级不丢失）。
#   枚举与删除复用应用同一组 Win32 API（CredEnumerateW / CredDeleteW），
#   保证目标集合与 TranslationCredentialStore.DeleteAll() 完全一致。
# ---------------------------------------------------------------------------

function Initialize-CredentialApi {
    # 幂等：类型已加载则直接返回（同一进程内 Add-Type 只需编译一次）。
    if ("AipdfCredentialApi" -as [type]) { return }
    # 与 C# Win32CredentialBackend 相同的 P/Invoke 定义：CREDENTIAL 结构 + CredEnumerateW/
    # CredDeleteW/CredFree。CredentialBlob 在结构里但不读取，凭据枚举不会返回密钥内容。
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class AipdfCredentialApi
{
    private const uint CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);

    public static string[] Enumerate(string prefix)
    {
        uint count;
        IntPtr array;
        if (!CredEnumerate(prefix + "*", 0, out count, out array))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return new string[0];
            throw new System.ComponentModel.Win32Exception(error);
        }
        try
        {
            var targets = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                IntPtr entry = Marshal.ReadIntPtr(array, (int)i * IntPtr.Size);
                if (entry == IntPtr.Zero) continue;
                var credential = (CREDENTIAL)Marshal.PtrToStructure(entry, typeof(CREDENTIAL));
                if (!string.IsNullOrEmpty(credential.TargetName) &&
                    credential.TargetName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    targets.Add(credential.TargetName);
                }
            }
            return targets.ToArray();
        }
        finally
        {
            CredFree(array);
        }
    }

    public static bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new System.ComponentModel.Win32Exception(error);
    }
}
'@
}

function Get-TranslationCredentialTargets([string]$Prefix) {
    # 只读枚举：返回以 $Prefix 开头的凭据目标名，不返回也不读取密钥内容。
    Initialize-CredentialApi
    return [AipdfCredentialApi]::Enumerate($Prefix)
}

function Remove-TranslationCredential([string]$Target) {
    # 与 C# Win32CredentialBackend.Delete 一致：不存在返回 $false，凭据管理器不可用抛异常。
    return [AipdfCredentialApi]::Delete($Target)
}

function Clear-TranslationCredentials([string]$Prefix, [switch]$Skip) {
    # 返回实际删除条数。失败/跳过时不中断卸载，但明确告知（不把跳过当成功）。
    if ($Skip) {
        Write-Host "已跳过翻译凭据清理（-SkipCredentialCleanup），凭据保留。"
        return 0
    }

    $targets = @()
    try {
        $targets = @(Get-TranslationCredentialTargets $Prefix)
    }
    catch {
        Write-Warning "无法枚举 Windows 凭据管理器：$($_.Exception.Message)"
        Write-Warning "已跳过翻译凭据清理；如需清理，可在应用「设置 → 翻译」中清除，或手动在「凭据管理器」删除 $Prefix* 条目。"
        return 0
    }

    if ($targets.Count -eq 0) {
        Write-Host "未发现 $Prefix* 翻译凭据，无需清理。"
        return 0
    }

    Write-Host "发现 $($targets.Count) 条翻译凭据（仅列出名称，不读取密钥内容）："
    foreach ($target in $targets) { Write-Host "  $target" }
    try {
        $answer = Read-Host "确认删除以上 $($targets.Count) 条凭据？(y/N)"
    }
    catch {
        # 无交互宿主（服务 / 管道）无法询问：按未确认处理，保留凭据。
        $answer = ""
    }
    if ("$answer".Trim() -notmatch '^(y|yes|是)$') {
        Write-Host "已保留翻译凭据（未确认删除）。"
        return 0
    }

    $deleted = 0
    foreach ($target in $targets) {
        try {
            if (Remove-TranslationCredential $target) { $deleted++ }
            else { Write-Warning "凭据已不存在，跳过：$target" }
        }
        catch {
            Write-Warning "凭据删除失败，跳过：$target（$($_.Exception.Message)）"
        }
    }
    Write-Host "翻译凭据：已删除 $deleted/$($targets.Count) 条。"
    return $deleted
}

# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "AIPDF · Windows 用户级安装" -ForegroundColor Cyan

if ($Check -and $Uninstall) {
    Exit-Fail @("-Check 与 -Uninstall 不能同时使用。")
}

if ($Uninstall) {
    if ($InstallDir) { $Target = [System.IO.Path]::GetFullPath($InstallDir) }
    else { $Target = Join-Path $env:LOCALAPPDATA "AIPDF" }
    Write-Step "卸载 AIPDF（$Target）"
    Assert-TargetSafe $Target
    $removed = @()
    if (Test-Path -LiteralPath $Target) {
        Remove-Item -LiteralPath $Target -Recurse -Force
        $removed += "安装目录 $Target"
    }
    else {
        Write-Host "未找到安装目录 $Target（可能已卸载）。"
    }
    $parent = Split-Path -Parent $Target
    if (Test-Path -LiteralPath $parent) {
        Get-ChildItem -LiteralPath $parent -Directory -Filter "AIPDF.bak-*" -ErrorAction SilentlyContinue |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
                $removed += "备份目录 $($_.Name)"
            }
    }
    if (-not $NoShortcut) {
        if ($ShortcutDir) { $linkPath = Join-Path ([System.IO.Path]::GetFullPath($ShortcutDir)) "AIPDF.lnk" }
        else {
            $programsDir = [Environment]::GetFolderPath("Programs")
            if (-not $programsDir) { Exit-Fail @("无法定位当前用户的开始菜单目录，无法清理快捷方式。") }
            $linkPath = Join-Path $programsDir "AIPDF.lnk"
        }
        if (Test-Path -LiteralPath $linkPath) {
            Remove-Item -LiteralPath $linkPath -Force
            $removed += "快捷方式 $linkPath"
        }
    }
    if (-not $NoAssociation) {
        $assocKey = Join-Path $RegistryRoot $AssociationProgId
        $hadAssociation = Test-Path -LiteralPath $assocKey
        Unregister-PdfAssociation $AssociationProgId $RegistryRoot
        if ($hadAssociation) { $removed += "文件关联 $AssociationProgId" }
    }
    # 翻译凭据：默认列出数量并确认后清理；跳过或拒绝时凭据保留。
    $cleanedCredentials = Clear-TranslationCredentials -Prefix $CredentialPrefix -Skip:$SkipCredentialCleanup
    if ($cleanedCredentials -gt 0) {
        $removed += "翻译凭据 $cleanedCredentials 条（$CredentialPrefix*）"
    }
    if ($removed.Count -gt 0) {
        Write-Host ("已删除：`n  " + ($removed -join "`n  "))
    }
    Write-Host "卸载完成。输出文档与 %APPDATA%\AIPDF 下的预设未受影响。"
    exit 0
}

# 安装来源先于前置检查解析：来源含 engine\python\python.exe 即自包含安装包，
# Python 与 .NET Desktop Runtime 随包内置，无需系统级前置；否则保持框架依赖模式。
$Source = Resolve-SourceLayout
$Mode = if ($Source -and (Test-Path -LiteralPath (Join-Path $Source "engine\python\python.exe"))) { "self-contained" } else { "framework-dependent" }

if ($Mode -eq "self-contained") {
    Write-Step "前置检查（自包含运行时、LibreOffice 可选）"
    Write-Host "安装模式：自包含（Python 与 .NET Desktop Runtime 随包内置）"
    if (-not (Test-Path -LiteralPath (Join-Path $Source "app\hostfxr.dll"))) {
        Exit-Fail @(
            "自包含安装包缺少 app\hostfxr.dll（.NET 运行时载体），构建产物可能不完整。",
            "请重新运行 scripts\build.ps1 生成完整产物后重试。详细说明：docs/构建与安装-Windows.md"
        )
    }
    Write-Host "Python：随安装包内置（engine\python\python.exe）"
    Write-Host ".NET Desktop Runtime：随安装包内置（app\hostfxr.dll）"
}
else {
    Write-Step "前置检查（Python 3.12+、.NET 8 Desktop Runtime、LibreOffice 可选）"

    $foundPython = Find-Python
    if (-not $foundPython) {
        Exit-Fail @(
            "需要 Python 3.12 或更新版本（处理引擎运行时依赖），未在系统中找到。",
            "官方下载：https://www.python.org/downloads/windows/",
            "安装时勾选「Add python.exe to PATH」，然后重新运行本脚本。",
            "详细说明：docs/构建与安装-Windows.md"
        )
    }
    Write-Host "Python：$($foundPython[1])（$($foundPython[0])）"

    $dotnetExe = Find-DesktopRuntime
    if (-not $dotnetExe) {
        Exit-Fail @(
            "需要 .NET 8 Desktop Runtime（界面程序运行时依赖），未在系统中找到。",
            "官方下载：https://dotnet.microsoft.com/download/dotnet/8.0（选择「.NET Desktop Runtime」的 Windows x64 安装程序）。",
            "安装后重新运行本脚本。详细说明：docs/构建与安装-Windows.md"
        )
    }
    Write-Host ".NET Desktop Runtime：8.x（$dotnetExe）"
}

if (Test-LibreOffice) {
    Write-Host "LibreOffice：已找到，Office/PDF-A 转换可用。"
}
else {
    Write-Host "提示：未找到 LibreOffice，Office/PDF-A 转换暂不可用，其余工具不受影响。" -ForegroundColor Yellow
    Write-Host "可选下载：https://www.libreoffice.org/download/download-libreoffice/" -ForegroundColor Yellow
}

if ($Check) {
    Write-Host ""
    Write-Host "环境检查通过，可以安装。"
    exit 0
}

if (-not $Source) {
    Exit-Fail @(
        "未找到 dist 安装布局（需要 app\、engine\、helper\ 与 README-Windows.txt）。",
        "请先运行 scripts\build.ps1 生成构建产物，或把本脚本放回 dist\aipdf-windows\scripts\ 后重试。"
    )
}
Write-Host "安装来源：$Source"

if ($InstallDir) { $Target = [System.IO.Path]::GetFullPath($InstallDir) }
else { $Target = Join-Path $env:LOCALAPPDATA "AIPDF" }
$ParentDir = Split-Path -Parent $Target
Assert-TargetSafe $Target

$ShortcutPath = $null
if (-not $NoShortcut) {
    if ($ShortcutDir) { $ShortcutPath = Join-Path ([System.IO.Path]::GetFullPath($ShortcutDir)) "AIPDF.lnk" }
    else {
        $programsDir = [Environment]::GetFolderPath("Programs")
        if (-not $programsDir) { Exit-Fail @("无法定位当前用户的开始菜单目录。") }
        $ShortcutPath = Join-Path $programsDir "AIPDF.lnk"
    }
}

# 安装互斥：同一时刻只允许一个安装任务（镜像 install.py 的 install.lock）。
$mutex = New-Object System.Threading.Mutex($false, "Local\AIPDF-Installer")
if (-not $mutex.WaitOne(0)) {
    Exit-Fail @("另一个 AIPDF 安装任务正在运行，请等待它完成。")
}

$stagingRoot = Join-Path $ParentDir (".aipdf-install-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$stagedApp = Join-Path $stagingRoot "AIPDF"
$backupPath = $null
$swapped = $false
$associationRegistered = $false

try {
    New-Item -ItemType Directory -Path $ParentDir -Force | Out-Null
    New-Item -ItemType Directory -Path $stagedApp -Force | Out-Null

    Write-Step "复制文件到暂存区"
    foreach ($name in @("app", "engine", "helper")) {
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination (Join-Path $stagedApp $name) -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $Source "README-Windows.txt") -Destination (Join-Path $stagedApp "README-Windows.txt") -Force

    Write-Step "校验安装布局"
    foreach ($rel in @(
        "app\AIPDF.Windows.exe", "app\AIPDF.Windows.dll", "app\catalog.json",
        "engine\backend\engine.py", "engine\backend\catalog.py", "engine\backend\catalog.json",
        "helper\WindowsOcrHelper.exe", "README-Windows.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagedApp $rel))) {
            throw "安装布局不完整：缺少 $rel"
        }
    }
    if ($Mode -eq "self-contained") {
        foreach ($rel in @("engine\python\python.exe", "app\hostfxr.dll")) {
            if (-not (Test-Path -LiteralPath (Join-Path $stagedApp $rel))) {
                throw "安装布局不完整：缺少 $rel"
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $stagedApp "engine\site-packages\fitz") -PathType Container)) {
            throw "安装布局不完整：缺少 engine\site-packages\fitz（目录）"
        }
        Write-Host "布局断言：8 项关键文件 + 自包含运行时 3 项全部在位。"
    }
    else {
        Write-Host "布局断言：8 项关键文件全部在位。"
    }

    if (Test-Path -LiteralPath $Target) {
        $backupLeaf = New-BackupLeaf $ParentDir
        $backupPath = Join-Path $ParentDir $backupLeaf
        Rename-Item -LiteralPath $Target -NewName $backupLeaf
        Write-Host "已备份旧安装：$backupPath"
    }
    if (Test-Path -LiteralPath $Target) {
        throw "目标位置在安装过程中被其他程序占用：$Target"
    }
    Move-Item -LiteralPath $stagedApp -Destination $Target
    $swapped = $true

    # 安装信息只含模式、时间与布局标记，不含任何绝对路径，保证安装目录可整体搬移。
    [System.IO.File]::WriteAllText((Join-Path $Target "install-info.json"),
        (@{ layout = "aipdf-windows"; mode = $Mode; installedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") } | ConvertTo-Json),
        (New-Object System.Text.UTF8Encoding($false)))

    if ($ShortcutPath) {
        Write-Step "创建快捷方式"
        New-StartShortcut $ShortcutPath $Target
        Write-Host "快捷方式：$ShortcutPath"
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }

    if (-not $NoAssociation) {
        Write-Step "登记 .pdf 打开方式（$AssociationProgId，仅 HKCU）"
        $associationExe = Join-Path $Target "app\AIPDF.Windows.exe"
        Register-PdfAssociation $AssociationProgId $RegistryRoot $associationExe
        $associationRegistered = $true
        Write-Host "已登记：$RegistryRoot\$AssociationProgId\shell\open\command → $associationExe"
        Write-Host "提示：仅加入「打开方式」候选，不覆盖默认程序；如需设为默认，可在系统设置中更改默认程序。" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host ("安装完成：{0}" -f $Target) -ForegroundColor Green
    if ($Mode -eq "self-contained") {
        Write-Host "可删除下载的构建目录；无需系统 Python 与 .NET Desktop Runtime；LibreOffice 可选；网址转 PDF 首次使用需联网下载 Chromium。"
    }
    else {
        Write-Host "可删除下载的构建目录；运行时仍需系统 Python 3.12+ 与 .NET 8 Desktop Runtime。"
    }
    exit 0
}
catch {
    $installError = $_.Exception.Message
    $rollbackNote = $null
    try {
        if ($swapped -and (Test-Path -LiteralPath $Target)) {
            Remove-Item -LiteralPath $Target -Recurse -Force
        }
        if ($backupPath -and (Test-Path -LiteralPath $backupPath)) {
            Rename-Item -LiteralPath $backupPath -NewName (Split-Path -Leaf $Target)
            $backupPath = $null
        }
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }
    catch {
        $rollbackNote = $_.Exception.Message
    }
    if ($associationRegistered) {
        # 安装失败回滚：同时清除本次写入的关联，避免注册表残留
        try { Unregister-PdfAssociation $AssociationProgId $RegistryRoot } catch { }
    }
    [Console]::Error.WriteLine("安装未完成：$installError")
    [Console]::Error.WriteLine("已回滚到安装前状态；已有输出文档不会被修改。")
    if ($rollbackNote) {
        [Console]::Error.WriteLine("注意：回滚过程中出现问题（$rollbackNote）；备份目录保留在：$backupPath")
    }
    exit 1
}
finally {
    try { $mutex.ReleaseMutex() } catch { }
    $mutex.Dispose()
}
