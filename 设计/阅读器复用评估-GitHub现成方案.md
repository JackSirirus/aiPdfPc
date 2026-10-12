# 阅读器复用评估：GitHub 上有没有「现成 PDF 阅读器」可以拿来改

> 日期：2026-10-11
> 触发问题：维护者问「检查我的需求，能不能从 GitHub 上找到现成的 PDF 阅读器进行修改」
> 检索方式：GitHub 官方 Search/Repos API 直查（未授权，10 次检索/分钟配额）+ 仓库 README/LICENSE 原文核对
> 检索口径：stars ≥ 100、排除归档、Top 10（按仓库规范默认；WPF 生态候选不足时放宽并显式标注星标）
> 结论一句话：**有唯一一个真正可整包 fork 的候选（KillerPDF，4074★，GPL-3.0，WPF/.NET 10），但它替代的正好是你已经做完并验收过的那一层，而它没有的正是本项目最贵的那一层；净收益为负，不建议整包 fork。** 三条「嵌入现成内核」的中间路线也全部不成立（Edge 内置查看器已被微软自己关掉批注、商业 SDK 许可不通、pdf.js/mupdf.js 等于用 Web 重写且批注类型更少）。**反向证据是：Windows 上新一代开源阅读器几乎都选「PDFium 渲染 + 独立文档引擎」，与你现在的做法一致。** 真正值得做的是组件级复用评估（KillerPdf.Engine）与一次零架构改动的 PDFiumCore 升级。

---

## 1 需求复核（本报告的判据来源）

复核对象：`设计/v1.0-阅读器/需求总结.md`、`设计/v1.0-阅读器/prd/04-阅读器PRD.md`、`05/06/07/08` 验收记录、`设计/v1.1-翻译/需求总结.md`、`设计/v1.2-翻译供应商配置/需求总结.md`、`memory/backlog.md`。

### 1.1 需求清单

| 维度 | 内容 |
|---|---|
| 阅读能力 | 21 条 FR：连续滚动+虚拟化、缩略图/目录/书签/搜索四面板、页码跳转、九档缩放+适配、多标签、搜索降级（无文本层→引导 OCR）、选中浮菜单、工具子菜单、打印、文件关联、单实例、起始页、最近文件、工具网格、四通道映射、设置面板与存储 |
| 批注 | 高亮 + 笔记（v3 扩展手写/形状）；**就地写回原 PDF**；2s 防抖；独立快速通道；30s 超时；原子保存（临时文件→`os.replace`→`fsync`）；保存前 size/mtime 冲突检测；以 PDF 注释为唯一真相源；工具执行前强制 flush |
| 翻译（v1.1/v1.2） | 右栏划词/整篇双 Tab + 批量工具页；mono/dual PDF 新标签打开；5 类预设 prompt + 术语表；14 家云端预设 + 本地服务 + 自定义供应商；openai/anthropic 双协议；密钥仅经 stdin、交凭据管理器、不落盘 |
| 工具耦合 | 32 工具四通道（起始页 6 / 右抽屉 2 / 向导 21 / 临时标签 3）；EngineBridge 一进程一请求 stdin/stdout JSON；任务目录输出；密码逐请求 |
| 硬约束 | Windows 10/11；.NET 8 WPF；自有代码 **AGPL-3.0-only**；复用代码许可须兼容；**不添加遥测/隐式上传/后台模型服务**；**不下载模型与字体** |

### 1.2 需求复核后的关键判断

**需求里真正稀缺、也最贵的不是「阅读器」，而是「阅读器 × 32 工具 Python 引擎 × 翻译」的耦合层。**

- 纯阅读能力（连续滚动、目录、搜索、批注）在开源世界是成熟品，随便一个头部项目都覆盖。
- 但「文档为中心、工具降级为文档上下文」的四通道信息架构、EngineBridge 子进程协议、批注保存快速通道与 flush 纪律、翻译供应商与密钥纪律、A+C 暖纸设计体系——这些是本项目独有的、在 GitHub 上没有对应物的部分。
- 因此「找一个现成阅读器来改」这个问题的实际含义是：**用别人的阅读层，换掉自己的阅读层**。工具耦合层、翻译层、设计体系、测试资产不会被替代，只会被重写。

### 1.3 现有资产规模（放弃成本量化）

| 资产 | 规模 |
|---|---|
| WPF 工程总量 | 82,904 行（497 个 `.cs` + 62 个 `.xaml`） |
| 阅读器专属生产代码 | ≈ 16,978 行（`Views/Reader`+`Rendering`+`Annotations`+`Search`+`Controls` 8,094；阅读相关 VM 3,883；阅读相关 Service 5,001） |
| 阅读器专属测试 | 14,961 行 / 67 个测试文件 |
| 工具耦合层 | 15,191 行（`Views` 去掉 `Reader`）+ 4,802 行（`Bridge`/`Tools`/`Models`） |
| Python 引擎 | 4,732 行（与 macOS 端共享） |
| 测试基线 | v3.0 / A+C 收官 847/847 通过；当前测试方法数 1,116 |
| 第三方依赖 | 仅 3 个 NuGet：PDFiumCore 152.0.7961、bblanchon.PDFium（Apache-2.0）、CommunityToolkit.Mvvm（MIT） |

---

## 2 检索执行记录

### 2.1 检索词（10 组 API 查询 + 4 组网页检索）

| # | 查询 | 目的 |
|---|---|---|
| 1 | `pdf viewer wpf` | 高召回 WPF 阅读控件/应用 |
| 2 | `pdf reader wpf` | 补「reader」同义词盲区 |
| 3 | `topic:wpf topic:pdf` | 按主题标签召回 |
| 4 | `pdf viewer language:C# stars:>30` | C# 控件层 |
| 5 | `pdf reader language:C#` | C# 完整阅读器 |
| 6 | `pdfium stars:>50` | 内核/绑定层 |
| 7 | `pdf reader stars:>1500` | 头部完整阅读器 |
| 8 | `pdf viewer stars:>1500` | 头部完整查看器 |
| 9 | `pdf editor wpf language:C#` | 找 WPF 阅读/编辑器（关键突破） |
| 10 | `pdf editor language:C# stars:>100` | 同上，按活跃度排序 |

补充网页检索：pdf.js 批注写回能力、SumatraPDF 许可与技术栈、WPF 开源 PDF 编辑器线索。

### 2.2 检索时间与配额

检索时间：2026-10-11。GitHub 未授权配额：Search API 10 次/分钟、Core API 60 次/小时；本报告共 10 次 Search + 6 次 Core 调用，未触限。**星标与时间戳为 2026-10-11 快照，会随时间变化。**

**补充调研（同日，独立进行，三轮）**：
- **第二轮（组件/绑定层）**：逐仓库一手核对 `LICENSE` / `csproj` / `fpdf_save.h` / `fpdf_annot.h` 原文（不采信 GitHub 的自动许可标签），并用 NuGet 检索反向补漏商业控件 → 见 4.5。
- **第三轮（中间路线与同类先例）**：核实 pdf.js 源码、MS Learn 官方文档、WebView2 公告、npm 包清单，以及 Stirling-PDF / BabelDOC / Docspell / PDF4QT 的架构与许可 → 见 4.3、4.6。
- **第四轮（整包 fork 候选）**：逐仓库核实完整阅读器的许可原文、UI 技术栈与批注写回能力 → 见 4.7。

三轮均**未做任何编译或运行验证**，全部为静态阅读与文档核实；局限见第 6 节。

---

## 3 候选池与分类

### 3.1 A 类：WPF / .NET 可整包 fork 的完整阅读器（本项目栈）

