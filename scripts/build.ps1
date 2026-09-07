# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
# Windows 构建脚本：镜像 scripts/build.sh 的阶段语义。
#   build.sh: venv → pip 锁定依赖 → catalog JSON → Swift 构建 → 打包
#   build.ps1: 前置检查 → venv + 锁定依赖 + Playwright Chromium → catalog JSON →
#              OCR helper 发布 → WPF 发布 → dist 布局 → 校验
# 产物：dist\aipdf-windows\（框架依赖，不打包 Python/LibreOffice/.NET 运行时）。
# 注意：本文件包含中文，PowerShell 5.1 要求 UTF-8 带 BOM 编码保存。
#Requires -Version 5.1
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $ProjectDir

function Write-Stage([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Exit-Fail([string[]]$Lines) {
    foreach ($line in $Lines) { Write-Host $line -ForegroundColor Red }
    exit 1
}

function Assert-LastExit([string]$Name) {
    if ($LASTEXITCODE -ne 0) {
        Exit-Fail @("$Name 失败（退出码 $LASTEXITCODE），构建中止。")
    }
}

# ---------------------------------------------------------------------------
# ① 前置检查：Python >= 3.12、.NET SDK 8
# ---------------------------------------------------------------------------
Write-Stage "[1/6] 前置检查（Python >= 3.12、.NET SDK 8）"

function Find-Python {
    # 候选顺序：py 启动器（官方安装自带）→ python → python3。
    # WindowsApps 下的 python3.exe 是商店跳转垫片，直接排除。
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
            $exe = $candidate[0]
            $exeArgs = $candidate[1]
            $version = & $exe @exeArgs -c "import sys;print('%d.%d.%d'%sys.version_info[:3])" 2>$null
            if ($LASTEXITCODE -eq 0 -and $version -match '^(\d+)\.(\d+)') {
                if ([int]$Matches[1] -eq 3 -and [int]$Matches[2] -ge 12) {
                    return , @($exe, $exeArgs, $version)
                }
            }
        }
        catch { }
    }
    return $null
}

$foundPython = Find-Python
if (-not $foundPython) {
    Exit-Fail @(
        "需要 Python 3.12 或更新版本（推荐 3.12—3.14），未在系统中找到。",
        "官方下载：https://www.python.org/downloads/windows/",
        "安装时勾选「Add python.exe to PATH」，或安装 py 启动器，然后重新运行本脚本。",
        "详细说明：docs/构建与安装-Windows.md"
    )
}
$PythonExe = $foundPython[0]
$PythonArgs = $foundPython[1]
$PythonVersion = $foundPython[2]
Write-Host "Python：$PythonVersion（$PythonExe）"

function Find-DotNet {
    # dotnet 常不在 PATH（如 per-user 安装），依次探测 PATH / DOTNET_ROOT / 常见位置。
    $paths = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $paths += $cmd.Source }
    if ($env:DOTNET_ROOT) { $paths += (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    $paths += (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    $paths += "C:\Program Files\dotnet\dotnet.exe"
    foreach ($path in $paths) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            $sdks = & $path --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { "$_" -match '^8\.' })) {
                return $path
            }
        }
    }
    return $null
}

$DotNetExe = Find-DotNet
if (-not $DotNetExe) {
    Exit-Fail @(
        "需要 .NET SDK 8，未在系统中找到。",
        "官方下载：https://dotnet.microsoft.com/download/dotnet/8.0（选择「SDK」一列的 Windows x64 安装程序）。",
        "安装后重新运行本脚本。详细说明：docs/构建与安装-Windows.md"
    )
}
Write-Host "dotnet SDK：$(& $DotNetExe --version)（$DotNetExe）"

# ---------------------------------------------------------------------------
# ② venv：.venv 创建 + 锁定依赖 + Playwright Chromium
# ---------------------------------------------------------------------------
Write-Stage "[2/6] 虚拟环境与锁定依赖"

