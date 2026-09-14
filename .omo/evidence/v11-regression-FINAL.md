# v1.1 最终 dist 与全量回归证据（FINAL）

> 生成：2026-09-14 23:42（本机时区）
> 仓库 HEAD：`6f6c291`（`fix(reader): P1 修复（重试状态/中途重解析/凭据哈希/localhost/端点校验…）`，`6f6c29196538eea3437c42eb35cc586aef9cc043`）
> 修复提交链：R1 `23bc684` → R2a `b423119` → R2b `6f6c291`；构建修复 `9c00fed`（dist 打包 `backend/translate`）
> 范围：**仅构建与验证**，未改动任何源码。`dist/` 受 `.gitignore` 保护，未提交。
> 本报告是 v1.1 最终验收证据，取代此前 `v11-regression-HEAD.md`（`3831fde` 时期）、`v11-t24-regression.md` 与 `final-dist-build.txt`（旧 dist 缺 translate 的阻塞记录）。

---

## 0. 执行摘要

| 项 | 结果 |
|---|---|
| dist 重建（`scripts/build.ps1`） | **EXIT_CODE=0**，7/7 阶段；无 Smart App Control（0x800711C7 / WinError 4551）阻塞，无需重试 |
| dist 含 `backend/translate` | **通过**：12 个 `.py`，与源码 **逐字节一致**（sha256 12/12） |
| dist catalog | **通过**：engine / app / source 均 33 个工具，含 `translate_batch`；源与 dist sha 一致 |
| app DLL 翻译类 | **通过**：`TranslationBatchPage`、`TranslationConsentService`、`CloudConsentDialog` 等 6/6 命中 |
| dist 运行时冒烟 | **通过**：`import translate` 及全部子模块 OK；`translate_text` 经引擎返回「请先配置翻译服务端点。」（与源码引擎一致） |
| Python 翻译套件（C1） | **Ran 312 tests — OK**（基线 ≥312） |
| Python 引擎套件（C2） | Ran 45 tests：2 errors（LibreOffice 超时 + OCR WinError 4551 环境）+ 2 skipped |
| 回填 spike · 收缩（C3a） | **VERDICT: PASS**（溢出 0/10 = 0.0%、裁切 0、ToUnicode 9/9 = 100%） |
| 回填 spike · 扩写（C3b） | **FAIL（诚实）**：ratio 1.0 → 溢出 60%；ratio 1.5 → 溢出 90%；裁切 0、ToUnicode 100% —— **已知限制**，非静默 |
| WPF 测试（C4） | 总计 1099 / 通过 1098 / 失败 1（`Ocr_RealEngine_SyntheticText_ImageOnlyPdf`，OCR 4551 环境）；翻译过滤 233/233；sidecar 过滤 13/13 |
| 源码/仓库卫生 | 无源码修改；`git status` 仅既有未跟踪目录 `设计/v1.1-翻译/`；`dist/` 被忽略 |

---

## 1. Step A：dist 重建

