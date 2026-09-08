# Windows 构建与安装

构建脚本将 Python 运行时、依赖包、WPF 界面和 OCR 助手打包进 `dist/aipdf-windows/`，形成自包含发行目录。**运行发行版不需要系统安装 Python、.NET Desktop Runtime 或任何外部依赖**（LibreOffice 可选）。Chromium **不随包分发**；网址转 PDF（`html_to_pdf`）首次使用时需联网下载（约 150 MB，`playwright install chromium`）。构建本身需要 Python 3.12+ 和 .NET SDK 8.0+。

## 快速开始

1. 从 GitHub 仓库下载 ZIP 并解压到本地目录（路径不含中文和空格为宜）。
2. 在 PowerShell 中进入解压目录，运行构建脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

3. 脚本完成后，`dist\aipdf-windows\` 即为可运行的发行目录。

## 前置条件

| 组件 | 要求 | 安装方法 |
|------|------|----------|
| Windows | Windows 10 1903+ 或 Windows 11（需要 Windows.Media.Ocr API） | 系统自带 |
| Python | 3.12 或更高版本 | [Python 官网](https://www.python.org/downloads/)下载安装；安装时勾选「Add Python to PATH」 |
| .NET SDK | 8.0 或更高版本（用于编译 WPF 界面和 OCR 助手，仅构建阶段需要） | [.NET 官网](https://dotnet.microsoft.com/download/dotnet/8.0)下载 SDK |
| LibreOffice（可选） | Word/PPT/Excel 与 PDF/A 转换需要；构建时不需要，运行时按需使用 | [LibreOffice 官网](https://www.libreoffice.org/download/)安装到默认路径；没有它也能使用其他工具 |
| 网络与磁盘 | 首次构建需联网下载 Python 依赖和 Playwright Chromium | 预留 2 GB 以上磁盘空间 |

## 构建步骤

构建脚本 `scripts/build.ps1` 自动完成以下 7 个阶段：

1. **前置检查**：验证 Python ≥ 3.12、.NET SDK 8 已安装并可执行。
2. **Python 环境**：创建项目 `.venv`，按 `requirements.lock.txt` 安装锁定依赖；额外安装 Playwright（仅 Windows）并下载 Chromium 浏览器。
3. **工具目录**：运行 `backend/catalog.py` 生成 `backend/catalog.json`。
4. **OCR 助手**：编译并发布 `Sources/WindowsOcrHelper`（自包含单文件，不依赖 .NET 运行时）。
5. **WPF 界面**：发布 `Sources/AIPDF.Windows`（自包含，内嵌 .NET 运行时）。
6. **组装与校验**：按预设布局组装 `dist/aipdf-windows/`，运行引擎健康检查和单元测试子集。
7. **打包**：将嵌入式 Python 运行时和锁定依赖包打包进发行目录。构建完成后 `dist/aipdf-windows/` 可直接运行，不需要系统安装 Python 或 .NET Desktop Runtime。Chromium 不包含在内，首次使用网址转 PDF 时需联网下载。

### 构建参数

```powershell
# Release 构建（默认）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# Debug 构建
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Debug

# 跳过测试
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SkipTests

# 构建并做 Authenticode 签名（证书参数见下文「代码签名与 SmartScreen」）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Sign -CertificateSubject "你的证书主题名"
```

### 构建产物布局

```
dist/aipdf-windows/
├── app/                  WPF 应用程序（AIPDF.Windows.exe + 依赖）
├── engine/
│   ├── backend/          Python 引擎（engine.py + catalog.py + catalog.json）
│   ├── python/           嵌入式 Python 运行时（免系统安装）
│   └── site-packages/    锁定依赖包（PyMuPDF、Playwright 等）
├── helper/               OCR 助手（WindowsOcrHelper.exe，自包含）
└── scripts/              安装脚本（Install.ps1）
```

## 安装步骤

构建完成后，`dist/aipdf-windows/` 目录可直接运行。如需安装到固定位置：

1. 将 `dist/aipdf-windows/` 整个目录复制到目标位置（如 `C:\Program Files\AIPDF`）。
2. 运行 `AIPDF.Windows.exe` 启动应用。

> 如需自动安装到固定位置，可运行 `scripts\Install.ps1`（不需要管理员权限）。安装器支持 `-InstallDir` 指定路径、`-NoShortcut` 跳过快捷方式创建、`-Uninstall` 卸载。

## 代码签名与 SmartScreen 警告

应用默认不签名。首次运行未签名应用时，Windows 可能弹出「Windows 已保护你的电脑」蓝色对话框（SmartScreen 筛选器）：点击「更多信息」→「仍要运行」即可，这不影响功能。

### 可选签名步骤（-Sign）

构建脚本支持在构建完成后对发行目录做 Authenticode 签名，需要本机安装 Windows SDK 的签名工具（脚本会自动定位 `C:\Program Files (x86)\Windows Kits\10\bin` 下最新版本的 `signtool.exe`）：

```powershell
# 方式一：使用证书存储中的证书（按主题名查找）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Sign -CertificateSubject "你的证书主题名"

