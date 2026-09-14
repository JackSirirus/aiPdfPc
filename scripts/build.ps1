# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
# Windows 构建脚本：镜像 scripts/build.sh 的阶段语义。
#   build.sh: venv → pip 锁定依赖 → catalog JSON → Swift 构建 → 打包
#   build.ps1: 前置检查 → venv + 锁定依赖 + Playwright Chromium → catalog JSON →
#              OCR helper 发布 → WPF（自包含）发布 → dist 布局 → 校验 →
#              嵌入式 Python 运行时与离线依赖
# 可选：-Sign 在构建完成后用 signtool 对 dist 顶层的 PE 做 Authenticode 签名
#       （需要证书参数，见 Invoke-DistSigning；正式分发应使用 CA 签发证书）。
# 产物：dist\aipdf-windows\（自包含：内置 .NET 运行时与嵌入式 Python；
#       不打包 LibreOffice 与 Chromium，后者由网址转 PDF 首次使用时联网下载）。
# 注意：本文件包含中文，PowerShell 5.1 要求 UTF-8 带 BOM 编码保存。
#Requires -Version 5.1
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    # 可选 Authenticode 签名：-Sign 时在构建完成后签名 dist 顶层 PE。
    [switch]$Sign,
    [string]$CertificateSubject = "",
    [string]$CertificateFile = "",
    [string]$CertificatePassword = ""
)

$ErrorActionPreference = "Stop"

# PS 5.1 在 stdout 被重定向（CI、日志捕获）时，向原生命令管道写入的输入
# 会带上 UTF-8 BOM，破坏引擎的 stdin JSON 协议（冒烟会收到「请求格式不正确」）。
# 显式指定无 BOM 的 UTF-8，保证 $json | & python 之外部调用方式也可靠。
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)

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
# 可选 Authenticode 签名（-Sign）：定位 signtool，签名 dist 顶层的 PE 文件。
# 只在构建完成、dist 产物全部就绪后调用（见脚本末尾）。
# ---------------------------------------------------------------------------
function Invoke-DistSigning {
    # ① 定位 signtool：取 Windows Kits\10\bin 下版本号最大的 x64\signtool.exe。
    #    signtool 不在 PATH 上，必须全路径调用。
    $kitsBinRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    $signtool = Get-ChildItem -LiteralPath $kitsBinRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+){3}$' } |
        ForEach-Object {
            $candidate = Join-Path $_.FullName "x64\signtool.exe"
            if (Test-Path -LiteralPath $candidate) {
                [PSCustomObject]@{ Version = [version]$_.Name; Path = $candidate }
            }
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if (-not $signtool) {
        Exit-Fail @(
            "-Sign 需要签名工具 signtool.exe，未在 $kitsBinRoot 下找到。",
            "请安装 Windows SDK（包含签名工具组件）后重新运行：",
            "https://developer.microsoft.com/windows/downloads/windows-sdk/"
        )
    }
    Write-Host "signtool：$($signtool.Path)"

    # ② 解析证书：-CertificateFile（PFX）优先，其次 -CertificateSubject（证书存储）。
    #    两者都缺省时直接报错退出，不触碰任何 dist 文件。
    if ($CertificateFile) {
        if (-not (Test-Path -LiteralPath $CertificateFile)) {
            Exit-Fail @("未找到证书文件：$CertificateFile")
        }
        if (-not $CertificatePassword) {
            Exit-Fail @("使用 -CertificateFile（PFX）时必须同时提供 -CertificatePassword。")
        }
        $certArgs = @("/f", $CertificateFile, "/p", $CertificatePassword)
    }
    elseif ($CertificateSubject) {
        $certArgs = @("/n", $CertificateSubject)
    }
    else {
        Exit-Fail @(
            "-Sign 需要指定签名证书，请补充以下参数之一：",
            "  -CertificateSubject <证书主题名>                        按名称在当前用户/本机证书存储中查找；",
            "  -CertificateFile <PFX 路径> -CertificatePassword <密码>  使用 PFX 证书文件。",
            "正式发布分发需要 CA 签发的代码签名证书；自签名证书仅适合本机测试。"
        )
    }

    # ③ 收集签名目标：app 顶层的 *.exe 与 *.dll（本地库）+ helper 主程序。
    #    仅顶层，不递归（不含 engine\site-packages 的 .pyd，也不进 helper 内部）。
    $targets = @()
    $appDir = Join-Path $Dist "app"
    foreach ($pattern in @("*.exe", "*.dll")) {
        $targets += @(Get-ChildItem -LiteralPath $appDir -Filter $pattern -File -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName)
    }
    $helperTarget = Join-Path $Dist "helper\WindowsOcrHelper.exe"
    if (Test-Path -LiteralPath $helperTarget) { $targets += $helperTarget }
    if (-not $targets) {
        Exit-Fail @("dist 中未找到可签名的可执行文件或本地库，签名中止。")
    }

    # ④ 逐个签名：SHA-256 摘要 + DigiCert 时间戳（发布期联网操作）。
    foreach ($target in $targets) {
        Write-Host "签名：$target"
        & $signtool.Path sign @certArgs /fd sha256 /tr http://timestamp.digicert.com /td sha256 $target
        if ($LASTEXITCODE -ne 0) {
            Exit-Fail @("signtool 签名失败（退出码 $LASTEXITCODE）：$target")
        }
    }

    # ⑤ 校验签名结果。
    foreach ($target in $targets) {
        $status = (Get-AuthenticodeSignature -FilePath $target).Status
        Write-Host ("  {0} → {1}" -f (Split-Path -Path $target -Leaf), $status)
    }
}

