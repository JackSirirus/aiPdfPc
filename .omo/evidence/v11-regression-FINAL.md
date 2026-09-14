# v1.1 最终 dist 与全量回归证据（FINAL）

> 生成：2026-09-15 00:50（本机时区，本会话全部实测）
> 仓库 HEAD：`ffe02ff`（`fix(engine): 修复 source_protection 断链/分片丢失/引擎 https/溢出聚合`，`ffe02ffca6b915c88e02d6200efa8f956918285a`）
> 修复提交链：R1 `23bc684` → R2a `b423119` → R2b `6f6c291` → `43e41d2`（reader 二次修复）→ R5 `ffe02ff`（对抗性复审 `bg_4c564457`／审查轮 R4 的遗留缺陷，修复编号 R5-1…R5-11）；dist 打包修复 `9c00fed`
> 范围：**仅构建与验证**，未改动任何源码（`git status` 无源码变更）。`dist/` 受 `.gitignore` 保护，未提交。
> 本报告取代上一版（`6f6c291` 时期、引用 `final-dist-build2.*` 的版本）；`v11-regression-HEAD.md`（`3831fde`）、`v11-t24-regression.md` 为更早历史证据。

---

## 0. 执行摘要

| 项 | 结果 |
|---|---|
| dist 重建（`scripts/build.ps1`，第 3 次） | **EXIT_CODE=0**，7/7 阶段，墙钟 73.2s；**无 Smart App Control（0x800711C7 / WinError 4551）阻塞，允许的 1 次重试未使用** |
| dist 含 `backend/translate` | **通过**：12 个 `.py` 与源码 **逐字节一致**（sha256 12/12 MATCH），含 `detect.py`；无 `__pycache__` |
| dist catalog | **通过**：engine-dist / app-dist / source 均 33 个工具（root 为数组），均含 `translate_batch`，三者 sha256 全同（`ffbdbcf1d402f8c4…`） |
| dist 引擎代码与源一致 | **通过**：`engine.py`（23a21082…）、`catalog.py`（5a034a81…）sha256 源/ dist 一致 |
| app DLL 翻译类 | **通过**：`TranslationBatchPage`、`TranslationConsentService`、`CloudConsentDialog`、`TranslationSidebar`、`TranslationConfigViewModel`、`AIPDF.Windows.Views.Translation` 6/6 命中 |
| dist 运行时冒烟 | **通过**：`import translate` + 10 个子模块 OK；`translate_text` 经 dist 引擎返回「请先配置翻译服务端点。」（**byte-exact，dist 与源码引擎逐字节相同**；非 ImportError） |
| Python 翻译套件（C1） | **Ran 324 tests — OK**（45.759s；R5 前基线 312，+12） |
| Python 引擎套件（C2） | Ran 45 tests：2 errors（LibreOffice 超时 + OCR WinError 4551 环境）+ 2 skipped（311.526s） |
| 回填 spike · 收缩（C3a） | **VERDICT: PASS**（溢出 0/10 = 0.0%、裁切 0、ToUnicode 9/9 = 100%） |
| 回填 spike · 扩写（C3b） | **FAIL（诚实）**：ratio 1.0 → 溢出 60%；ratio 1.5 → 溢出 90%；裁切 0、ToUnicode 100% —— **已知限制**，非静默（见 §5.1） |
| WPF 测试（C4） | 总计 **1120** / 通过 **1119** / 失败 1（`Ocr_RealEngine_SyntheticText_ImageOnlyPdf`，OCR 4551 环境）；翻译过滤 **253/253**；sidecar 过滤 **13/13** |
| 源码/仓库卫生 | 无源码修改；`git status` 仅既有未跟踪目录 `设计/v1.1-翻译/`；`dist/` 被忽略 |

---

## 1. Step A：dist 重建