$VenvPython = Join-Path $ProjectDir ".venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $VenvPython)) {
    & $PythonExe @PythonArgs -m venv .venv
    Assert-LastExit "python -m venv"
}
if (-not (Test-Path -LiteralPath $VenvPython)) {
    Exit-Fail @("虚拟环境创建后未找到 .venv\Scripts\python.exe，构建中止。")
}

# requirements.lock.txt 钉死版本；requirements.txt 补充 win32 平台标记依赖（Playwright）。
& $VenvPython -m pip install -r requirements.lock.txt -r requirements.txt
Assert-LastExit "pip install"

# 网址转 PDF 依赖捆绑 Chromium（幂等；已安装时快速跳过）。
& $VenvPython -m playwright install chromium
Assert-LastExit "playwright install chromium"

# ---------------------------------------------------------------------------
# ③ 工具目录 JSON（镜像 build.sh 的 backend/catalog.py 步骤）
# ---------------------------------------------------------------------------
Write-Stage "[3/6] 生成工具目录 JSON（backend/catalog.py）"

# Windows 上 catalog.py 默认按系统区域（GBK）写文件，会把仓库的 UTF-8 快照改脏；
# PYTHONUTF8=1 强制 UTF-8 输出（已验证与仓库快照字节等价）。
$env:PYTHONUTF8 = "1"
& $VenvPython "backend\catalog.py"
Assert-LastExit "backend\catalog.py"

# ---------------------------------------------------------------------------
# ④ OCR helper 构建（自包含单文件，零运行时依赖）
# ---------------------------------------------------------------------------
Write-Stage "[4/6] 构建 OCR helper（Sources\WindowsOcrHelper）"

# 显式指向 csproj：目录参数会命中同目录 .sln，连带构建测试项目。
& $DotNetExe publish "Sources\WindowsOcrHelper\WindowsOcrHelper.csproj" -c $Configuration
Assert-LastExit "dotnet publish WindowsOcrHelper"

$HelperExe = Get-ChildItem "Sources\WindowsOcrHelper\bin" -Recurse -Filter "WindowsOcrHelper.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\$Configuration\*" -and $_.FullName -like "*\publish\*" } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $HelperExe) {
    Exit-Fail @("未在 Sources\WindowsOcrHelper\bin 下找到 $Configuration 发布产物 WindowsOcrHelper.exe。")
}
$HelperPublish = $HelperExe.DirectoryName
Write-Host "helper 产物：$HelperPublish"

# ---------------------------------------------------------------------------
# ⑤ WPF 发布 + dist 布局
# ---------------------------------------------------------------------------
Write-Stage "[5/6] 发布 WPF 应用并组装 dist\aipdf-windows"

& $DotNetExe publish "Sources\AIPDF.Windows\AIPDF.Windows.csproj" -c $Configuration -r win-x64 --self-contained false
Assert-LastExit "dotnet publish AIPDF.Windows"

$AppExe = Get-ChildItem "Sources\AIPDF.Windows\bin" -Recurse -Filter "AIPDF.Windows.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\$Configuration\*" -and $_.FullName -like "*\publish\*" } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $AppExe) {
    Exit-Fail @("未在 Sources\AIPDF.Windows\bin 下找到 $Configuration 发布产物 AIPDF.Windows.exe。")
}
$AppPublish = $AppExe.DirectoryName

$Dist = Join-Path $ProjectDir "dist\aipdf-windows"
if (Test-Path -LiteralPath $Dist) {
    Remove-Item -LiteralPath $Dist -Recurse -Force
}
foreach ($sub in @("app", "engine\backend", "helper", "scripts")) {
    New-Item -ItemType Directory -Path (Join-Path $Dist $sub) -Force | Out-Null
}