# ---------------------------------------------------------------------------
# ① 前置检查：Python >= 3.12、.NET SDK 8
# ---------------------------------------------------------------------------
Write-Stage "[1/7] 前置检查（Python >= 3.12、.NET SDK 8）"

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
Write-Stage "[2/7] 虚拟环境与锁定依赖"

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
Write-Stage "[3/7] 生成工具目录 JSON（backend/catalog.py）"

# Windows 上 catalog.py 默认按系统区域（GBK）写文件，会把仓库的 UTF-8 快照改脏；
# PYTHONUTF8=1 强制 UTF-8 输出（已验证与仓库快照字节等价）。
$env:PYTHONUTF8 = "1"
& $VenvPython "backend\catalog.py"
Assert-LastExit "backend\catalog.py"

# ---------------------------------------------------------------------------
# ④ OCR helper 构建（自包含单文件，零运行时依赖）
# ---------------------------------------------------------------------------
Write-Stage "[4/7] 构建 OCR helper（Sources\WindowsOcrHelper）"

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
Write-Stage "[5/7] 发布 WPF 应用并组装 dist\aipdf-windows"

& $DotNetExe publish "Sources\AIPDF.Windows\AIPDF.Windows.csproj" -c $Configuration -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false
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
# 翻译引擎包（backend/translate/*.py）；引擎以 ../backend 为 sys.path 顶层，故需整体拷贝。
Copy-Item -Path "backend\translate" -Destination (Join-Path $Dist "engine\backend\translate") -Recurse -Force
Remove-Item -Path (Join-Path $Dist "engine\backend\translate\__pycache__") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $HelperPublish "*") -Destination (Join-Path $Dist "helper") -Force
if (Test-Path -LiteralPath (Join-Path $ProjectDir "Install.ps1")) {
    Copy-Item -Path "Install.ps1" -Destination (Join-Path $Dist "scripts") -Force
}
else {
    Write-Host "提示：仓库根暂无 Install.ps1，dist\scripts 留空（由后续安装器任务补充）。" -ForegroundColor Yellow
}

$readme = @(
    "AIPDF · Windows 自包含构建产物",
    "",
    "这是一个自包含的构建目录：无需系统 Python，无需 .NET Desktop Runtime。各子目录：",
    "  app\                   WPF 界面程序（自包含发布，内置 .NET 运行时）",
    "  engine\python\         嵌入式 Python 3.14 运行时（便携，随目录移动）",
    "  engine\site-packages\  处理引擎依赖（按锁定版本离线安装）",
    "  engine\backend\        处理引擎代码",
    "  helper\                基于 Windows.Media.Ocr 的 OCR 辅助程序（自包含）",
    "  scripts\               安装脚本（Install.ps1，如已提供）",
    "",
    "运行：双击 app\AIPDF.Windows.exe。OCR 使用系统内置服务，无需额外安装。",
    "",
    "可选功能：",
    "  - Office / PDF/A 转换需要本机 LibreOffice：程序会自动探测安装位置，",
    "    或用环境变量 AIPDF_OFFICE 指定 soffice 可执行文件路径。",
    "  - 网址转 PDF 依赖 Chromium 浏览器：首次使用需联网下载（约 150MB，",
    "    由 Playwright 下载并缓存）；也可提前执行",
    "    engine\python\python.exe -m playwright install chromium。",
    "  - 本目录不捆绑 LibreOffice 与 Chromium；除网址转 PDF 外其余功能不联网。",
    "",
    "构建、安装步骤与故障排查见仓库文档：docs/构建与安装-Windows.md",
    "网址转 PDF 功能会联网获取输入网址的页面、脚本和资源；本地文件操作不上传文档。",
    ""
) -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $Dist "README-Windows.txt"), $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------------------------------------------------------------------------
# ⑥ 校验：布局断言 + dist 引擎冒烟 + 快速测试子集
# ---------------------------------------------------------------------------
Write-Stage "[6/7] 校验 dist 布局与引擎冒烟"

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