### 1.1 命令与环境

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT=1
$env:PYTHONIOENCODING='utf-8'
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 2>&1 | Tee-Object .omo\evidence\final-dist-build2.log
```

- 结果：**EXIT_CODE=0**（7/7 阶段全部完成）。
- Smart App Control：本次构建**未出现** 0x800711C7 / WinError 4551，未触发重试路径。
- 原始日志：`.omo/evidence/final-dist-build2.log`（UTF-16LE，95 行，14,236 bytes）；尾部：`.omo/evidence/final-dist-build2.txt`（UTF-8）。
- 既有噪声（非失败）：pip「新版本可用」通知；PS 5.1 对子进程写 stderr 的 `NativeCommandError` 包装；`OcrPage.xaml.cs(23,9) CS4014` 既存 warning（与 22:03 版构建相同）。

### 1.2 阶段摘要（日志逐条核对）

| 阶段 | 结果 |
|---|---|
| [1/7] 前置检查 | Python 3.14.2；dotnet SDK 8.0.424 |
| [2/7] 依赖 | 锁定依赖已满足；playwright chromium 就绪（幂等，无下载） |
| [3/7] catalog | `backend/catalog.py` 运行成功（`PYTHONUTF8=1`；源快照 sha 未变） |
| [4/7] OCR helper | Release / win-x64 发布成功 |
| [5/7] WPF + dist | 自包含发布成功；dist 组装（含 `backend/translate` 拷贝） |
| [6/7] 校验 | 布局断言 8/8；dist 引擎冒烟 `health ok=true（engine=1.28.2）`；快离子集 `Ran 6 tests OK`（2.221s） |
| [7/7] 自包含运行时 | 嵌入式 Python 3.14.2 + 离线 wheel 安装；自包含断言 7/7；`health ok=true vision=True`；检测到本机 LibreOffice |

### 1.3 产物新鲜度（证明为本次重建，非旧物）

| 产物 | mtime | 大小 |
|---|---|---|
| `dist/aipdf-windows` | 2026-09-14 **23:29:56** | — |
| `dist/aipdf-windows/app/AIPDF.Windows.dll` | 2026-09-14 **23:29:53** | 1,068,032 bytes |
| （对照）旧 dist DLL（22:03 构建） | 2026-09-14 22:03:06 | 1,061,376 bytes |

> DLL 体积变化与 R2b reader 修复一致；新构建晚于全部修复提交。

---

## 2. Step B：dist 验证（翻译是否真的进入发行目录）

### 2.1 B1 — `engine/backend/translate/` 存在且完整 ✅

`dist\aipdf-windows\engine\backend\translate\` 内容：

| 文件 | bytes |
|---|---|
| `bridge.py` | 5,240 |
| `chunk.py` | 13,331 |
| `detect.py` | 13,986 |
| `extract.py` | 17,773 |
| `geometry.py` | 3,765 |
| `glossary.py` | 8,266 |
| `llm.py` | 20,147 |
| `output.py` | 23,206 |
| `prompt.py` | 10,443 |
| `reflow.py` | 27,470 |
| `verify.py` | 16,527 |
| `__init__.py` | 179 |

- `Test-Path ...\translate\__init__.py` → **True**。
- **sha256 逐字节校验：源 `backend/translate/*.py` 12/12 与 dist 完全一致，mismatched=[]**。
- dist 内无 `translate\__pycache__`（构建脚本已清理，L287）。
- 对照旧构建（`.omo/evidence/final-dist-build.txt` §3.5）：当时该目录**不存在**，dist 引擎 `translate_text` 返回通用错误。本阻塞项已消除。

### 2.2 B2 — catalog：33 工具含 `translate_batch` ✅

```
engine dist: count=33, translate ids=['translate_batch']
app   dist: count=33
source    : count=33
sha256(source backend/catalog.json)[:16]   = ffbdbcf1d402f8c4
sha256(dist engine catalog.json)[:16]      = ffbdbcf1d402f8c4   # 与源一致
```

### 2.3 B3 — app DLL 翻译类探测 ✅

对 `dist/aipdf-windows/app/AIPDF.Windows.dll` 原始字节探测：

| 字符串 | 命中 |
|---|---|
| `TranslationBatchPage` | True |
| `TranslationConsentService` | True |
| `CloudConsentDialog` | True |
| `TranslationSidebar` | True |
| `TranslationConfigViewModel` | True |
| `AIPDF.Windows.Views.Translation` | True |

（对照：09-12 旧 DLL 全部 0 命中；22:03 版与本次均为全命中。）

### 2.4 B4 — dist 运行时冒烟 ✅

以 **dist 内嵌 Python** 执行：

```powershell
# ① 包导入（._pth 的 ..\backend 生效，解析到 dist 布局内的包）
dist\aipdf-windows\engine\python\python.exe -c "import translate; import translate.bridge, translate.geometry, translate.detect, translate.chunk, translate.llm, translate.prompt, translate.glossary, translate.verify, translate.reflow, translate.output"
→ import translate OK: D:\...\dist\aipdf-windows\engine\backend\translate\__init__.py
→ all submodules OK

# ② 经过引擎 dispatch 的真实路径（与 C# 安装态一致）
'{"operation":"translate_text","options":{"text":"hello world","targetLang":"zh"}}' | dist\...\engine\python\python.exe dist\...\engine\backend\engine.py
→ {"ok": false, "error": "请先配置翻译服务端点。"}   exit=1

# ③ 源码引擎同请求（一致性对照）
'{"operation":"translate_text",...}' | .venv\Scripts\python.exe backend\engine.py
→ {"ok": false, "error": "请先配置翻译服务端点。"}   exit=1
```

- dist 与源码引擎**响应完全一致**：翻译分支已可导入并由引擎正常执行到「未配置端点」的配置校验。
- 旧构建的同请求返回通用「处理引擎发生错误。」（ImportError 被兜底捕获）；本结果为修复后的预期签名（旧报告 §4.1 预先给出的复验标准）。

---

## 3. Step C：全量测试验证

### 3.1 C1 — Python 翻译套件 ✅

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests -p "test_translate_*.py"
→ Ran 312 tests in 15.487s
→ OK   (EXIT=0)
```

- 覆盖 12 个文件：`test_translate_{bridge,chunk,detection,extract,geometry,glossary,llm,orchestrator,output,prompt,reflow,verify}.py`。
- 与 R2a 提交信息一致（312 全绿；R2a 前基线 293）。
- 原始输出：`.omo/evidence/final-python-translate.txt`。

### 3.2 C2 — Python 引擎套件 ⚠️（环境错误，已归因）

```powershell
.venv\Scripts\python.exe tests\test_engine.py
→ Ran 45 tests in 308.570s
→ FAILED (errors=2, skipped=2)   (EXIT=1)
```

| # | 测试 | 错误 | 归因（诚实） |
|---|---|---|---|
| 1 | `test_office_imports_and_pdfa` | `UserError: 本地转换超时`（soffice `D:\Program Files (x86)\program\soffice.exe` 300s 超时） | **本机 LibreOffice 环境**：已知问题；v3-task12 / v3-task11 基线同为该错误（当时第 2 个错误为 `tearDownClass`） |
| 2 | `test_vision_ocr_scan` | `OSError: [WinError 4551] 应用程序控制策略已阻止此文件`（引擎 `subprocess.Popen` 拉起 helper） | **本会话 SAC/应用控制策略抖动**：该 4551 在 closure 证据中已有波动时间线（T7 拦 / T6+T8 过 / T9/T10 拦 / T15/T16 过） |

重试与归因探针：

```
重试 test_vision_ocr_scan（隔离运行）        → FAILED (errors=1)（0.5s，仍 4551）
Sources\...\publish\WindowsOcrHelper.exe --selftest → {"ok":true,...} exit=0
dist\aipdf-windows\helper\WindowsOcrHelper.exe --selftest → {"ok":true,...} exit=0
```

- 即：**helper 二进制本身可运行**（源码树与 dist 的 `--selftest` 均通过），被策略拦截的是本会话中由测试宿主 `Popen` 发起的拉起路径；这不是源码回归（t24 会话 OCR 曾通过，本机策略按会话/宿主波动）。
- skipped=2：`test_cancel_stops_children`（Mac-only `/usr/bin/pgrep`）、`test_web_url`（Web helper 未构建，Windows 侧预期）。
- 原始输出：`.omo/evidence/final-engine-test.txt`。

### 3.3 C3 — 回填门禁 spike

**a) 收缩方向（默认 EN→ZH）→ VERDICT: PASS** ✅

```powershell
.venv\Scripts\python.exe tests\spike_translate_reflow.py
→ VERDICT: PASS
  溢出页 0/10 = 0.0%（≤10%）
  最大裁切块/页 0（≤2）
  ToUnicode 覆盖 9/9 = 100.0%（=100%）
  机制自检：S3 clip 计数=1 / S2 overflow 计数=1
```

报告（本次重生成，含新时间戳）：`.omo/evidence/v11-spike-report.md`；原始输出：`.omo/evidence/final-spike-shrink.txt`。

**b) 扩写方向（等长 / ×1.5）→ 诚实 FAIL** ⚠️

```powershell
.venv\Scripts\python.exe tests\spike_translate_reflow_expansion.py
→ ratio=1.00: 溢出 6/10 = 60%（阈值 ≤10%），最大裁切块 0，ToUnicode 100%
→ ratio=1.50: 溢出 9/10 = 90%（阈值 ≤10%），最大裁切块 0，ToUnicode 100%
→ 端到端写出的原因: font_shrink, formula_fail, overflow_margin, raster_overlay, rotation_crop, table_fail, unsupported_script
```

报告（本次重生成，证据提交钉住 `6f6c291`）：`.omo/evidence/v11-spike-expansion.md`；原始输出：`.omo/evidence/final-spike-expansion.txt`。

> **判定口径**：收缩 PASS 不代表扩写安全。扩写方向 60–90% 溢出为 **KNOWN LIMITATION（已知限制）**，本次不修（任务约束：只构建与验证）。产品仍不建议在中→英／英→德等扩写语言对默认开启 PDF 回填；应单独定阈并单独放行。

**c) 非静默核对（扩写 FAIL 不是静默失败）**

- 已可见：单文件结果区逐页呈现 `overflow_margin` / `clip`（`Sources/AIPDF.Windows/ViewModels/Translation/FullTranslationViewModel.cs:120-142`；渲染 `Views/Translation/FullTranslationPanel.xaml:300-348`）；回填优先页边溢出再裁切并逐页标记（`backend/translate/reflow.py:406-431`）；降级报告 sidecar 落盘（`backend/engine.py:940-941`）。
- 仍存在的可见性缺口（HEAD 复核，未随 R1/R2 消除）：批量页仅显示降级**数量**（`Sources/AIPDF.Windows/ViewModels/TranslationBatchViewModel.cs:126`「N 页降级」，无逐页原因/溢出占比）；引擎结果无顶层聚合 `overflow` 字段（消费方须自行遍历 `report.pages[].reasons`）。

### 3.4 C4 — WPF 全量测试 ⚠️（1 个环境失败）

```powershell
& "C:\Users\shige\AppData\Local\Microsoft\dotnet\dotnet.exe" test Sources\AIPDF.Windows\AIPDF.Windows.Tests\AIPDF.Windows.Tests.csproj
→ 失败! - 失败: 1，通过: 1098，已跳过: 0，总计: 1099，持续 27 s   (EXIT=1)
```

| 项 | 结果 |
|---|---|
| 全量 | **1099 总 / 1098 通过 / 1 失败**（较 22:21 基线的 1060 增加 39 项，来自 R1/R2 新测试） |
| 唯一失败 | `OptimizerTests.Ocr_RealEngine_SyntheticText_ImageOnlyPdf` —— 真实 OCR 引擎用例；与 §3.2 同一会话的 helper 拉起被 4551 拦截同源（测试宿主经引擎拉起 helper，引擎只透出通用错误）。**归因：环境（SAC/应用控制策略），非翻译/非源码回归。** |
| 翻译过滤 | `--filter "FullyQualifiedName~Translation"` → **失败 0，通过 233，总计 233**（22:21 基线为 199） |
| sidecar 过滤 | `--filter "FullyQualifiedName~SelectionSidecarStore"` → **失败 0，通过 13，总计 13**（22:21 时该套件有 1 项失败，现已转绿） |

- **Smart App Control 未阻塞 `dotnet test` 本身**（t24 时期曾被 0x800711C7 归零执行；本次正常执行 1099 项）。
- 原始输出：`.omo/evidence/final-dotnet-test.txt`。

---

## 4. R1 / R2 修复记录与证据

### 4.1 R1 `23bc684` —— P0（F1 / F17 / F21 / F27）

| 发现 | 修复内容 | 证据（file:line / test） |
|---|---|---|
| **F17** 旋转页竖排误判 | `/Rotate 90/270` 交换内容坐标轴使 `line["dir"]` 不可靠；仅在 rotation ∈ {0,180} 时判竖排，避免正常横排文本整块不翻译 | `backend/translate/detect.py`；`tests/test_translate_detection.py:68,193,347` |
| **F1** overlap 重组错位 | `_translate_blocks` 改为产出与请求对齐的译文（重叠段用空串占位），`reassemble_chunks` 对齐契约显式化 | `backend/translate/chunk.py:62,316`；`tests/test_translate_chunk.py` |
| **F27** stderr 密钥泄漏 | EngineBridge 从请求 options 取 `apiKey/password`，异常诊断（含 Bearer / URL 编码形态）进入消息与 Diagnostics 前脱敏 | `Sources/AIPDF.Windows/Bridge/EngineBridge.cs`；`EngineBridgeTests.cs:96` |
| **F21** 撤销同意不停止在途任务 | `TranslationConsentService` 增加同意代次；整篇/批量经 `ConsentRevocationWatcher` 监听并取消在途任务 | `Services/ConsentRevocationWatcher.cs`；`FullTranslationViewModel.cs:476`；`TranslationBatchViewModel.cs:773`；两侧 VM 测试 |

### 4.2 R2a `b423119` —— 引擎 P1（Murphy 审查 `bg_09b1c263`）

修复：**F2 / F5 / F6 / F7 / F10 / F11 / F12 / F13 / F14 / F16 / F18 / F19 / F20**，补测试缺口 **F33 / F34 / F35 / F38 / F39 / F40**。

| 发现 | 修复内容（提交信息逐条） |
|---|---|
| F2 | `chunk.py:62,316` 记录 `split_indices`；`engine.py:907` 按页上报 `segment_split` |
| F5 | `extract.py:160` bbox 回环守卫，不一致则 `rect_normalize` 回退 |
| F6 | `llm.py:104` 移除 `cancel_event` 死机制，取消 = 进程终止 + socket 超时（`DEFAULT_TIMEOUT=30`） |
| F7 / F10 | `llm.py:186,382` 成本预检；prompt / completion 分开估算 |
| F11 / F12 | `reflow.py:489,620` 无译文/不支持块移出溢出障碍集；`reflow.py:509` 可搜索性抽检限定 placement 矩形 |
| F13 / F14 | `output.py:90` 页脚带已有正文则跳过标记；`output.py:362` 空白页保留 CropBox 偏移 |
| F16 | `engine.py:1138` 划词 RTL 检测与 `unsupported_script` 上报 |
| F18 / F19 / F20 | `detect.py:233,261,287` 表格须有网格线佐证、`has_grid_lines` 按页缓存、亚阈值裁剪说明 |
| 测试 | 新增/更新 19 项；translate 套件 312 全绿（基线 293）；shrink / expansion spike 无回归 |

### 4.3 R2b `6f6c291` —— reader P1

修复：**重试状态 / 中途重解析 / 凭据哈希 / localhost / 端点校验**（13 个文件，+544/−31；含 `TranslationProviderConfig.cs`、`SessionStore.cs`、`SelectionSidecarStore.cs` 原子替换、批量/整篇 VM 与测试）。

### 4.4 构建修复 `9c00fed` —— dist 打包翻译包

`scripts/build.ps1`：新增 `backend\translate` 递归拷贝（L286）并清理其 `__pycache__`（L287）。**本报告 §2 即该修复的端到端验收**。

---

## 5. 已知限制（HEAD 未消除，诚实披露）

1. **扩写方向回填溢出 60%/90%**（比例 1.0/1.5）远超设计 ≤10% 门禁 —— 已知限制；非静默（逐页 reasons + sidecar），但存在聚合告警缺失与批量页只显示数量的可见性缺口（§3.3c）。
2. **OCR helper 拉起被应用控制策略拦截（WinError 4551）** —— 本会话环境问题；导致 C2 的 `test_vision_ocr_scan` 与 C4 的 `Ocr_RealEngine_SyntheticText_ImageOnlyPdf` 各 1 个失败；helper `--selftest`（源码树 + dist）均通过，证明二进制可用、非源码回归。
3. **LibreOffice 转换超时（300s）** —— 本机环境（`D:\Program Files (x86)\program\soffice.exe`）；C2 `test_office_imports_and_pdfa` 失败；属已知环境项。
4. **批量页降级可见性**：仅数量（`「N 页降级」`），无逐页原因/溢出占比；引擎结果无顶层机器可读 `overflow` 聚合字段。
5. 测试跳过项：Mac-only `pgrep` 用例、Windows 未构建 Web helper 的 URL 用例（预期跳过）。
6. `公式块合并` 启发式的保守边界（主样例公式被合并后 `formula_fail` 可能不触发，专项样例可触发）—— 沿用 `v11-spike-expansion.md` 的记录。

---

## 6. 证据文件清单

| 文件 | 内容 |
|---|---|
| `.omo/evidence/final-dist-build2.log` | Step A 完整构建日志（UTF-16LE，95 行） |
| `.omo/evidence/final-dist-build2.txt` | 构建日志尾部 + EXIT_CODE=0 观测（UTF-8） |
| `.omo/evidence/final-python-translate.txt` | C1 原始输出（312 OK） |
| `.omo/evidence/final-engine-test.txt` | C2 原始输出（45 tests / 2 env errors / 2 skipped） |
| `.omo/evidence/final-spike-shrink.txt` | C3a 原始输出（PASS） |
| `.omo/evidence/final-spike-expansion.txt` | C3b 原始输出（FAIL，60%/90%） |
| `.omo/evidence/final-dotnet-test.txt` | C4 原始输出（1099 / 1098 / 1） |
| `.omo/evidence/v11-spike-report.md` | 收缩 spike 报告（本次 23:37 重生成） |
| `.omo/evidence/v11-spike-expansion.md` | 扩写 spike 报告（本次 23:38 重生成，钉住 `6f6c291`） |
| 本文件 | `v11-regression-FINAL.md` |

> 注：`.omo/` 被 `.gitignore` 忽略；这些证据文件经 `git add -f` 显式路径提交（提交信息 `test: v1.1 最终 dist 与全量回归证据`）。

## 7. 结论

- **发行目录阻塞已解除**：本次重建的 `dist/aipdf-windows` 内含完整 `backend/translate`（12 文件、逐字节一致），33 工具 catalog 与翻译 DLL 类型齐备，dist 引擎冒烟与源码引擎响应一致 —— v1.1 翻译在 Windows 发行布局下具备可运行性（缺端点/密钥时的配置错误为预期行为）。
- **回归结论**：Python 翻译套件 312/312；WPF 全量 1098/1099（唯一失败为 OCR 环境 4551）；翻译过滤 233/233；收缩回填门禁 PASS。
- **诚实边界**：扩写方向回填门禁 FAIL（60%/90%），为已知产品限制，未修复、未静默；两个环境类失败（LibreOffice、OCR 4551）已逐项归因，helper 二进制自检通过。
- **卫生**：未修改源码；`dist/` 未提交；工作树仅剩既有未跟踪 `设计/v1.1-翻译/`。