### 1.1 命令与结果

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT=1
$env:PYTHONIOENCODING='utf-8'
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 2>&1 | Tee-Object .omo\evidence\final-dist-build3.log
```

- 结果：**EXIT_CODE=0**（7/7 阶段全部完成；墙钟 73.2s）。
- Smart App Control：本次**未出现** 0x800711C7 / WinError 4551 阻塞；任务允许的「命中则重试一次」路径未触发。
- 原始日志：`.omo/evidence/final-dist-build3.log`（UTF-16LE，95 行，14,236 bytes；与 build2 体积相同系巧合，sha256 不同）；尾部：`.omo/evidence/final-dist-build3.txt`（UTF-8）。
- 既有噪声（非失败）：pip「新版本可用」通知；PS 5.1 对子进程写 stderr 的 `NativeCommandError` 包装；`OcrPage.xaml.cs(23,9) CS4014` 既存 warning。

### 1.2 阶段摘要（日志逐条核对）

| 阶段 | 结果 |
|---|---|
| [1/7] 前置检查 | Python 3.14.2；dotnet SDK 8.0.424 |
| [2/7] 依赖 | 锁定依赖已满足；playwright chromium 就绪（幂等，无下载） |
| [3/7] catalog | `backend/catalog.py` 运行成功（`PYTHONUTF8=1`；与仓库快照一致，§2.2） |
| [4/7] OCR helper | Release / win-x64 发布成功 |
| [5/7] WPF + dist | 自包含发布成功；dist 组装（含 `backend/translate` 递归拷贝 + `__pycache__` 清理） |
| [6/7] 校验 | 布局断言 8/8；dist 引擎冒烟 `health ok=true（engine=1.28.2）`；快离子集 `Ran 6 tests OK`（2.816s） |
| [7/7] 自包含运行时 | 嵌入式 Python 3.14.2 + 离线 wheel 安装；自包含断言 7/7；`health ok=true vision=True`；检测到本机 LibreOffice |

### 1.3 产物新鲜度（证明为本次重建，非旧物）

| 产物 | mtime | 大小 / sha256 |
|---|---|---|
| `dist/aipdf-windows` | 2026-09-15 **00:31:49** | — |
| `dist/.../app/AIPDF.Windows.dll` | 2026-09-15 **00:31:45** | 1,075,200 bytes（上一版 `6f6c291` 构建为 1,068,032；+7,168 与 `43e41d2` reader 修复一致） |
| `dist/.../app/AIPDF.Windows.exe` | 2026-09-15 **00:31:45** | 152,064 bytes |
| `dist/.../engine/backend/engine.py` | 2026-09-15 00:23:52（拷贝保留源 mtime） | sha256 `23a210822d6c9fdd…` **= 源** |
| `dist/.../engine/backend/catalog.py` | — | sha256 `5a034a81f5df16a1…` **= 源** |

> 构建晚于全部修复提交（HEAD `ffe02ff`，提交于 00:28:09）；dist 内 `engine.py` 内容即 `ffe02ff` 版本。

---

## 2. Step B：dist 验证（翻译是否真的进入发行目录）

> 原始输出：`.omo/evidence/final-dist-verify3.txt`（B1/B2/B3/B4 全量记录）。

### 2.1 B1 — `engine/backend/translate/` 存在且完整 ✅

`dist\aipdf-windows\engine\backend\translate\` 共 12 个 `.py`，与源 `backend/translate/*.py` **逐字节一致（12/12 MATCH）**：

| 文件 | bytes | sha256[:16] |
|---|---|---|
| `__init__.py` | 179 | 3253e7a5ea38122d |
| `bridge.py` | 5,240 | dce4cf8f9d74850a |
| `chunk.py` | 14,838 | ee6702186a3b1318 |
| `detect.py` | 13,986 | 438a44cf9627ddf4 |
| `extract.py` | 17,773 | 03c30e056afb055b |
| `geometry.py` | 3,765 | d7b1af1713e47f8f |
| `glossary.py` | 8,266 | d14af51cffdcb425 |
| `llm.py` | 20,147 | fae0a97d29886aca |
| `output.py` | 24,077 | 89b307f38031413d |
| `prompt.py` | 10,443 | 8d121be4860cd3cb |
| `reflow.py` | 27,847 | 6dafd59c9c484ff4 |
| `verify.py` | 16,527 | 02a4be4a3d0587a9 |

- `detect.py` 在位（R1 F17 旋转页竖排误判修复所在模块）。
- `mismatched = []`；dist 内无 `translate\__pycache__`。
- 对照旧构建（`final-dist-build.txt` §3.5）：当时该目录不存在、`translate_text` 返回通用错误 —— 该阻塞已由 `9c00fed` 解除，本次再次确认。

### 2.2 B2 — catalog：33 工具含 `translate_batch` ✅

```
engine-dist count=33 translate_ids=['translate_batch'] sha256=ffbdbcf1d402f8c4
app-dist    count=33 translate_ids=['translate_batch'] sha256=ffbdbcf1d402f8c4
source      count=33 translate_ids=['translate_batch'] sha256=ffbdbcf1d402f8c4
```

（catalog root 为工具数组；33 个 id 与仓库快照逐一相同，含翻译批量入口 `translate_batch`。）

### 2.3 B3 — app DLL 翻译类探测 ✅

对 `dist/aipdf-windows/app/AIPDF.Windows.dll` 原始字节探测（.NET #Strings 堆为 UTF-8）：

| 字符串 | 命中 |
|---|---|
| `TranslationBatchPage` | True |
| `TranslationConsentService` | True |
| `CloudConsentDialog` | True |
| `TranslationSidebar` | True |
| `TranslationConfigViewModel` | True |
| `AIPDF.Windows.Views.Translation` | True |

### 2.4 B4 — dist 运行时冒烟 ✅

以 **dist 内嵌 Python** 执行，并与源码引擎做逐字节对照：

```text
B4a  import translate OK: dist\aipdf-windows\engine\backend\translate\__init__.py   exit=0
B4b  import translate.{bridge,geometry,detect,chunk,llm,prompt,glossary,verify,reflow,output} → all submodules OK   exit=0
B4c  dist  引擎 translate_text → rc=1，stdout 逐字节 = {"ok": false, "error": "请先配置翻译服务端点。"}\r\n
B4d  source 引擎 同请求       → rc=1，与 dist 输出 byte-identical（dist == source = True）
```

- 两种路径均**非 ImportError**：翻译分支已可导入并由引擎正常执行到「未配置端点」的配置校验；stderr 为空（`stderr_len=0`）。
- 该结果为 `9c00fed` + 后续引擎修复后的预期签名（旧报告预先给出的复验标准）。

---

## 3. Step C：全量测试验证

### 3.1 C1 — Python 翻译套件 ✅

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests -p "test_translate_*.py"
→ Ran 324 tests in 45.759s
→ OK   (EXIT=0)
```

- 覆盖 12 个文件：`test_translate_{bridge,chunk,detection,extract,geometry,glossary,llm,orchestrator,output,prompt,reflow,verify}.py`。
- 基线变化：`6f6c291` 时期 312 → 本轮 **324**（+12，来自 R5 的新测试：orchestrator +202 行、chunk +25、output +11、reflow +11）。
- 原始输出：`.omo/evidence/final-python-translate.txt`。

### 3.2 C2 — Python 引擎套件 ⚠️（环境错误，已归因）

```powershell
.venv\Scripts\python.exe tests\test_engine.py
→ Ran 45 tests in 311.526s
→ FAILED (errors=2, skipped=2)   (EXIT=1)
```

| # | 测试 | 错误 | 归因（诚实） |
|---|---|---|---|
| 1 | `test_office_imports_and_pdfa` | `UserError: 本地转换超时`（`D:\Program Files (x86)\program\soffice.exe` 300s 超时） | **本机 LibreOffice 环境**：沿用 v3-task12 / v3-task11 / 上一版 FINAL 的同一已知错误 |
| 2 | `test_vision_ocr_scan` | `OSError: [WinError 4551] 应用程序控制策略已阻止此文件`（引擎 `subprocess.Popen` 拉起 helper） | **本会话 SAC/应用控制策略抖动**：与上一版同源；closure 证据中已有该 4551 的波动时间线（T7 拦 / T6+T8 过 / T9/T10 拦 / T15+T16 过） |

环境探针（本次重跑，已追加进原始输出）：

```text
Sources\...\win-x64\publish\WindowsOcrHelper.exe --selftest → {"ok":true,"language":"zh-Hans-CN","text":"AIPDF TEST",...} exit=0
dist\aipdf-windows\helper\WindowsOcrHelper.exe --selftest  → {"ok":true,"language":"zh-Hans-CN","text":"AIPDF TEST",...} exit=0
```

- 即：**helper 二进制本身可运行**（源码树与 dist 均通过 `--selftest`），被策略拦截的是由测试宿主 `Popen` 发起的拉起路径；不是源码回归。
- skipped=2：`test_cancel_stops_children`（Mac-only `/usr/bin/pgrep`）、`test_web_url`（Web helper 未构建，Windows 侧预期）。
- 原始输出：`.omo/evidence/final-engine-test.txt`。

### 3.3 C3 — 回填门禁 spike

**a) 收缩方向（默认 EN→ZH）→ VERDICT: PASS** ✅

```text
溢出页 0/10 = 0.0%（≤10%）
最大裁切块/页 0（≤2）
ToUnicode 覆盖 9/9 = 100.0%（=100%）
灵敏度（非门禁）：ratio=1.0 溢出 60% / ratio=1.5 溢出 90%
机制自检：S3 clip 计数=1 / S2 overflow 计数=1
```

- 报告本次重生成（00:35:23，耗时 73.78s）：`.omo/evidence/v11-spike-report.md`；原始输出：`.omo/evidence/final-spike-shrink.txt`（EXIT=0）。

**b) 扩写方向（等长 / ×1.5）→ 诚实 FAIL** ⚠️

```text
ratio=1.00: 溢出 6/10 = 60%（阈值 ≤10%），最大裁切块 0，ToUnicode 100%
ratio=1.50: 溢出 9/10 = 90%（阈值 ≤10%），最大裁切块 0，ToUnicode 100%
端到端触发的原因: font_shrink, formula_fail, overflow_margin, raster_overlay, rotation_crop, table_fail, unsupported_script
```

- 报告本次重生成并**钉住 `ffe02ff`**（00:35:23，耗时 64.35s）：`.omo/evidence/v11-spike-expansion.md`；原始输出：`.omo/evidence/final-spike-expansion.txt`（EXIT=0）。
- **判定口径**：收缩 PASS 不代表扩写安全。扩写 60–90% 溢出为 **KNOWN LIMITATION（已知限制）**，本次不修（任务约束：只构建与验证）；但该失败**非静默**（§5.1）。

### 3.4 C4 — WPF 全量测试 ⚠️（1 个环境失败）

```powershell
& "C:\Users\shige\AppData\Local\Microsoft\dotnet\dotnet.exe" test Sources\AIPDF.Windows\AIPDF.Windows.Tests\AIPDF.Windows.Tests.csproj
→ 失败! - 失败: 1，通过: 1119，已跳过: 0，总计: 1120，持续 27 s   (EXIT=1)
```

| 项 | 结果 |
|---|---|
| 全量 | **1120 总 / 1119 通过 / 1 失败**（较上一版 `6f6c291` 的 1099 增加 21 项，全部来自 `43e41d2` 的 reader 测试；R5 为纯 Python 改动，C# 测试数不变） |
| 唯一失败 | `OptimizerTests.Ocr_RealEngine_SyntheticText_ImageOnlyPdf` —— 与 §3.2 同源（测试宿主经引擎拉起 helper 被 4551 拦截）。**归因：环境（SAC/应用控制策略），非翻译/非源码回归。** |
| 翻译过滤 | `--filter "FullyQualifiedName~Translation"` → **失败 0，通过 253，总计 253**（上一版 233；+20 来自 `43e41d2`） |
| sidecar 过滤 | `--filter "FullyQualifiedName~SelectionSidecarStore"` → **失败 0，通过 13，总计 13** |

- **Smart App Control 未阻塞 `dotnet test` 本身**（t24 时期曾被 0x800711C7 归零执行；本次正常执行 1120 项）。
- 原始输出：`.omo/evidence/final-dotnet-test.txt`、`.omo/evidence/final-dotnet-translation-filter.txt`、`.omo/evidence/final-dotnet-sidecar-filter.txt`。

---

## 4. 修复记录与证据（R1 / R2 / `43e41d2` / R5）

### 4.1 R1 `23bc684` —— P0（F1 / F17 / F21 / F27）

| 发现 | 修复内容 | 证据（file:line / test） |
|---|---|---|
| **F17** 旋转页竖排误判 | `/Rotate 90/270` 交换内容坐标轴使 `line["dir"]` 不可靠；仅在 rotation ∈ {0,180} 时判竖排 | `backend/translate/detect.py`；`tests/test_translate_detection.py:68,193,347` |
| **F1** overlap 重组错位 | `_translate_blocks` 产出与请求对齐的译文（重叠段空串占位），`reassemble_chunks` 对齐契约显式化 | `backend/translate/chunk.py`；`tests/test_translate_chunk.py` |
| **F27** stderr 密钥泄漏 | EngineBridge 从请求 options 取 `apiKey/password`，异常诊断（含 Bearer / URL 编码形态）进入消息与 Diagnostics 前脱敏 | `Sources/AIPDF.Windows/Bridge/EngineBridge.cs`；`EngineBridgeTests.cs` |
| **F21** 撤销同意不停止在途任务 | `TranslationConsentService` 同意代次；整篇/批量经 `ConsentRevocationWatcher` 监听并取消在途任务 | `Services/ConsentRevocationWatcher.cs`；`FullTranslationViewModel.cs`；`TranslationBatchViewModel.cs`；两侧 VM 测试 |

### 4.2 R2a `b423119` —— 引擎 P1（Murphy 审查 `bg_09b1c263`）

F2 / F5 / F6 / F7 / F10 / F11 / F12 / F13 / F14 / F16 / F18 / F19 / F20 + 补测试缺口 F33 / F34 / F35 / F38 / F39 / F40。要点：`segment_split` 分片上报（`chunk.py` → `engine.py`）、bbox 回环守卫（`extract.py`）、取消=进程终止（`llm.py`）、成本预检（`llm.py`）、溢出障碍集/可搜索性抽检限定（`reflow.py`）、页脚标记与 CropBox 保留（`output.py`）、划词 RTL / `unsupported_script`（`engine.py`）、表格网格线佐证与按页缓存（`detect.py`）。translate 套件 293 → 312。

### 4.3 R2b `6f6c291` —— reader P1

重试状态 / 中途重解析 / 凭据哈希 / localhost / 端点校验（13 个文件，+544/−31）。上一版 FINAL 已完整验收。

### 4.4 `43e41d2` —— reader 二次修复（本版新增验收）

`fix(reader): 修复路径假阳性/片段密钥/诊断脱敏/localhost 缓存/批量原因显示`（13 个文件，+516/−43）：

| 项 | 内容 | 本版证据 |
|---|---|---|
| 路径假阳性 / 端点校验 | `TranslationProviderConfig.cs` 等新增测试与校验（+108 行） | 翻译过滤 253/253（§3.4） |
| 片段密钥 | `FullTranslation.cs`（+74）等 | 同上 |
| 诊断脱敏 | `EngineBridge.cs`（+20）、`SettingsStore.cs` | `EngineBridgeTests`、`SettingsStoreTests` 全绿 |
| localhost 缓存 | `SettingsTranslationRuntimeConfigProvider.cs` | 同上 |
| 批量原因显示 | `TranslationBatchViewModel.cs`（+59）：批量结果「N 页降级（原因标签…）＋ N 页超出原文范围 ＋ N 页被裁切」 | 本版 §5.4 复核；253 过滤含批量 VM 测试 |
| 测试 | C# 测试 1099 → **1120**（+21），翻译过滤 233 → **253**（+20） | §3.4 |

### 4.5 R5 `ffe02ff` —— 对抗性复审（R4，`bg_4c564457`）遗留缺陷修复

| 编号 | 修复内容 | 本版核验位置 |
|---|---|---|
| **R5-1** source_protection 断链 | `_report_payload`（`backend/engine.py:948`）统一 sidecar ↔ `results[].report`；`translate_request` 结果携带 `sourceProtection` 与 `overflow`（`engine.py:1120-1124`）；`output.write_outputs` 前置披露（`output.py:498+`），仅导出 Markdown 时也携带 | 源码核对通过 |
| **R5-2** 分片丢失 | `reassemble_chunks` 同一下标多片按源序合并（`chunk.py:217,234,263`）；`engine._translate_blocks` 对切分块按 `core_segments` 逐片发送（`engine.py:922-928`）；`output._append_text`（`output.py:196,236,297`）与 `reflow._store_translation` 由覆盖改拼接（第二道防线） | 源码核对通过 |
| **R5-3** 引擎 https | 新增 `_require_secure_endpoint`（`engine.py:1036`）：非 loopback 拒绝明文 http；整篇与划词两处调用（`engine.py:1068,1162`）；本机 loopback 仍允许 http | 源码核对通过；`tests/test_translate_orchestrator.py` 覆盖 |
| **R5-11** 溢出聚合 | 每文件 `results[]` 增加 `overflow={pages, clipPages, warn}`，由 `report.pages[].reasons` 的 `{overflow_margin, clip}` 派生（`engine.py:962,1124`） | 源码核对通过；本版 B4 冒烟同源路径可执行 |
| 测试 | 新增 R5-1 受限文档端到端、R5-2 超长段落多片全落盘、R5-3 http 拒绝 / https 与 loopback 放行、R5-11 告警；chunk/output/reflow 同下标拼接单测。**翻译套件 312 → 324 全绿**；收缩 spike PASS | `.omo/evidence/final-python-translate.txt`（324 OK）、`.omo/evidence/final-spike-shrink.txt` |

---

## 5. 已知限制（HEAD `ffe02ff` 复核，诚实披露）

1. **扩写方向回填溢出 60%/90%**（比例 1.0/1.5）远超设计 ≤10% 门禁 —— 已知限制，未修复。**非静默**：单文件结果区逐页呈现原因标签（`FullTranslationViewModel.cs:120-142` 中文 ReasonLabels；`FullTranslationPanel.xaml:300+` 逐页原因与译文）；批量页显示「N 页降级（原因标签）＋ N 页超出原文范围 ＋ N 页被裁切」（`TranslationBatchViewModel.cs:147-160`，本版复核）；引擎每文件输出 `results[].overflow={pages,clipPages,warn}`（R5-11），C# 侧已消费（`FullTranslation.cs:326-331`）。**残留缺口**：批量视图为按文件聚合、无逐页映射与溢出占比；`warn` 是提示字段，不自动阻断导出。
2. **OCR helper 拉起被应用控制策略拦截（WinError 4551）** —— 本会话环境抖动；导致 C2 的 `test_vision_ocr_scan` 与 C4 的 `Ocr_RealEngine_SyntheticText_ImageOnlyPdf` 各 1 个失败；helper `--selftest`（源码树 + dist）本次均通过，证明二进制可用、非源码回归。
3. **LibreOffice 转换超时（300s）** —— 本机环境（`D:\Program Files (x86)\program\soffice.exe`）；C2 `test_office_imports_and_pdfa` 失败；属已知环境项。
4. **批量降级可见性（已改善，仍有边界）** —— `43e41d2` 后批量页展示原因标签与溢出/裁切页数（不再是只有「N 页降级」）；但仍是文件级聚合，无逐页明细与占比。
5. 测试跳过项：Mac-only `pgrep` 用例、Windows 未构建 Web helper 的 URL 用例（预期跳过）。
6. `公式块合并` 启发式的保守边界（主样例公式被合并后 `formula_fail` 可能不触发，专项样例可触发）—— 沿用 `v11-spike-expansion.md` 记录。

---

## 6. 证据文件清单（本次全部重生成）

| 文件 | 内容 |
|---|---|
| `.omo/evidence/final-dist-build3.log` / `.txt` | Step A 构建日志（UTF-16LE 14,236B / UTF-8 尾部，EXIT_CODE=0） |
| `.omo/evidence/final-dist-verify3.txt` | Step B B1–B4 全量记录（hash/catalog/DLL/冒烟+逐字节对照） |
| `.omo/evidence/final-python-translate.txt` | C1 原始输出（324 OK） |
| `.omo/evidence/final-engine-test.txt` | C2 原始输出（45 tests / 2 env errors / 2 skipped + helper selftest） |
| `.omo/evidence/final-spike-shrink.txt` | C3a 原始输出（PASS；00:35 重跑） |
| `.omo/evidence/final-spike-expansion.txt` | C3b 原始输出（FAIL，60%/90%；00:35 重跑） |
| `.omo/evidence/final-dotnet-test.txt` | C4 全量原始输出（1120 / 1119 / 1） |
| `.omo/evidence/final-dotnet-translation-filter.txt` | 翻译过滤（253/253，EXIT=0） |
| `.omo/evidence/final-dotnet-sidecar-filter.txt` | sidecar 过滤（13/13，EXIT=0） |
| `.omo/evidence/v11-spike-report.md` | 收缩 spike 报告（00:35:23 重生成） |
| `.omo/evidence/v11-spike-expansion.md` | 扩写 spike 报告（00:35:23 重生成，钉住 `ffe02ff`） |
| 本文件 | `v11-regression-FINAL.md`（HEAD `ffe02ff` 终版） |

> 注：`.omo/` 中未跟踪路径被 `.gitignore` 忽略；新证据文件经 `git add -f` 显式路径提交（提交信息 `test: v1.1 dist 重建与终局回归证据`），未提交 `dist/` 与任何源码。

## 7. 结论

- **发行目录验证通过**：本版重建的 `dist/aipdf-windows` 内含完整 `backend/translate`（12 文件、与 HEAD 源码逐字节一致），33 工具 catalog（engine / app / source 三者同 sha）与翻译 DLL 类型齐备；dist 引擎 `translate_text` 与源码引擎响应逐字节相同（配置错误为预期签名），`import translate` 全链路可用。
- **回归结论**：Python 翻译套件 **324/324**；Python 引擎套件 45 项仅 2 个环境错误（LibreOffice、OCR 4551）+2 预期跳过；WPF 全量 **1119/1120**（唯一失败为 OCR 环境 4551）；翻译过滤 **253/253**；sidecar 过滤 **13/13**；收缩回填门禁 **PASS**。
- **诚实边界**：扩写方向回填门禁 FAIL（60%/90%），为已知产品限制，未修复、且已具备逐页原因 + 文件级溢出聚合的非静默披露（§5.1）；两个环境类失败已逐项归因（helper 二进制自检通过、LibreOffice 本机超时），不代表源码回归。
- **卫生**：仅构建与验证，未修改源码；`dist/` 未提交；工作树仅剩既有未跟踪 `设计/v1.1-翻译/`。