# ---------------------------------------------------------------------------
# ⑦ 自包含运行时：嵌入式 Python 解释器 + 离线依赖 wheel
# ---------------------------------------------------------------------------
Write-Stage "[7/7] 打包嵌入式 Python 运行时与离线依赖"

$ProgressPreference = "SilentlyContinue"
$EmbedVersion = "3.14.2"
$BuildCache = Join-Path $env:TEMP "aipdf-build"
$EmbedZip = Join-Path $BuildCache "python-$EmbedVersion-embed-amd64.zip"
$EmbedDir = Join-Path $Dist "engine\python"
$SitePackages = Join-Path $Dist "engine\site-packages"
$EmbedPython = Join-Path $EmbedDir "python.exe"

# 嵌入式 Python zip 按版本缓存在 %TEMP%，命中则跳过下载。
if (-not (Test-Path -LiteralPath $EmbedZip)) {
    New-Item -ItemType Directory -Path $BuildCache -Force | Out-Null
    Write-Host "下载 Python $EmbedVersion embeddable（约 12MB，缓存到 $EmbedZip）…"
    $embedUrl = "https://www.python.org/ftp/python/$EmbedVersion/python-$EmbedVersion-embed-amd64.zip"
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        curl.exe -L --fail -sS -o $EmbedZip $embedUrl
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $EmbedZip -Force -ErrorAction SilentlyContinue
            Exit-Fail @("下载 Python embeddable 失败（退出码 $LASTEXITCODE），请检查网络后重试。")
        }
    }
    else {
        try { Invoke-WebRequest -Uri $embedUrl -OutFile $EmbedZip }
        catch {
            Exit-Fail @("下载 Python embeddable 失败：$($_.Exception.Message)", "请检查网络后重试。")
        }
    }
}
Write-Host "解压嵌入式 Python 到 engine\python …"
New-Item -ItemType Directory -Path $EmbedDir -Force | Out-Null
Expand-Archive -LiteralPath $EmbedZip -DestinationPath $EmbedDir -Force
if (-not (Test-Path -LiteralPath $EmbedPython)) {
    Exit-Fail @("解压后未找到 engine\python\python.exe，构建中止。")
}

# ._pth：便携相对路径，dist 整体移动后仍然有效。存在 ._pth 时 sys.path
# 只由这些条目决定（脚本所在目录不会自动加入），..\backend 与
# ..\site-packages 分别是引擎导入 catalog 与第三方依赖的关键。
$pthContent = "python314.zip`r`n.`r`n..\site-packages`r`n..\backend`r`nimport site`r`n"
[IO.File]::WriteAllText((Join-Path $EmbedDir "python314._pth"), $pthContent)

# 构建期联网下载 wheel 到 %TEMP% 缓存：pip 本体 + 锁定依赖 + playwright，
# 按 3.14/win_amd64 标签解析；缓存键 = 锁文件哈希 + playwright 版本。
# playwright 不在锁定文件内，但 win32 的网址转 PDF 由它渲染（浏览器本体
# 按首次使用联网下载），必须随 dist 提供。
$WheelCache = Join-Path $BuildCache "wheels"
$PwVersion = & $VenvPython -c "from importlib.metadata import version; print(version('playwright'))"
Assert-LastExit "读取 venv 中的 playwright 版本"
$MarkerPath = Join-Path $WheelCache ".source"
$Marker = "{0}|{1}" -f (Get-FileHash "requirements.lock.txt" -Algorithm SHA256).Hash.Substring(0, 16), $PwVersion
$CacheStale = $true
if (Test-Path -LiteralPath $MarkerPath) {
    $CacheStale = ((Get-Content -LiteralPath $MarkerPath -Raw) -ne $Marker)
}
if ($CacheStale) {
    if (Test-Path -LiteralPath $WheelCache) { Remove-Item -LiteralPath $WheelCache -Recurse -Force }
    New-Item -ItemType Directory -Path $WheelCache -Force | Out-Null
    Write-Host "构建期下载 wheel 缓存（pip + 锁定依赖 + playwright）…"
    & $VenvPython -m pip download --disable-pip-version-check -d $WheelCache -q pip
    Assert-LastExit "pip download（pip wheel）"
    & $VenvPython -m pip download --disable-pip-version-check -d $WheelCache -q `
        --only-binary :all: --python-version $EmbedVersion --platform win_amd64 `
        -r requirements.lock.txt "playwright==$PwVersion"
    Assert-LastExit "pip download（锁定依赖 + playwright）"
    [IO.File]::WriteAllText($MarkerPath, $Marker)
}