Copy-Item -Path (Join-Path $AppPublish "*") -Destination (Join-Path $Dist "app") -Recurse -Force
# 引擎只带 backend 的三个文件（tests 不进 dist）；catalog JSON 由步骤 ③ 保证最新。
Copy-Item -Path "backend\engine.py", "backend\catalog.py", "backend\catalog.json" -Destination (Join-Path $Dist "engine\backend") -Force
Copy-Item -Path (Join-Path $HelperPublish "*") -Destination (Join-Path $Dist "helper") -Force
if (Test-Path -LiteralPath (Join-Path $ProjectDir "Install.ps1")) {
    Copy-Item -Path "Install.ps1" -Destination (Join-Path $Dist "scripts") -Force
}
else {
    Write-Host "提示：仓库根暂无 Install.ps1，dist\scripts 留空（由后续安装器任务补充）。" -ForegroundColor Yellow
}

$readme = @(
    "AIPDF · Windows 本地构建产物",
    "",
    "这是一个框架依赖的构建目录，各子目录：",
    "  app\      WPF 界面程序（运行需要 .NET 8 Desktop Runtime）",
    "  engine\   Python 处理引擎（运行需要系统 Python 3.12+ 及依赖）",
    "  helper\   基于 Windows.Media.Ocr 的 OCR 辅助程序（自包含）",
    "  scripts\  安装脚本（Install.ps1，如已提供）",
    "",
    "构建、安装步骤与前置条件见仓库文档：docs/构建与安装-Windows.md",
    "本目录不打包 Python 运行时与 LibreOffice。",
    "网址转 PDF 功能会联网获取输入网址的页面、脚本和资源；本地文件操作不上传文档。",
    ""
) -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $Dist "README-Windows.txt"), $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------------------------------------------------------------------------
# ⑥ 校验：布局断言 + dist 引擎冒烟 + 快速测试子集
# ---------------------------------------------------------------------------
Write-Stage "[6/6] 校验 dist 布局与引擎冒烟"

$required = @(
    "app\AIPDF.Windows.exe",
    "app\AIPDF.Windows.dll",
    "app\catalog.json",
    "engine\backend\engine.py",
    "engine\backend\catalog.py",
    "engine\backend\catalog.json",
    "helper\WindowsOcrHelper.exe",
    "README-Windows.txt"
)
foreach ($rel in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $Dist $rel))) {
        Exit-Fail @("dist 布局断言失败：缺少 $rel")
    }
}
Write-Host "布局断言：$($required.Count) 项关键文件全部在位。"

# 冒烟 1：以 JSON 子进程协议直接驱动 dist 里的引擎（health），验证拷贝产物可用。
$healthRequest = '{"operation":"health"}'
$healthOutput = $healthRequest | & $VenvPython (Join-Path $Dist "engine\backend\engine.py")
Assert-LastExit "dist 引擎冒烟（health）"
try {
    $healthResult = $healthOutput | ConvertFrom-Json
}
catch {
    Exit-Fail @("dist 引擎冒烟：stdout 不是合法 JSON（协议被污染）。")
}
if (-not $healthResult.ok) {
    Exit-Fail @("dist 引擎冒烟：health 返回 ok=false。")
}
Write-Host ("dist 引擎冒烟：health ok=true（engine={0}）" -f $healthResult.engine)

# 冒烟 2：快速纯引擎测试子集（不依赖 LibreOffice/OCR/网络，约 4 秒）。
if (-not $SkipTests) {
    & $VenvPython -m unittest discover -s tests -p "test_engine.py" `
        -k test_merge_order -k test_split_ranges_and_pages -k test_page_validation `
        -k test_rotation_crop -k test_bounds_and_invalid_inputs -k test_worker_json_protocol -v
    Assert-LastExit "test_engine.py 快离子集"
}
else {
    Write-Host "已按 -SkipTests 跳过快速测试子集。"
}

Write-Host ""
Write-Host ("应用已生成：{0}" -f $Dist) -ForegroundColor Green