| 仓库 | 星标 | 许可 | 最近推送 | 形态 | 关键判断 |
|---|---:|---|---|---|---|
| [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF) | **4,074** | GPL-3.0 | 2026-10-07 | WPF 完整应用（.NET 10） | **唯一头部候选**，功能覆盖超出本项目阅读需求；但无本项目工具/翻译耦合层 |
| [CalyPdf/CalyPdf](https://github.com/CalyPdf/CalyPdf) | 230 | MIT | 2026-10-10 | **Avalonia** 完整应用 | 活跃但自述 alpha；无批注/表单；Avalonia ≠ WPF，不可嵌入 |
| [reliak/moonpdf](https://github.com/reliak/moonpdf) | 199 | GPL-3.0 | 2019-09-05 | WPF 控件 + 示例 | **2019 年后停更**，仅能当参考 |
| [pvginkel/PdfiumViewer](https://github.com/pvginkel/PdfiumViewer) | 1,026 | Apache-2.0 | 2021-07-28 | **WinForms** 控件 | 已归档；WinForms 需 `WindowsFormsHost`；无批注/表单/搜索 API |
| [bezzad/PdfiumViewer](https://github.com/bezzad/PdfiumViewer) | 175 | Apache-2.0 | 2026-04-22 | WinForms 控件（.NET Core 移植） | 已归档 |
| [Dtronix/DtronixPdf](https://github.com/Dtronix/DtronixPdf) | 55 | MIT | 2025-03-24 | 库 | 无批注/表单/搜索 API（已在 `02-pdfium绑定选型报告` 排除） |
| [chrisg32/AvaloniaPdfViewer](https://github.com/chrisg32/AvaloniaPdfViewer) | 52 | MIT | 2025-03-25 | Avalonia 控件 | 栈不匹配 |
| [TheEightBot/MauiNativePdfView](https://github.com/TheEightBot/MauiNativePdfView) | 101 | MIT | 2026-10-05 | .NET MAUI 控件 | 栈不匹配 |
| [ComPDFKit/compdfkit-pdf-sdk-windows](https://github.com/ComPDFKit/compdfkit-pdf-sdk-windows) | 104 | Apache-2.0（仓库） | 2026-08-07 | 商业 SDK 的示例仓 | **闭源商业 SDK**，与 AGPL 项目不兼容 |
| 其余 WPF 阅读器（pPdf / ClearPDF / Pdf.Wpf / PDFSculpt / NexusPDF / mpdf-desktop / vellum / Bookie …） | **0–4** | 混杂 | 2026 年新仓居多 | 小型应用/示例 | 星标 0–4，**无成熟度**；其中 `FirekeeperPhate/pPdf`、`thescurry/ClearPDF` 功能描述与本项目高度重合，可当功能清单参照 |

**A 类结论**：WPF 生态里除 KillerPDF 外，**不存在成熟可整包 fork 的完整 PDF 阅读器**。最接近的控件型项目（PdfiumViewer）已归档 5 年且只是 WinForms 渲染控件。

### 3.2 B 类：跨平台完整阅读器（栈不匹配）

| 仓库 | 星标 | 许可 | 语言/UI | 最近推送 | 对 WPF 项目的意义 |
|---|---:|---|---|---|---|
| [mozilla/pdf.js](https://github.com/mozilla/pdf.js) | 53,990 | Apache-2.0 | JavaScript | 2026-10-10 | 唯一可嵌入路线（WebView2）；批注可写回 PDF（见 4.3） |
| [koreader/koreader](https://github.com/koreader/koreader) | 30,217 | AGPL-3.0 | Lua（电子墨水设备） | 2026-10-10 | 设备端阅读器，无 Windows 桌面形态 |
| [koodo-reader/koodo-reader](https://github.com/koodo-reader/koodo-reader) | 28,464 | AGPL-3.0 | JS/Electron | 2026-10-09 | 整包是 Electron 应用，WPF 无法复用其 UI |
| [readest/readest](https://github.com/readest/readest) | 25,036 | AGPL-3.0 | TS（Tauri） | 2026-10-11 | 同上，Tauri 栈 |
| [sumatrapdfreader/sumatrapdf](https://github.com/sumatrapdfreader/sumatrapdf) | 17,732 | (A)GPLv3（部分 BSD） | C / Win32 + 内置 MuPDF | 2026-10-11 | 能力几乎覆盖全部清单：多标签/标签组恢复/缩略图/目录/搜索/打印/**批注创建并写回原 PDF**（`Ctrl+Shift+S`，官方文档）。但 C++ Win32 单进程应用，UI 与渲染不可分离；master 新增 `AIChatPanel.cpp`/`SelectionTranslate.cpp` 等联网功能，fork 必须剥离 |
| [ahrm/sioyek](https://github.com/ahrm/sioyek) | 9,935 | GPL-3.0 | C / Qt | 2026-10-07 | Qt 栈，嵌入 WPF 需进程外承载 |
| [itsjunetime/tdf](https://github.com/itsjunetime/tdf) | 1,992 | AGPL-3.0 | Rust（TUI） | 2026-08-16 | 终端阅读器，不适用 |

**B 类结论**：这些项目阅读能力成熟，但**整包 fork 等于放弃 WPF 外壳**——即放弃 32 工具四通道、翻译层、A+C 设计体系与 847 项测试。除 pdf.js（可嵌入）外，无一能被 WPF 应用复用。

### 3.3 C 类：可复用组件/内核

| 组件 | 许可 | 状态 | 与本项目的关系 |
|---|---|---|---|
| PDFiumCore + bblanchon.PDFium | Apache-2.0 | 在用（152.0.7961） | **已在用**，即 `02-pdfium绑定选型报告` 的选型结论 |
| [UglyToad/PdfPig](https://github.com/UglyToad/PdfPig) | Apache-2.0 | 活跃 | 文本提取替代方案（KillerPDF/CalyPdf 使用） |
| **KillerPdf.Engine** | GPL-3.0 | NuGet 包，.NET 10 | **本次最有价值的新发现**（见 4.4） |
| pdf.js（WebView2 承载） | Apache-2.0 | 活跃 | 中间路线，见 4.3 |

---

## 4 关键判断与逐轮复核

### 4.1 头部候选 KillerPDF 的真实成色（已核实，非搜索摘要）

| 项 | 实测值（2026-10-11 API） |
|---|---|
| 星标 / fork / open issues | 4,074 / 324 / 48 |
| 许可 | GPL-3.0 |
| 技术栈 | C# + WPF（`net10.0-windows`），C# 源码 7.31 MB；PDFium 渲染 + PdfPig 文本提取 + 自有 KillerPdf.Engine 文档引擎 |
| 创建 / 最近推送 | 2026-04-16 / 2026-10-07 |
| 发布节奏 | v1.8.5 → v1.8.80 密集发布，最新 2026-10-05，每次 4 个产物 |
| 贡献者 | 主作者 922 commits，其余 9 人合计约 85 commits；近 30 天 100 commits 全部来自主作者 |
| 质量信号 | 1,436 个引擎测试；2,907 文件增量结构语料门；qpdf / veraPDF / OpenSSL 校验 |
| 功能 | 四种视图（单页/连续/双页/网格）、标签页、分屏、批注（高亮/文本/绘制/线条/图章）、Tesseract OCR、页面组织、表单填写并写回、数字签名、打印预览、flatten、全文搜索、19 语言、13 主题、加密 PDF、修复 |
| 合规注意 | 无遥测，但**默认开启启动更新检查（访问 GitHub）**；**OCR 语言包按需下载**；打包了 Tesseract |
| 代码形态 | `MainWindow.xaml` 单文件 265 KB、`App.xaml.cs` 103 KB —— 单体窗口结构 |

**判断**：这是真实、活跃、工程量扎实的项目，不是空壳。**但它是「完整产品」，不是「可嵌入控件」**；fork 它等于接管它的整个产品（品牌、更新通道、19 语言、主题体系、自有工具集）。

### 4.2 为什么整包 fork 仍然不划算

| 维度 | 结论 |
|---|---|
| 许可 | **兼容**：GPLv3 可与 AGPLv3 组合，组合作品按 AGPLv3 分发（AGPLv3 §13）。需保留其版权与许可声明，并记录其 README 中「fork 必须 GPLv3」表述与本项目 AGPL-3.0-only 的关系 |
| 运行时 | KillerPDF 需 **.NET 10**；本项目钉 .NET 8（SDK 8.0.424）、构建脚本、安装器、SAC 放行策略均按 .NET 8 建立 |
| 被替代的 | 只有「阅读层」——而这层**已经做完并验收**（21 FR + 847 测试 + A+C 设计体系） |
| 未被替代的（需重写） | 32 工具四通道 + 向导/右抽屉/临时标签/起始页 + EngineBridge 协议 + 任务目录/密码纪律 + 翻译 v1.1/v1.2（含供应商配置与密钥纪律）+ 阅读状态/会话恢复 + 单实例与文件关联 + 设计 token 与主题审计 |
| 引擎冲突 | KillerPDF 自带 merge/split/OCR/crop/rotate/compare/水印/页码/flatten 等实现，与本项目 32 工具（Python/PyMuPDF，与 macOS 共享）**功能重叠且互斥**，必须二选一 |
| 合规清理 | 必须关闭启动更新检查、去除品牌与下载渠道（winget/choco/官网）、处理 Tesseract 语言包按需下载（违反「不下载模型与字体」） |
| 上游风险 | 事实上的单作者项目、周更节奏、48 个 open issues；fork 后立刻分叉，无法跟随上游 |

**净判断**：整包 fork 是「用一个别人的阅读层，换掉自己的阅读层，同时推倒自己的耦合层」——**成本远大于收益**。除非维护者对现有阅读器的 UI 质量或维护成本已到「愿意重来」的程度，否则不成立。

### 4.3 中间路线：WebView2 + pdf.js（已核实关键事实）

- **批注类型反而不如现自研路径**：源码核实 `src/shared/util.js` 的 `AnnotationEditorType` 与 `web/viewer.html` 实际暴露的按钮，pdf.js 6.4.299 **只能创建 6 类**——高亮、自由文本、墨迹、图章、签名、评论；**不支持下划线、删除线、波浪线、方框、圆、直线、多边形/折线**（`STRIKEOUT` 只出现在读取渲染已有批注的路径）。本项目 v3.0 已支持高亮/笔记/手写/**形状**——走 pdf.js 会在批注覆盖上**倒退**。
- **写回语义其实很好，交付方式才是问题**：源码证据链核实——批注以 `pdfjs_internal_editor_` 前缀存入 `AnnotationStorage` → `src/core/worker.js` 的 `SaveDocument` → `document.js` 的 `getNewAnnotationsMap` + `AnnotationFactory.saveNewAnnotations` 并入页面 `/Annots` → `src/core/writer.js` 的 `incrementalUpdate({ originalData: stream.bytes, … })` **把变更追加到原文件字节之后（增量更新，不重写全文）**，且 `getTrailerDict` 中 `newXref.setIfDefined("Encrypt", xrefInfo?.encryptRef)` **保留加密字典**（即加密文档保存后仍是加密的）；无变更时原样返回 `stream.bytes`。限制：纯 XFA 文档抛 `XFA: Cannot save new annotations`。
  **但交付方式是下载**：`save()` → `saveDocument()` → `downloadManager.download(...)`；且 `annotationStorage` 不落任何持久化存储（`web/app.js` 全文无 `localStorage`/`indexedDB`/`sessionStorage`），刷新即丢。要就地写回必须由 WPF 宿主拦截下载或经 `chrome.webview.postMessage` 取回 ArrayBuffer 再原子写盘——**批注的权威副本变成浏览器内存**，崩溃恢复要自建。
  → **对本项目最有价值的不是「用 pdf.js」，而是这套写回语义模型**：增量追加 + 保留 Encrypt 字典，可作为决策 17/19「就地写回」的自研参照（现路径是「临时文件 → `os.replace`」全量替换）。
- **工程代价（已核实）**：本地内容**不能用 `file://`**（官方文档明确其不支持 secure-context API 与跨源资源），必须用 `SetVirtualHostNameToFolderMapping`；pdf.js 资产需自打包（`cmaps` 169 / `iccs` 2 / `standard_fonts` 16 / `wasm` 13 等，解包约 35 MB，默认不请求 CDN）；且 **`pdfjs-dist` 不含完整 viewer 应用**（无 `viewer.html`/`app.js`/`locale`），要官方 UI 得自行从仓库构建。
- **WebView2 Runtime 是微软专有组件**（可分发但非 OSS，须如实标注）；Fixed Version 分发 **>250 MB**，Win10 上还需对目录执行两条 `icacls` 授权；Evergreen 需检测 Runtime 是否存在。**其遥测行为无官方关闭文档**——与「不得引入遥测」硬约束存在未决冲突（见第 6 节）。
- **许可无碍**：pdf.js 为 Apache-2.0（npm 与仓库 LICENSE 双核实）。
- 结论：**技术上可行，但等于用「重写阅读与批注层」换「更成熟的阅读 UI」，且批注类型变少**。仅当未来要重做阅读 UI 时才值得重新评估。

**同路线的另两个变体（一并排除）**：
- **WebView2 直接内嵌 Edge 内置 PDF 查看器**：**不可行**。微软 2021 年以 `WebView2Announcements#21` 移除 WebView2 的 PDF 批注（Draw/Highlight/Erase 全部下线），2023-01 在 `WebView2Feedback#1638` 明确「no plans to re-add this feature」；宿主控制面只剩 `HiddenPdfToolbarItems`（保存/打印/缩放/旋转/书签/搜索等按钮的隐藏），**拿不到文本层、页面位图与批注**；且渲染引擎自 Edge 116 起改由 Adobe Acrobat 驱动（策略 `NewPDFReaderWebView2List`），是黑盒。
- **CefSharp + pdf.js**：pdf.js 的代价全有，另加自带 Chromium 的分发与更新负担，不推荐。

### 4.4 真正值得评估的复用点：KillerPdf.Engine

`KillerPdf.Engine` 是 KillerPDF 内一个**独立、无 UI、已发布 NuGet 的 .NET 文档引擎**（GPL-3.0，.NET 10，1,436 测试 + 2,907 文件语料门），能力与本项目 Python 引擎的痛点高度对应：

| 本项目已记录的 PyMuPDF 痛点（`learnings.md`） | KillerPdf.Engine 的对应能力 |
|---|---|
| `PDF_ENCRYPT_KEEP` 在用户密码会话下写坏新增注释字符串，只能改用显式 AES-256 重加密 + 随机 owner 密码（已知限制） | RC4 / AES-128 / AES-256、crypt filters、权限位、增量更新与重写 |
| MuPDF 进程级 AES 状态缺陷 → 必须一进程一请求 | 无此约束（纯托管库，可进程内调用） |
| `page.annots()` 在加密文档上读字符串不可靠、annot 对象跨页失效 | 注释/表单 widget 的完整读写 API、稳定对象标识 |
| 就地保存靠「临时文件→`os.replace`」全量重写 | **字节保留的增量更新**（保留原字节前缀，签名/审计友好） |
| redact 删除全部既有批注（需 UI 警告） | 注释与 redaction 的类型化编辑 API |

**这意味着一条与整包 fork 无关的、更窄的技术路径**：把「批注/表单/修复/加密保存」从 Python 引擎的往返里拿出来，改用 KillerPdf.Engine 在 WPF 进程内完成，可能同时消掉三个已知缺陷与「快速通道 + 30s 超时 + 一进程一请求」的复杂度。

**但必须先做 spike 并回答三个问题**：
1. **双引擎还是单引擎**：Windows 走 C# 引擎、macOS 仍走 `backend/engine.py` → 双实现分叉；若要统一，等于重写 macOS 冻结路径（违反 additive-only 约定）。
2. **.NET 10 迁移**：NuGet 包 target .NET 10；本项目 .NET 8，需要评估升级面（含构建/安装器/SAC）。
3. **许可与治理**：GPL-3.0 依赖并入 AGPL-3.0-only 项目合规（组合作品按 AGPLv3），但需接受对单作者上游的依赖；同时 `THIRD_PARTY_NOTICES.md` 与许可快照需同步。
   - 已核实：其 csproj 为 `net10.0` + `PackageLicenseExpression=GPL-3.0-only`；**未实测**能否降级到 `net8.0` 重定向编译。
   - **→ 已实测（2026-10-11，见第 7 节）：可行性成立，但不能替换现路径**——首要阻碍是 PyMuPDF 产出的加密 PDF（`/Encrypt` 直接字典）会被该引擎在 Open 阶段拒绝。

---

### 4.5 组件层独立复核（第二轮调研，一手文件核实）

第二轮以 LICENSE / csproj / C 头文件原文为准（不以 GitHub 自动识别的许可标签为准），产出四条会影响架构判断的事实：

**① PDFium 系永远做不到「保存回加密 PDF」。** 读 `public/fpdf_save.h` 原文：`FPDF_SaveAsCopy` / `FPDF_SaveWithVersion` **只有 `flags` 参数，没有任何加密或口令参数**。推论：**渲染栈可以换，写回引擎不能只靠 PDFium**；「加密文档批注保存后仍加密」这条硬需求必须继续由 PyMuPDF（现状）或 KillerPdf.Engine / iText7 这类真正的写入引擎承担。这从外部印证了本项目决策 18/19 的分工。

**② PDFium 不能创建形状批注。** 读 `public/fpdf_annot.h`：`FPDFAnnot_IsSupportedSubtype` 明确只列 **freetext / highlight / ink / link** 四种可创建；`FPDFPage_CreateAnnot`、`FPDFPage_RemoveAnnot`、`FPDFAnnot_AddInkStroke` 均在。推论：v3.0 的**形状批注（Square/Circle）必须留在写入引擎侧**，这也是本项目已有的分工。

**③ 四处许可需按原文纠正（GitHub 标签不可信）：**

| 组件 | GitHub 标签 | LICENSE 原文（已核实） | 结论 |
|---|---|---|---|
| ArtifexSoftware/MuPDF.NET | NOASSERTION | **Artifex Community License v1**（明确不覆盖商业使用） | ❌ **不是 AGPL**，商业使用需付费，出局 |
| ArgusMagnus / Zubastic PDFiumSharp | NOASSERTION | **MS-RL** | ❌ 与 `02-pdfium绑定选型报告` 结论一致 |
| empira/PDFsharp | NOASSERTION | **MIT** | ✅ 纯写入库，与阅读栈无关 |
| cube-soft/cube.pdf | NOASSERTION | **按子项目混合**：`Cube.Pdf` / `Cube.Pdf.Pdfium` = Apache-2.0；`Cube.Pdf.Itext` / 应用 = AGPLv3 | ⚠️ 仅 Apache 的两个库可用 |
| itext/itext-dotnet | — | **AGPL-3.0 或商业**双许可 | ✅ 走 AGPL 分支天然兼容，但**无阅读/渲染能力**，对阅读栈零帮助 |

**④ 商业 WPF 控件全部出局，且其中两个是「能力陷阱」：**

| 产品 | 关键事实（官网/EULA 原文） |
|---|---|
| Syncfusion WPF PDF Viewer | 能力最全（虚拟化、搜索、批注、表单、真脱敏、FDF/XFDF、已支持 .NET 10），但**商业闭源**；社区许可门槛（营收 <100 万美元、≤5 开发者、≤10 员工、外部资本 ≤300 万美元）且须先注册 |
| Telerik RadPdfViewer | 注释**只支持 Widget/Link 可写，Highlight/FreeText/Ink 全部只读**——正打在批注能力上；概览页未列文本搜索；加密仅到 RC4/AES-128 |
| Patagames Pdf.Wpf | 仓库 Apache-2.0 但 SDK 商业 EULA，且 OEM 条款**明文禁止用于「本身即 PDF 查看/编辑应用」的产品**——本项目正好命中，路直接封死 |
| ComPDFKit / Spire / DevExpress / Telerik | 商业闭源；ComPDFKit 正式许可**绑设备 ID**；DevExpress 的 PDF Document API 单卖 $1,199.99 且是库不是控件 |

**⑤ 两个可直接落地的低风险动作（本轮新增建议）：**

- **升级 PDFiumCore（同 owner、同 API 形态、Apache-2.0）**：**已实测执行，见第 6 节**。结论是钉 `155.0.8057`——156.0.8076 的**托管程序集**被本机 SAC 拦（3 次稳定复现），155 及以下全部可用，且 155 的全量回归与 152 基线完全一致（6 失败 / 1497 通过，失败集合相同）。
  ⚠️ **更正既有记录**：被拦的是托管 `PDFiumCore.dll`，**不是**原生 `pdfium.dll`（原生 152/154/156 用 `LoadLibraryW` 直测均可加载）；且 `154.0.8035` 现在已可用——SAC 按哈希/云信誉随时间累积放行。
- **KillerPdf.Engine 只做写入引擎评估，不碰渲染**：它明确「不渲染、不提取文本」（渲染由 PDFium、文本由 PdfPig 在库外完成），因此复用它**不影响**现有渲染栈与 `PdfiumRenderService`。

**⑥ 一个对「整包 fork KillerPDF」不利的新事实**：其渲染后端是 **Docnet.Core 2.6.0**，而 Docnet 内置的 PDFium 停留在 5445（2022 年），且该项目 2023-09 后停更。KillerPDF 的渲染层因此比本项目现用的 PDFiumCore 152 **落后数年**——整包 fork 会在渲染/安全补丁维度**倒退**。

---

### 4.6 同类先例复核（第三轮调研）

**① 对现架构最有力的外部印证**：Windows 上新一代开源 PDF 阅读器**几乎一致选择「PDFium 渲染 + 独立文档引擎 + 纯托管文本层」**，而不是 WebView2/pdf.js：

| 项目 | 星标 | 许可 | 栈 |
|---|---:|---|---|
| KillerPDF | 4,074 | GPL-3.0 | WPF + Docnet(PDFium) + PdfPig + KillerPdf.Engine |
| [CalyPdf/Caly](https://github.com/CalyPdf/Caly) | 230 | MIT | Avalonia + SkiaSharp + PdfPig + 纯 C# 渲染器 |
| [DanialJaved/rune](https://github.com/DanialJaved/rune) | 16 | GPL-3.0 | WinUI 3 + PDFium |
| [wieslawsoltes/PdfSpace](https://github.com/wieslawsoltes/PdfSpace) | 7 | MIT | Uno Platform + SkiaSharp + PdfPig |
| [PDF4QT](https://github.com/PDF4QT/PDF4QT) | 1,498 | **MIT**（2025-04-27 由 LGPLv3 重许可） | C++/Qt 引擎 + Viewer/Editor/Diff/CLI |

→ 本项目「PDFium 渲染 + 自研阅读器 + Python 文档引擎」不是偏门做法，而是**与主流一致**的取舍。差异只在「独立文档引擎」的形态：别人是进程内 .NET 库，本项目是 stdin/stdout 子进程（更轻、跨端共享，代价是每次调用付进程启动与文档重开）。

**② 「阅读器 + 工具箱」先例，均不可复用代码：**

| 项目 | 星标 / 许可 | 结论 |
|---|---:|---|
| [Stirling-PDF](https://github.com/Stirling-PDF/Stirling-PDF) | 93,949 | **open-core，不是纯 MIT**：根 LICENSE 的 MIT 明确排除 `app/proprietary/`、`app/saas/`、**`engine/`**、`frontend/editor/src/{proprietary,desktop,saas,…}`；`engine/LICENSE` 是需订阅的「Stirling PDF User License」，且其 Python 引擎依赖 **posthog（遥测）**。→ 架构可参考（「前端声明 + 后端 endpoint」与本项目 `catalog.py` + JSON 协议同构），**代码与依赖一律不可引入** |
| [PDFMathTranslate](https://github.com/PDFMathTranslate/PDFMathTranslate) / [BabelDOC](https://github.com/BabelDOC/BabelDOC) | 37,471 / 9,689，AGPL-3.0 | 定位是翻译引擎（库/CLI），不是阅读器；BabelDOC 依赖 **onnx/onnxruntime** → 与「不下载模型」硬约束冲突。可借鉴「引擎以库形态发布 + 多前端复用」，不可借依赖 |
| [Docspell](https://github.com/eikek/docspell) | 2,334，AGPL-3.0 | 服务端 `restserver` + 处理节点 `joex` 分离 —— 与本项目「外壳 + 独立引擎进程」**同构**，可作边界文档/ADR 引用；无桌面阅读器 |
| [Paperless-ngx](https://github.com/paperless-ngx/paperless-ngx) | 46,486，GPL-3.0 | DMS，无自带阅读内核、无批注；对本项目选型无直接输入 |

**③ 新增的可兼容内核候选（本轮补充）：**

| 组件 | 许可 | 状态 | 判断 |
|---|---|---|---|
| [mupdf.js](https://github.com/ArtifexSoftware/mupdf.js) | **AGPL-3.0** | 609★，2026-07-01 | 与 PyMuPDF 同源、语义一致、许可相容；但要在 WebView2 里跑 WASM，交互式滚动/虚拟化/写回全要自建 |
| [arklumpus/MuPDFCore](https://github.com/arklumpus/MuPDFCore) | **AGPL-3.0** | 152★，NuGet 2.0.1，2025-08-17 | 唯一许可相容的 C# MuPDF 绑定；维护偏慢，可作为「纯托管引擎」的备选参考 |
| CefSharp | BSD 风格 + CEF 条款 | 10,267★，活跃 | 仅作绕开 WebView2 的第二承载；PDF 能力仍受限于 Chromium 内置查看器 |

**④ 商业内核定价（佐证排除）**：Apryse WebViewer 入门 **$1,500** 起；PDF.js Express Professional **$595/月**（官方论坛已建议迁移到 WebViewer）；ComPDFKit SDK 商业且绑设备。全部与 AGPL-3.0-only 的源码可得要求冲突。

---

### 4.7 整包 fork 候选复核（第四轮调研）

对 B 类「完整阅读器」逐仓库核实了许可原文、UI 技术栈与批注写回能力。**结论：不存在值得整包 fork 的候选**，排除原因归为四类。

**① 语言/UI 栈不可跨越（不是「移植」而是「重写」或「并存第二个应用」）**

| 仓库 | 星标 | 许可（原文核实） | 栈 | 备注 |
|---|---:|---|---|---|
| [sumatrapdfreader/sumatrapdf](https://github.com/sumatrapdfreader/sumatrapdf) | 17,732 | (A)GPLv3 + 部分 BSD | C++ / Win32 | 能力最接近，含批注写回；UI 与渲染不可分离 |
| [KDE/okular](https://github.com/KDE/okular) | 1,535（镜像） | GPL-2.0-or-later（`LICENSES/` 另有 GPL-2.0-only.txt，需逐文件核对） | C++ / Qt6 + KF6 | Windows 走 Microsoft Store；KF6 依赖 KDE Craft 打包 |
| [xournalpp/xournalpp](https://github.com/xournalpp/xournalpp) | 15,495 | GPL-2.0-or-later | C++ / GTK+3 | 手写批注最强，但定位是笔记软件，阅读器能力不全 |
| [ahrm/sioyek](https://github.com/ahrm/sioyek) | 9,935 | GPL-3.0 | C++ / Qt6 + MuPDF | **最新 release 仍停在 v2.0.0（2022-12）**，只能跟 master |
| [kovidgoyal/calibre](https://github.com/kovidgoyal/calibre) | 26,116 | GPL-3.0 | Python + PyQt6 | viewer 目录无 PDF 渲染模块，很可能不原生支持 PDF |
| [ArtifexSoftware/mupdf](https://github.com/ArtifexSoftware/mupdf) | 2,992（镜像） | AGPL-3.0 | C | **渲染层已无增量价值**——PyMuPDF 就是它的绑定；应用层极简 |
| [koodo-reader/koodo-reader](https://github.com/koodo-reader/koodo-reader) | 28,464 | AGPL-3.0 | Electron | 有插件系统；批注写回无实现证据（倾向存应用数据库） |
| [readest/readest](https://github.com/readest/readest) | 25,036 | AGPL-3.0 | Tauri + pdf.js | PDF 走 pdf.js；写回未核实 |
| [edrlab/thorium-reader](https://github.com/edrlab/thorium-reader) | 2,898 | BSD-3-Clause | Electron | README 通篇 EPUB，全文未提 PDF |
| [zotero/zotero](https://github.com/zotero/zotero)（含 [zotero/reader](https://github.com/zotero/reader)） | 15,545 | 主体 AGPLv3；**reader 子仓库 NOASSERTION 且 LICENSE 为空** | XUL + React（pdf.js 分支） | **pdf.js 批注写回在真实产品中的落地范例**，可作参考实现；深度绑定 Zotero 数据模型 |
| [pwmt/zathura](https://github.com/pwmt/zathura) | 3,347 | Zlib | GTK4 | 无 Windows |
| [GNOME/evince](https://github.com/GNOME/evince) | 378（镜像） | GPL-2.0-or-later | GTK3 | 无 Windows 官方 |
| [koreader/koreader](https://github.com/koreader/koreader) | 30,217 | AGPL-3.0 | Lua + SDL | 无 Windows 官方（e-ink 取向） |

**② 平台不符**：Evince、Zathura、KOReader 均无 Windows 官方支持。

**③ 许可不可用**：
- [wmjordan/PDFPatcher](https://github.com/wmjordan/PDFPatcher)（12,720★，C#/WinForms）：`授权协议.txt` 是自定义「良心授权」——要求分发完整版本、**不得修改软件及安装程序**、商业附加需书面许可、销售收入须捐出不低于千分之一。**非 OSI 许可、限制修改与分发，与 AGPL-3.0-only 不兼容。**
- PDFgear（专有免费软件）、Foxit（EULA 第 6 条明确源码为商业秘密）：不可 fork、不可复用代码。
- Stirling-PDF：核心 MIT 但夹带目录级专有许可（须逐目录排除，见 4.6）。

**④ 能力面小于现状**：PdfiumViewer、MoonPdf、MuPDF viewer、Calibre viewer 只有渲染或查看；本项目缺的应用层能力（多标签、会话恢复、批注写回、加密保持、单实例+关联、翻译右栏）它们要么没有、要么要重做。

**⑤ 两处事实修正**：
- **MoonPdf 是 GPL-3.0，不是 MS-RL**（`reliak/moonpdf` + NuGet `MoonPdfLib` 均标 GPL-3.0）；未找到以 MS-RL 发布的 MoonPdf 仓库。它只是 WPF 显示控件，最后提交 2019。
- 新增一个 WPF 侧可参考的 PDFium 包装：**`MilosKonecny/PDFiumDotNET`**（MIT，23★，2024-08 停更）——许可干净但同样不含文本搜索/批注。

**⑥ 两个内核级事实（本轮最有价值的部分）**：
1. **pdf.js 的批注写回 = 增量更新 + 保留 Encrypt 字典**（Apache-2.0），见 4.3 的证据链。它是唯一「成熟 + 许可宽松 + 具备就地写回并保持加密语义」的现成内核；**但交付方式是下载字节流**，且批注类型只有 6 类。
2. **KillerPDF 证明「WPF + PDFium 渲染 + 自有 C# 增量写回引擎 + CLI」在 .NET 上可行**（其 `Writing/PdfIncrementalUpdateBuilder.cs` 即增量写回），架构上有 `Features/Viewer/IViewerHost.cs`、`Features/IShellServices.cs` 等模块边界——**可作自研架构参照，而非 fork 对象**。

---

## 5 推荐

| 方案 | 建议 | 理由 |
|---|---|---|
| **A. 维持现状**（PDFiumCore 自研阅读器 + Python 引擎） | **基准方案** | 已覆盖连续滚动/四面板/文本层/就地写回/多标签/打印/翻译右栏；**没有哪条中间路线能在「批注类型覆盖 + 写回原文件 + 无遥测 + 许可」四项同时超过它** |
| **B. 整包 fork KillerPDF** | **不做** | 替代的是已完成并验收的层，未替代的是最贵的耦合层；还要重写工具/翻译/设计体系并放弃 847 测试；其渲染后端 Docnet（PDFium 5445）比现用栈落后数年；OCR 语言包按需下载、更新检查默认联网 |
| **C. 整包 fork 其他阅读器**（SumatraPDF/pdf.js/koreader/readest 类） | **不做** | 技术栈与 WPF 外壳不兼容，等于放弃现有全部 WPF 资产 |
| **D. 商业 WPF 阅读控件**（Syncfusion/Telerik/DevExpress/Patagames/ComPDFKit/Spire/Apryse…） | **不做** | 闭源商业；Telerik 批注只读、Patagames EULA 明文禁止用于 PDF 查看器类产品；Apryse $1,500 起、PDF.js Express $595/月 |
| **E. WebView2 + pdf.js** | **暂不做** | 批注类型**反而变少**（无下划线/删除线/形状）；写回只能拿到下载字节流、批注权威副本落在浏览器内存；资产约 35 MB 且 `pdfjs-dist` 不含完整 viewer；Runtime 为微软专有且遥测不可控 |
| **F. WebView2 + mupdf.js(WASM)** | **暂不做** | 许可（AGPL-3.0）与语义同源是优点，但同样等于用 Web 重写渲染层，性能未实测 |
| **G. WebView2 内嵌 Edge 内置查看器** | **不可行** | 微软 2021 年移除 WebView2 的 PDF 批注并于 2023 年明确「no plans to re-add」；宿主只剩工具栏按钮隐藏；渲染已改由 Adobe Acrobat 驱动，是黑盒 |
| **H. CefSharp + pdf.js** | **不推荐** | E 的代价全有，另加自带 Chromium 的分发与更新负担 |
| **I. PDFiumCore 升级** | ✅ **已执行** → 钉 `155.0.8057` | 156 的托管程序集被 SAC 拦（3 次复现）、155 可用；全量回归与 152 基线完全一致（见第 6 节） |
| **J. KillerPdf.Engine 组件级 spike** | ✅ **已完成** → **不采纳替换** | 增量写回确实可用且保留原字节前缀，但现有加密文档（PyMuPDF 产物 `/Encrypt` 直接字典）会被引擎 Open 阶段拒绝；.NET 10 迁移 + FreeText 体积回退 + 无 Base-14 字体（见第 7 节） |
| **K. 参照 KillerPDF 边界，把文档引擎做成进程内库** | **记录为长期选项**（不自研启动） | 架构上最有价值的先例（ADR-001 的引擎边界值得借鉴），但工作量最大，且会与 macOS 端 Python 引擎分叉 |

### 下一步（若维护者同意）

0. ~~**升级 PDFiumCore**~~：✅ **已完成**（2026-10-11）——实测边界后钉 `155.0.8057`，全量回归与基线一致；详见第 6 节。
1. ~~**spike 1（组件）**~~：✅ **已完成**（2026-10-11）——`KillerPdf.Engine` 1.8.80 在隔离 .NET 10 下可编译可运行，T1 增量写回通过、T3 fail-closed 通过，但 **T2 在仓库现路径的加密产物上失败** → **不采纳替换**；详见第 7 节。
2. ~~**新增建议（由 spike 1 派生）**：单开一个小任务核实「现路径加密产物 trailer `/Encrypt` 为直接字典」这一偏差~~ → ✅ **已完成**（2026-10-11）：`/Encrypt` 偏差无需处理；但同一轮校验发现并修复了「加密 PDF 批注保存摧毁页面内容」的严重缺陷，见 [`加密批注保存缺陷-根因与修复.md`](加密批注保存缺陷-根因与修复.md)。
3. **spike 2（对标，可选）**：安装 KillerPDF 发行版，逐条对照本项目 21 条 FR，产出一份「它有什么我们没有 / 我们有什么它没有」的功能差表，作为后续 UI 打磨的输入（这一步只读、不引入依赖）。
4. **不做**：任何形式的整包 fork 或双 UI 体系。

---

## 6 实测结果：PDFiumCore 升级边界（2026-10-11，step 0 已执行）

**方法**：隔离验证工程 `tmp/spike-pdfium156/`（`net8.0` 控制台，`PDFiumCore` 版本以 MSBuild 属性参数化，合成样例由 PyMuPDF 生成，`tmp/` 不入库）。每次运行打印实际加载的 `pdfium.dll` 路径与 SHA256，并执行「初始化 → 加载 → 页数/页尺寸 → 96dpi 渲染非白像素统计 → FPDFText 字符数」全链路。

| 版本 | 托管 `PDFiumCore.dll` | 原生 `pdfium.dll` | 全链路 | 原生 SHA256[:16] |
|---|---|---|---|---|
| 152.0.7961（原钉） | ✅ 加载 | ✅ 加载 | **PASS** | D3D9F4B7C9DABE33 |
| 153.0.7999 | ✅ 加载 | ✅ 加载 | **PASS** | FB898A1F5ACE5780 |
| 154.0.8035 | ✅ 加载 | ✅ 加载 | **PASS** | CCFAC1AAD9E78624 |
| **155.0.8057（新钉）** | ✅ 加载 | ✅ 加载 | **PASS** | 55E7EBEF29A1EC95 |
| 156.0.8076 | ❌ **被 SAC 拦** | ✅ 可加载 | 失败（`FileLoadException` `0x800711C7`） | — |
| 157.0.8086 | 该版本不存在 | — | 编译失败 | — |

**关键更正（推翻本报告 4.5 与项目既有记录的表述）**：
1. **被 SAC 拦的是托管程序集 `PDFiumCore.dll`，不是原生 `pdfium.dll`。** 事件证据：`CodeIntegrity/Operational` 事件 3077 + 3033，进程 `PdfiumSpike.exe` 尝试加载 `PDFiumCore.dll` 被拒，Policy ID `{0283ac0f-fff1-49ae-ada1-8a933130cad6}`（与 `07-v3-验收记录.md` §9.4 同一策略）。原生 DLL 用 `LoadLibraryW` 直接测试，152/154/156 **全部加载成功**（handle 非 0、lastError 0）。
2. **`154.0.8035` 现在可用**，而项目旧注释记的是「154 原生被拦」。说明 SAC 的放行是**按文件哈希/云信誉随时间累积**的——同一版本隔一段时间后可能由拦转放。
3. **156 的拦截稳定复现**（3 次尝试均失败），不是概率性抖动。
4. **157.0.8086 在旧版本方案里不存在**：NuGet 将其解析到新版方案 `4270.0.0`，且新版 API 有变化（`FPDFBitmapFillRect` 的颜色参数由 `ulong` 变 `uint`）→ 直接跳到新版方案是**另一件事**，不在本次升级范围内。

**回归验证（全量 `dotnet test`，`net8.0-windows`，Debug）**：

| 状态 | 结果 | 失败集合 |
|---|---|---|
| 基线 `152.0.7961` | 失败 6 / 通过 1497 / 总计 1503（4m42s） | 5 项 FlaUI UI 冒烟（`冒烟2 启动…`、`冒烟11 导航/工具箱…`，需可交互桌面）+ 1 项 OCR（`OptimizerTests.Ocr_RealEngine_SyntheticText_ImageOnlyPdf`） |
| 应用 `155.0.8057` | **失败 6 / 通过 1497 / 总计 1503（4m03s）** | **与基线完全相同** |

产物侧确认：`AIPDF.Windows.Tests/bin/Debug/net8.0-windows/runtimes/win-x64/native/pdfium.dll` = 7,375,360 字节、SHA256[:16] `55E7EBEF29A1EC95`（即 155 原生库）。**无新增失败，全部失败项均为既有环境性失败。**

**已落地的改动（隔离、可回退）**：`Sources/AIPDF.Windows/AIPDF.Windows.csproj` 单文件 14 行——`PDFiumCore` 由 `152.0.7961` 改为 `155.0.8057`，并把实测边界写进注释。`git diff` 确认该文件是本次唯一改动，仓库其余修改均为既有在飞工作。

**证据文件**（`tmp/` 不入库）：`tmp/spike-pdfium156/`（工程 + `sample.pdf` + `gen_sample.py`）、`test-152.log`、`test-155.log`。

**未核实**：156 何时（或能否）获得云信誉不可预测；新版方案（`4270.0.0+`）的 API 差异范围未评估；本次仅验证 Windows x64 路径。

---

## 7 实测结果：KillerPdf.Engine spike（2026-10-11，step 1 已执行）

**方法**：隔离工程 `tmp/spike-killerpdf/`（.NET 10 SDK 隔离安装到 `tmp/dotnet10/`，NuGet 缓存亦隔离；`KillerPdf.Engine` 1.8.80，`GPL-3.0-only`；全部样例由 PyMuPDF 合成）。**结论：技术可行性成立，但不能替换现路径。**

### 7.1 环境与许可

| 项 | 值 |
|---|---|
| 隔离 SDK | .NET **10.0.401**（运行时 10.0.12）→ `tmp/dotnet10/`，未改动机器上的 .NET 8.0.424，未设持久化环境变量 |
| 包 | `KillerPdf.Engine` **1.8.80**，`lib/net10.0`，唯一依赖 `System.Security.Cryptography.Pkcs 9.0.10` |
| 许可 | `GPL-3.0-only`（nuspec license expression）；与 AGPL-3.0-only 组合需补 `NOTICE` / `THIRD_PARTY_NOTICES.md` / 完整许可文本，GPL 部分不可升版 |
| SAC | 本次**未触发拦截**；但 `KillerPdf.Engine.dll` 为 `NotSigned`，同机 `PDFiumCore.dll` 156 被拦过 → 用户机上不可控 |

### 7.2 T1 增量写回 —— ✅ 通过（这是 Python 路径做不到的实质增益）

| 断言 | 实测 |
|---|---|
| 新文件以原文件字节为前缀 | ✅ `commonPrefixLen = 4311` = 原文件大小（逐字节比较）；二次增量同样完整保留（40138/40138） |
| PyMuPDF 读到注释 | ✅ 2 条（`Highlight` + `FreeText`） |
| 体积 | 4311 → 40138 B（+35827）——**仅 Highlight 只 +1180 B**；+34.6 KB 全部来自 FreeText 嵌入的 TTF 子集（`WUPKBE+ArialMT`），且**每次新增 FreeText 都重嵌一份子集** |

### 7.3 T2 加密保持 —— ⚠️ 分情况（**首要阻碍**）

| 输入 | trailer `/Encrypt` | 结果 |
|---|---|---|
| KillerPdf 自产 AES-256 | 间接 `6 0 R` | ✅ 通过（`/V5 /R6 /P-4` 指纹保持，注释字符串可被 PyMuPDF 正常读出） |
| pypdf AES-256 / AES-128 / RC4-128 | 间接 | ✅ 通过（需先把 `/P 4294967292` 等长改为 `-4`） |
| **PyMuPDF AES-256（= 仓库现路径产物形态）** | **直接 `<<…>>`** | ❌ **`InvalidOperationException: The trailer /Encrypt value is not indirect.`（Open 阶段即拒绝）** |
| pypdf 原样（`/P 4294967292`） | 间接 | ❌ `OverflowException`（非受控 fail-closed） |

**根因已用「等长字节补丁」隔离**（`/P 4294967292` → `/P -4        `，10 字符→10 字符、xref 偏移不变）：补丁后三种算法全部由失败转成功。

**本报告的独立复核确认**（Lead 亲自重测，严格定位 trailer 而非子串误命中）：
```
PyMuPDF AES-256 (1392 B): trailer … /Encrypt<</Filter/Standard/R 6/V 5/…   => DIRECT dict
pypdf   AES-256 (1581 B): trailer … /Encrypt 8 0 R …                       => INDIRECT ref
```

### 7.4 T3 失败路径 —— ✅ 全部 fail-closed，原文件 sha256 不变

错误密码 → `PdfPasswordAuthenticationException`；加密未给密码 → Build 阶段 `InvalidOperationException`；49 B 垃圾 → `PdfSyntaxException`；空文件 → `FormatException`；只读目标 → `UnauthorizedAccessException`（应用层）；无改动 → `InvalidOperationException: The incremental annotation update is empty.`

### 7.5 与现有 Python 路径的正面对比（同源同内容：1 Highlight + 1 FreeText）

| 指标 | Python 路径（原子就地重写） | KillerPdf 增量 |
|---|---|---|
| 产物大小（源 4311 B） | 5477（+1166） | 40138（+35827） |
| **保留原字节前缀** | ❌ 共同前缀仅 **404** | ✅ **4311/4311** |
| 加密源（PyMuPDF AES-256） | ✅ 3512 B，仍加密、同密码可开 | ❌ **Open 即拒绝** |
| 加密源（KillerPdf 自产 AES-256） | ✅ 4089 B，`/V5/R6/P-4` 保持 | ✅ 39101 B，同指纹保持 |
| 注释被 PyMuPDF 读到 | ✅ 2 条 | ✅ 2 条 |
| 耗时（热，含进程启动） | ~396–412 ms | ~349–365 ms |

### 7.6 结论

**不采纳 KillerPdf.Engine 替换现路径。** 采纳前必须先解决的前置条件（按严重度）：

1. **现有加密文档会全部打不开**：仓库 `_atomic_save_in_place` 走 PyMuPDF 显式 AES-256 重加密，产物是直接字典 → 引擎 Open 阶段直接拒绝。
2. **解析器健壮性**：`/P 4294967292`（pypdf 默认无符号写法）抛 `OverflowException`，不是受控错误类型。
3. **.NET 10 全量迁移**（SDK/runtime/MSBuild/TFM）。
4. **体积回退**：FreeText 每次 +21~55 KB 字体子集。
5. **无 Base-14 字体**：FreeText 必须调用方提供 TTF（现路径用 `helv`/`china-s` 无需字体文件）。
6. SAC 不可控 + GPL 许可/NOTICE 流程。

**但 spike 暴露了一条独立于 KillerPdf 的真实风险 → 已单开任务核实并闭环（见 [`加密批注保存缺陷-根因与修复.md`](加密批注保存缺陷-根因与修复.md)）**：
> 复核结论分两层。① `/Encrypt` 直接字典：**无需处理**——qpdf、PDFium、pypdf、pdfminer 均正常读取，唯一拒绝者是 KillerPdf.Engine（已不采纳），记录为已知偏差即可。② **同一轮校验挖出一个严重缺陷**：给**加密** PDF 保存批注会摧毁页面内容（3 页实测：第 2/3 页渲染全白、第 1 页仅剩笔记文字），而就地保存会覆盖源文件。根因是 `_atomic_save_in_place` 在**认证后**读取 `doc.needs_pass`，触发 PyMuPDF 1.28.2 的 AES 状态缺陷。**已修复**（改用认证前快照），真实引擎 CLI 产物 `qpdf --check` 由 EXIT=2 转为 **EXIT=0**，并补了内容完整性回归断言。

**可记录的参照价值**：增量写回（保留原字节前缀）语义值得作为自研写回优化的参照。**若将来要增量语义，先验证 PyMuPDF 自身的 `incremental=True`（零新依赖）**——但它与「临时文件 → `os.replace`」的原子性互斥，需重新权衡（**该假设本 spike 未验证**）。

**证据**：`tmp/spike-killerpdf/REPORT.md` + `logs/`（原始命令输出）+ `work/`（合成 PDF 与产物）+ `probe/`（C# 探针）。仓库受版本控制文件零改动。

**未核实**：真实用户加密文档中 `/Encrypt` 为直接字典的比例（无样本，硬约束禁止读个人文档）；Acrobat/Word/Chrome 导出的加密 PDF；ISO 32000-1 原始条款（付费标准，仅二手来源确认惯例）；KillerPdf 的 AES-128/RC4 写回；渲染像素级外观；并发/大文件/线性化/签名共存。

---

## 8 未能核实 / 需进一步确认的事实

- KillerPDF 的**实际运行质量**（渲染性能、批注写回在其他阅读器中的兼容性、48 个 open issues 的严重度）未实测，本报告只核实了仓库元数据、README 与引擎文档。
- **`KillerPdf.Engine` 能否在 .NET 8 上编译**未实测：只确认其 csproj 为 `net10.0` + `PackageLicenseExpression=GPL-3.0-only`；「拿 GPL 源码降级到 net8.0」的代价是估计而非实测。
- **没有任何候选在本机做过编译或运行验证**（含 SAC 放行、原生库加载、DPI、airspace）——全部为静态阅读推断。
- SumatraPDF / Okular / Xournal++ / Sioyek / Calibre / Evince / Zathura / KOReader / Thorium / Readest / Zotero 的**许可原文与 UI 栈已逐仓库核实**（见 4.7），但以下细节仍未核实：**KillerPDF 的会话恢复**（未找到实现，仅有 `RecentFiles`/`Shell/TempReload.cs`）、**KillerPDF 加密文档保存后是否保持加密**（其引擎有 `PdfIncrementalUpdateBuilder.cs`，但未读实现）、**各候选全文搜索含 CJK 的实测表现**（仅按文本提取后端推断）。
- **KillerPDF 仓库内未找到独立的 THIRD-PARTY/NOTICE 文件**（`THIRD-PARTY` 仅命中 `release.ps1` 注释与 `.gitattributes`），其发布物是否随附 PDFium(BSD-3)/Tesseract(Apache-2.0) 声明未核实。
- **SumatraPDF 的 AI Chat / 划词翻译是否默认关闭**未核实（仅确认两个源文件存在于 master）；其单实例行为仅由 Changelog 间接推断。
- **Koodo Reader / Readest / Thorium / Sioyek 是否把批注写回 PDF** 未核实（Koodo 无实现证据；Thorium README 完全未提 PDF）；**Calibre 是否原生打开 PDF** 未取得明确否定声明。
- **Okular 中具体哪些文件是 GPL-2.0-only** 未逐文件定位（`LICENSES/` 含该文本），影响逐文件合规判断。
- **镜像仓库的 stars 不代表热度**：Okular(1,535)、Evince(378)、MuPDF(2,992) 均为镜像，官方开发在 invent.kde.org / gitlab.gnome.org / git.ghostscript.com。
- **WebView2 Runtime 自身的诊断/遥测行为及关闭方式**未找到官方文档——这与本项目「不得引入遥测」的硬约束存在**未决冲突**，若将来走 WebView2 路线须单独评估。
- **mupdf.js 在 WebView2 中的实际性能**（滚动帧率、大文档内存、与原生 PDFium 的差距）未做任何实测。
- **pdf.js 历史版本是否曾支持创建下划线/删除线**未回溯；第三方扩展 `Laomai-codefee/pdfjs-annotation-extension`（声称支持 UNDERLINE/STRIKEOUT/SQUARE/CIRCLE/POLYLINE）的许可与维护状态未核实。
- **Edge 浏览器本体把墨迹/高亮写回 PDF 文件的确切语义**未找到微软一手说明；**WebView2 内置查看器能否被宿主显式禁用/替换**未见已实现 API。
- **KillerPDF 的实现质量**未审阅实现代码（只读 README、csproj、目录树、engine/README 与 ADR-001）；**.NET 8 与 .NET 10 在 WPF API 上的差异范围**未核实。
- **Telerik 是否内置文本搜索**、**Syncfusion 是否支持多文档同窗**：官方概览页未列出，不能断言「无」。
- **Spire 免费版页数限制**仅有搜索摘要，官网被 Cloudflare 拦截（403），未取得一手确认。
- **Foxit / Nutrient(PSPDFKit) 的 .NET/WPF 支持细节**未打开官方许可页核实，故未进入主判断。
- **Stirling-PDF 各专有子目录的具体条款**只核实了根 LICENSE 分区声明与 `engine/LICENSE`，未逐个读；CefSharp 的 CEF 二进制分发条款未核实。
- KillerPDF 的 GPLv3 与本项目 AGPL-3.0-only 的组合分发**未做法务确认**，本报告依据 AGPLv3 §13 原文（已逐字核实）的兼容性判断。
- 星标/时间戳为 2026-10-11 快照。

---

## 附：检索原始证据

- GitHub Search API 结果（10 组查询的紧凑表）与关键仓库元数据来自 `api.github.com` 直查，2026-10-11。
- KillerPDF 仓库元数据：`api.github.com/repos/SteveTheKiller/KillerPDF`（4,074★ / GPL-3.0 / 324 forks / 48 open issues）。
- KillerPDF 引擎文档：`KillerPDF/engine/README.md`（能力清单、测试门、许可、依赖边界）。
- pdf.js 批注写回：`pdfDocument.saveDocument()` 产出标准注释对象，来源见 4.3 链接。