# 离线安装：--no-index 从本地 wheel 缓存装入 dist\engine\site-packages。
# 以 <embedpy> <wheel>/pip 方式运行 pip（嵌入式包不带 pip 模块），全程
# 不写入绝对路径。
$PipWhl = Get-ChildItem -LiteralPath $WheelCache -Filter "pip-*.whl" |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $PipWhl) {
    Exit-Fail @("wheel 缓存中未找到 pip wheel，构建中止。")
}
Write-Host "离线安装依赖 wheel 到 engine\site-packages …"
& $EmbedPython "$($PipWhl.FullName)/pip" install --no-index --find-links $WheelCache `
    -r requirements.lock.txt "playwright==$PwVersion" --target $SitePackages `
    --no-warn-script-location -q
Assert-LastExit "离线安装依赖 wheel"

# 自包含布局断言（基础布局已在步骤 ⑥ 校验）。
$requiredStage7 = @(
    "engine\python\python.exe",
    "engine\python\python314._pth",
    "engine\python\python314.zip",
    "engine\site-packages\fitz",
    "engine\site-packages\PIL",
    "engine\site-packages\playwright",
    "app\hostfxr.dll"
)
foreach ($rel in $requiredStage7) {
    if (-not (Test-Path -LiteralPath (Join-Path $Dist $rel))) {
        Exit-Fail @("dist 自包含布局断言失败：缺少 $rel")
    }
}
Write-Host "自包含布局断言：$($requiredStage7.Count) 项关键文件全部在位。"

# 冒烟：用 dist 内嵌 Python 直接驱动引擎，验证 ._pth 相对路径解析与
# 离线依赖可用。vision 必须为 true（helper 锚点为安装根下的 helper\）；
# office 为可选功能，构建机未装 LibreOffice 时为 false，不算失败。
$distHealthRequest = '{"operation":"health"}'
$distHealthOutput = $distHealthRequest | & $EmbedPython (Join-Path $Dist "engine\backend\engine.py")
Assert-LastExit "dist 自包含引擎冒烟（health）"
try {
    $distHealth = $distHealthOutput | ConvertFrom-Json
}
catch {
    Exit-Fail @("dist 自包含引擎冒烟：stdout 不是合法 JSON（协议被污染）。")
}
if (-not $distHealth.ok) {
    Exit-Fail @("dist 自包含引擎冒烟：health 返回 ok=false。")
}
if (-not $distHealth.vision) {
    Exit-Fail @("dist 自包含引擎冒烟：vision=false，helper\WindowsOcrHelper.exe 路径锚点失效。")
}
Write-Host ("dist 自包含引擎冒烟：health ok=true（engine={0}，vision={1}）" -f $distHealth.engine, $distHealth.vision)
if ($distHealth.office) {
    Write-Host "检测到本机 LibreOffice，Office/PDF/A 转换可用。"
}
else {
    Write-Host "未检测到本机 LibreOffice：Office/PDF/A 为可选功能，可安装后自动探测，或用 AIPDF_OFFICE 指定 soffice 路径。" -ForegroundColor Yellow
}
$ProgressPreference = "Continue"

# ---------------------------------------------------------------------------
# 可选：Authenticode 签名（-Sign）。位于 [7/7] 之后，此时 dist 产物已就绪。
# ---------------------------------------------------------------------------
if ($Sign) {
    Write-Stage "[+] Authenticode 签名 dist 顶层可执行文件（-Sign）"
    Invoke-DistSigning
}

Write-Host ""
Write-Host ("应用已生成：{0}" -f $Dist) -ForegroundColor Green
