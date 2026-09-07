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
#                                                                        卸载（删除安装目录与快捷方式）
# 说明：仅安装到当前用户目录，不写 HKLM，不需要管理员；不下载 Python/LibreOffice，
#       只检查并给出指引。本文件包含中文，PowerShell 5.1 要求 UTF-8 带 BOM 编码保存。
#Requires -Version 5.1
param(
    [string]$InstallDir = "",
    [string]$ShortcutDir = "",
    [switch]$Check,
    [switch]$Uninstall,
    [switch]$NoShortcut
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
    if ($removed.Count -gt 0) {
        Write-Host ("已删除：`n  " + ($removed -join "`n  "))
    }
    Write-Host "卸载完成。输出文档与 %APPDATA%\AIPDF 下的预设未受影响。"
    exit 0
}

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

$Source = Resolve-SourceLayout
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
    Write-Host "布局断言：8 项关键文件全部在位。"

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

    # 安装信息只含时间与布局标记，不含任何绝对路径，保证安装目录可整体搬移。
    [System.IO.File]::WriteAllText((Join-Path $Target "install-info.json"),
        (@{ layout = "aipdf-windows"; installedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") } | ConvertTo-Json),
        (New-Object System.Text.UTF8Encoding($false)))

    if ($ShortcutPath) {
        Write-Step "创建快捷方式"
        New-StartShortcut $ShortcutPath $Target
        Write-Host "快捷方式：$ShortcutPath"
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }

    Write-Host ""
    Write-Host ("安装完成：{0}" -f $Target) -ForegroundColor Green
    Write-Host "可删除下载的构建目录；运行时仍需系统 Python 3.12+ 与 .NET 8 Desktop Runtime。"
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