# 方式二：使用 PFX 证书文件
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Sign -CertificateFile "代码签名证书.pfx" -CertificatePassword "PFX 密码"
```

签名对象为 `app\AIPDF.Windows.exe`、`app\` 顶层的本地 DLL 与 `helper\WindowsOcrHelper.exe`。签名采用 SHA-256 摘要，并通过 DigiCert 时间戳服务联网加时间戳。不带 `-Sign` 时构建行为不变。

### 证书要求

- 自签名证书仅适合本机测试。**正式分发需要 CA 签发的代码签名证书**（OV/EV），否则 SmartScreen 依旧会警告，云信誉也不会累积到你的发布上。
- 证书会过期；更换证书后需要重新签名再分发。

### 实测的拦截行为（云信誉）

Windows 对新二进制的放行依赖云端信誉。在本机观察到：

- 旧构建的未签名二进制（已在系统上运行过）通常可正常运行；
- **全新构建的未签名二进制可能被应用程序控制拦截**：以子进程方式启动时报 `WinError 4551`（策略阻止该文件）。

处理方式：用 `-Sign` 对产物签名，或等待该二进制的微软云信誉建立后重试。

## 已知降级

以下功能在 Windows 上与 Mac 版本存在差异：

### 扫描透视校正（scan 工具）

Mac 版使用 Apple Vision 进行纸张边缘检测和透视校正。Windows 无等价 API，当前降级为**仅图像增强，不做透视校正**。扫描照片无法自动纠偏。

### HEIC 输入（images_to_pdf、scan 工具）

Windows 无原生 HEIC 解码支持。iPhone 直出的 HEIC/HEIF 格式图片无法直接使用，会提示用户预转换为 JPG/PNG 格式。

### URL 转 PDF（html_to_pdf 工具）

Mac 版使用 WebKit 无头渲染。Windows 版使用 Playwright + Chromium 替代。Chromium **不随包分发**，首次使用时需联网下载（约 150 MB，通过 `playwright install chromium`）。渲染行为可能与 WebKit 有细微差异。

### OCR 文字识别（ocr 工具）

Mac 版使用 Apple Vision。Windows 版使用 Windows.Media.Ocr API，中文识别需要系统安装对应语言包（设置 → 时间和语言 → 语言 → 添加中文语言包）。

## 故障排查

### Python 相关

**"python 不是内部或外部命令"**
确保 Python 已安装且添加到 PATH。在 PowerShell 中运行 `python --version` 验证。

**"pip 安装依赖失败"**
检查网络连接。部分依赖需要从 PyPI 下载编译好的 wheel。如果网络受限，可尝试使用镜像源。

### .NET 相关

**"dotnet 不是内部或外部命令"**
构建阶段需要 .NET SDK 8.0+。安装后重启终端，确认 `dotnet --version` 输出 8.x。发行版本身不需要系统安装 .NET Desktop Runtime（已自包含）。

### OCR 相关

**OCR 识别结果为空**
确认已安装 Windows.Media.Ocr 语言包。在 PowerShell 中运行 `Get-WinUserLanguageList` 查看已安装语言。

### Playwright/Chromium 相关

**"网页加载失败"**
确保 Chromium 已下载。Chromium 不随发行版分发，需手动安装：

```powershell
.venv\Scripts\python.exe -m playwright install chromium
```

### LibreOffice 相关

**Word/Excel/PPT 转换失败**
确认 LibreOffice 已安装在默认路径（`C:\Program Files\LibreOffice\program\soffice.com`）。也可通过环境变量 `AIPDF_OFFICE` 指定 `soffice.com` 路径（注意是 `soffice.com` 不是 `soffice.exe`，后者是 GUI 启动器，命令行会挂起）。

## 运行测试

```powershell
# 运行引擎测试
.venv\Scripts\python.exe tests\test_engine.py -v

# 运行 Windows 特定测试
.venv\Scripts\python.exe tests\test_cancel_windows.py -v
.venv\Scripts\python.exe tests\test_paths_windows.py -v
.venv\Scripts\python.exe tests\test_ocr_windows.py -v
.venv\Scripts\python.exe tests\test_url2pdf_windows.py -v
.venv\Scripts\python.exe tests\test_stress_windows.py -v
.venv\Scripts\python.exe tests\test_engine_utf8_windows.py -v
.venv\Scripts\python.exe tests\test_installer_windows.py -v
.venv\Scripts\python.exe tests\test_office_windows.py -v

# 运行 WPF 单元测试
dotnet test Sources\AIPDF.Windows
```

测试自行生成示例文件，不读取个人文件。
