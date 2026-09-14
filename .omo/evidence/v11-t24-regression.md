# v1.1 翻译 · 端到端回归报告（T24）

> 日期：2026-09-14
> 任务：T24 端到端回归 + 设计对齐审计（实现阶段最后一个任务；供 F1–F4 最终验收波输入）
> 设计基线：`设计/v1.1-翻译/需求总结.md`（21 条决策 + §实现分期与验收门）
> 计划基线：`.omo/plans/pdf-translation-v1.1.md`
> 范围：只读回归与审计；**本任务不修代码**。
> 结果汇总：Python 全量 377 用例（6 个环境错误 / 2 个平台跳过）；spike **PASS**；**dotnet test 因环境被 0 执行**；发现 1 个阻断级实现缺口（生产未接线运行时配置）与若干局部缺口。

---

## 0. 执行摘要

| 项 | 结论 |
|---|---|
| Python 全量回归 | **Ran 377 tests, errors=6, skipped=2**（6 个错误全部为环境/平台类，非 v1.1 代码缺陷） |
| Python 翻译子集 | **Ran 264 tests, OK**（11 个 `test_translate_*.py`，含引擎端到端编排） |
| `dotnet test`（WPF） | **0 用例执行**：被本机 Smart App Control 拦截（`0x800711C7`），非代码失败 |
| 回填 spike | **VERDICT: PASS**（溢出 0/10、裁切 0、ToUnicode 9/9=100%），T20 允许并入 mono/dual |
| 21 条设计决策 | 14 条完全实现；7 条部分实现（#4/#9/#10/#12/#13/#15/#18）；无整条未实现 |
| 分期交付 | V1.1a PASS；V1.1c 回填已并入；V1.1d 批量完整；**V1.1b 云端同意链路未接线** |
| 阻断级缺口 | 生产入口使用 `NullTranslationRuntimeConfigProvider`，翻译在 UI 中恒返回「尚未配置」 |
| 源文件只读 | 通过（编排测试断言源 PDF SHA/内容不变） |

---

## 1. 环境与方法

- 平台：Windows（win32），Python 3.14（`C:\Users\shige\AppData\Local\Python\pythoncore-3.14-64`），.NET SDK 8.0.424，LibreOffice `D:\Program Files (x86)\program\soffice.exe`。
- 代码状态：`main`，HEAD `6b3ed7b`；仅 `设计/v1.1-翻译/` 未跟踪；`backend/catalog.json` 重跑后无差异（`git status` 干净）。
- 合成样例：全部由测试内存合成（PyMuPDF）；未读取任何真实或个人文档。
- 证据文件（本次运行产生）：
  - `.omo/evidence/t24-python-suite-err.txt`（全量结果行）、`t24-python-suite-out.txt`
  - `.omo/evidence/t24-translate-subset.txt`
  - `.omo/evidence/t24-dotnet-test.txt` / `t24-dotnet-test-retry.txt`
  - `.omo/evidence/t24-spike-rerun.txt`、`v11-spike-report.md`（spike 重写）
- 说明：spike 脚本运行会按设计重写 `.omo/evidence/v11-spike-report.md`（时间戳更新，内容一致）。

---

## 2. 测试结果

### 2.1 Python 全量回归

命令（任务指定）：

```
.venv\Scripts\python.exe -m unittest discover -s tests -p "test_*.py"
```

结果：**Ran 377 tests in 1679.123s — FAILED (errors=6, skipped=2)**

> 耗时约 28 分钟，主因是 5 个 LibreOffice 转换用例各等待 300s 超时（见下）。

**6 个错误（全部为环境/平台类，非 v1.1 特性缺陷）：**

| # | 用例 | 根因 | 归类 |
|---|---|---|---|
| 1 | `test_engine.EngineTests.test_office_imports_and_pdfa` | `soffice.exe` 转换超时 300s | LibreOffice 环境（任务已知） |
| 2 | `unittest.loader._FailedTest.test_installer` | `ModuleNotFoundError: No module named 'fcntl'`（`scripts/install.py` 为 macOS 专用） | 平台不匹配（Windows 跑 macOS 安装测试） |
| 3 | `test_office_windows...test_real_docx_to_pdf` | soffice 300s 超时 | LibreOffice 环境 |
| 4 | `test_office_windows...test_real_chinese_path_docx_to_pdf` | soffice 300s 超时 | LibreOffice 环境 |
| 5 | `test_office_windows...test_real_xlsx_to_pdf` | soffice 300s 超时 | LibreOffice 环境 |
| 6 | `test_office_windows...test_real_pptx_to_pdf` | soffice 300s 超时 | LibreOffice 环境 |

**2 个跳过（平台门控，非失败）：**
- `test_engine.py:618` — `"Mac-only test: uses /usr/bin/pgrep"`（Windows 恒跳过）。
- `test_paths_windows.py::test_find_office_registry_dir_without_soffice_skipped`（无 soffice 注册表时的分支）。

> 判定：这些错误/跳过在 v1.1 翻译改动之前即存在（同机历史凭证一致，例如 `.omo/evidence/final-qa/T10-test_office_windows.txt`）。**不构成 v1.1 回归**；但本报告不把 377 记为「全绿」，如实记录 6 errors。

### 2.2 Python 翻译子集（v1.1 核心）

命令：`.venv\Scripts\python.exe -m unittest discover -s tests -p "test_translate_*.py"`
结果：**Ran 264 tests in 12.150s — OK**（exit 0）

覆盖 11 个文件：`test_translate_bridge`(19)、`chunk`(26)、`extract`(15)、`geometry`(13)、`glossary`(26)、`llm`(47)、`orchestrator`(7)、`output`(28)、`prompt`(20)、`reflow`(16)、`verify`(47)。

其中 `test_translate_orchestrator.py` 是**引擎级端到端**：`engine.dispatch({operation:"translate"})` → extract → chunk → prompt → LLM(mock) → verify → reflow → mono/dual，并断言：
- mono 含真实译文、原文被移除（非黑框）、dual 页数 = 2×原页（`test_end_to_end_mono_and_dual`）；
- 批量多文件（`test_batch_translates_multiple_files`）；
- 模型拒译 → 保留原文并标 `untranslated_echo`（`test_model_refusal_degrades_to_original`）;
- 密钥只出现在 `Authorization` 头，**不在请求正文 / `sys.argv` / `os.environ`**（`test_end_to_end_mono_and_dual` 末尾）；
- 源文件只读不变。

### 2.3 `dotnet test`（WPF）

命令（任务指定）：

```
& "C:\Users\shige\AppData\Local\Microsoft\dotnet\dotnet.exe" test Sources/AIPDF.Windows/AIPDF.Windows.Tests/AIPDF.Windows.Tests.csproj
```

结果：**0 用例执行**（退出码 0，具误导性）。原始报错：

```
[xUnit.net] Skipping: AIPDF.Windows.Tests (could not load dependent assembly 'AIPDF.Windows'):
Could not load file or assembly '...\AIPDF.Windows.Tests\bin\Debug\net8.0-windows\AIPDF.Windows.dll'.
应用程序控制策略已阻止此文件（0x800711C7）
→ AIPDF.Windows.Tests.dll 中没有可用测试。
```

**根因（环境回归，非代码）：** 本机 **Smart App Control 已启用且强制执行**——
`Win32_DeviceGuard.CodeIntegrityPolicyEnforcementStatus=2`、`UsermodeCodeIntegrityPolicyEnforcementStatus=2`、
注册表 `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy`：`VerifiedAndReputablePolicyState=1`、`SAC_EnforcementReason=1`。
未签名的测试依赖 `AIPDF.Windows.dll` 被 WDAC/SAC 拦截，故测试主机发现到 0 个用例。

**排除项验证：**
- 重跑 `dotnet test`：结果完全一致（排除瞬时/文件锁）。
- 两个程序集哈希相同（`DA27AB6E…`），排除「测试输出目录里是过期副本」。
- 应用主程序 `AIPDF.Windows.exe` 仍能启动（探测存活 ≥7s），SAC 未全面封禁应用，仅拦截 testhost 加载未签名 DLL。
- 本机历史（SAC 开启前）该套件为 **847 passed / 0 failed**（`.omo/evidence/v4-lock-dotnet-test-final.txt`，`.omo/evidence/v3-task10-dotnet-test.txt` 记录 827 通过）。当前提交的 .NET 侧行为**未执行验证**。

> 判定：`dotnet test` **BLOCKED（环境）**，不是通过、也不是失败。未做任何系统级绕过（关闭 SAC 需重置 Windows 且超出本任务范围，禁止）。

---

## 3. 回填 spike 判定 + T20 路由

命令：`.venv\Scripts\python.exe tests/spike_translate_reflow.py`
结果：**spike 完成：VERDICT: PASS**（exit 0；重写 `.omo/evidence/v11-spike-report.md`）

| 指标 | 阈值 | 实测 | 判定 |
|---|---|---|---|
| 溢出页占比（S2∪S3） | ≤ 10% | 0/10 = 0.0% | PASS |
| 每页裁切块数 | ≤ 2 | 0 | PASS |
| ToUnicode 覆盖率 | = 100% | 9/9 = 100.0% | PASS |
| 降级报告 schema（每页 `degraded`+`reasons`） | 完整 | PASS | PASS |

- 灵敏度检查（非门禁）：CJK 字数比 1.0 → 溢出 60%；1.5 → 溢出 90%，机制确实会触发。
- 机制自检：S3 `clip` 触发 1 块、S2 `overflow_margin` 触发 1 块。
- **T20 路由（依据 spike）：允许 T20 并入回填产物**（mono + dual、可搜索校验、每页降级报告）。已按该路由集成（`backend/engine.py:_translate_document` → `output.write_outputs`）。
- spike 自带诚实披露（沿用）：门禁样例默认 EN→ZH 收缩方向只触发 S0/S1；`formula_fail`/`table_fail`/`rotation_crop`/`unsupported_script`/`reassembly_mismatch`/`segment_split` 已登记枚举但当前链路从不写出（见 §8）。

---

## 4. 端到端 walkthrough（划词 / 整篇 / 批量 / 同意 / 撤销同意）

> 诚实前置：本环境**无法执行 UI 级真人走查**——`dotnet test` 被 SAC 拦截（§2.3），且生产翻译入口未接线（§8-①）。以下区分「引擎级自动端到端（已执行）」与「UI 级（未执行）」并注明阻塞原因。

| 流程 | 引擎级验证 | UI 级验证 | 证据 |
|---|---|---|---|
| 划词 | `translate_text` op 逻辑存在（`engine.py:978-1034`）；服务层单测（C#，未执行）覆盖 | **未执行**：`_translationConfig` 为 Null → 卡片恒返回「翻译服务尚未配置」 | `Services/SelectionTranslation.cs:80-85`；`MainWindow.xaml.cs:936-939` |
| 整篇 | **已执行 PASS**：orchestrator `test_end_to_end_mono_and_dual`（mono+dual、可搜索、源只读、密钥卫生） | **未执行**：`FullTranslationViewModel._configProvider` 为 Null → 状态「翻译服务尚未配置」 | `test_translate_orchestrator.py:84-127`；`FullTranslationViewModel.cs:160/404-408` |
| 批量 | **已执行 PASS**：orchestrator `test_batch_translates_multiple_files`（2 文件） | **未执行**：批量 VM 同样走 Null provider | `test_translate_orchestrator.py:135-142`；`TranslationBatchViewModel.cs:209/378` |
| 云端同意（首次） | 服务层规则有单测（C#，未执行）；Python 无对应 | **未执行 + 缺 UI**：无 F5 同意模态，无 `GrantConsent` 生产调用方 | `TranslationConsentService.cs`；全文无同意对话框 |
| 撤销同意 / 清凭据 | 服务层有单测（C#，未执行） | 设置对话框入口存在（`SettingsDialog.cs` 翻译段：撤销同意 / 清除凭据）；未执行 | `SettingsDialog.cs:6-11,205-261,462-478` |
| spike 判定 | **已执行 PASS**（§3） | — | `v11-spike-report.md` |

补充：回填链路中「源目录不可写 → sidecar 回退 %TEMP%」「hidden 属性」「退出清理」由 `SelectionSidecarStore.cs` 实现（会话内有效），未做真人 UI 走查。

---

## 5. 21 条设计决策对齐审计

> 审计方式：读实现源码 + 引用行号；「部分实现」指设计承诺存在未落地的子项。

| # | 决策 | 判定 | 关键证据 |
|---|---|---|---|
| 1 | 范围=正式扩展，更新 AGENTS.md/README | ✅ 实现 | `AGENTS.md`（33 工具、翻译纳入段）；`README.md`（能力段 + 网络段诚实改写）；提交 `57e42c6`/`6b3ed7b` |
| 2 | 载体=右栏双 Tab「划词/整篇」，整篇纵向堆叠 | ✅ 实现 | `MainWindow.xaml.cs:635-708`；`Views/Translation/FullTranslationPanel.xaml`；`SelectionTranslationCard.xaml` |
| 3 | 译文=mono/dual PDF 新标签打开 | ✅ 实现 | `backend/translate/output.py:336-377`；`FullTranslationViewModel.cs` 的 `OpenArtifactRequested`→`OpenDocumentTab`（`MainWindow.xaml.cs:100`） |
| 4 | 默认 mono+dual；Markdown 可选 | ⚠️ 部分 | mono/dual 默认开：`TranslationConfigViewModel.cs:142-146`；**Markdown 导出未实现**：`ExportMarkdown`（`:150`）仅参与校验，引擎无 `.md` 产出，且仅勾 Markdown 时 `format` 退化为 `dual`（`FullTranslation.cs:156-158`） |
| 5 | 批量工具页；结果列表按需打开 | ✅ 实现 | `backend/catalog.py` `translate_batch`；`TranslationToolPages.cs:17-30`；`TranslationBatchViewModel.cs:8`「绝不自动开标签」 |
| 6 | 划词=选中浮出「译」；无文本层不可用 | ✅ 实现 | `SelectionTranslationEventArgs.cs`；`MainWindow.xaml.cs:930-950`（含无文本层态） |
| 7 | 复用 A+C token，不新增 | ✅ 实现 | `ThemeManager.cs:37-50` 的 30 个 token 无翻译专属项；`ThemeManagerTests.cs:66-93` 全等断言；`Views/Translation/*.xaml` 零硬编码色值 |
| 8 | 引擎=自研轻量；不下载模型/字体 | ✅ 实现 | `backend/translate/*` 纯抽取→LLM→回填；`reflow.py:247`「不下载任何字体」；全仓无 download/onnx/huggingface |
| 9 | 云端首次同意 + 密钥仅 stdin + 凭据管理器 | ⚠️ 部分 | stdin：`EngineBridge.cs:130/164`；`FullTranslation.cs:164`、`SelectionTranslation.cs:95`；凭据库：`TranslationCredentialStore.cs`；**但生产无运行时配置提供者，且无 F5 同意模态**（§8-①） |
| 10 | 5 预置 prompt + 自定义 + 术语表 | ⚠️ 部分 | 5 预置：`prompt.py:45-99`；术语表：`glossary.py`；**自定义 prompt 未接入引擎**：`FullTranslationViewModel.ToEngineTemplate` 把 `custom`→`generic`（`:580-586`），`TranslationCustomPrompt` 被持久化却不发送 |
| 11 | 通用横排 LTR | ✅ 实现 | `extract.py` 阅读序/多栏；未做竖排硬编码 |
| 12 | 纳入 OCR / 公式表格尽力 / 术语 / 批量 / 可搜索 | ⚠️ 部分 | OCR：`extract.py:186-223`；术语：`glossary.py`；批量：§5-5；可搜索：`reflow.py:_verify_searchable`；**公式/表格无结构化检测，`formula_fail`/`table_fail` 从不写出**（spike 已披露） |
| 13 | 不做：模型下载/历史/RTL 竖排/跨页重排 | ⚠️ 部分 | 无模型下载、无历史、无跨页重排 ✅；**RTL/竖排无检测**，故「检测到即保留原文并标注」的**标注**无从触发（`unsupported_script` 从不写出，spike 已披露） |
| 14 | 同意按 provider+endpoint(+指纹)；TTL；变更重弹；改 model 不重置；改 key 失效；撤销+清凭据 | ✅ 实现（服务层） | `TranslationConsent.cs:23,55,62-87`；`TranslationConsentService.cs:53-78,89-128,162-166`；`TranslationConsentServiceTests.cs`（TTL/端点/密钥/provider） |
| 15 | 默认引擎=本地；同意默认未勾选 | ⚠️ 部分 | 默认本地：`TranslationConfigViewModel.cs:138`；同意数据默认空：`SettingsStore.cs:144-147`；**「记住我的选择」勾选框 UI 不存在**（无同意模态） |
| 16 | 输出可搜索 + 唯一溢出策略 | ✅ 实现 | `reflow.py` S0→S3（`:378-416`）、redaction 真实移除（`:534-540`）、ToUnicode 抽检（`:483-499`）；spike PASS |
| 17 | 成本硬上限 + 错误分类 + 统一重试 | ✅ 实现 | `llm.py:76-91,152-180`（退避 1s/4s、429 更长、空响应/校验各 1 次、拒绝/中止不重试）；`CostCap` |
| 18 | 安全边界：system/user 分离、nonce、禁工具、schema 校验、可疑指令拒绝告警、无遥测 | ⚠️ 部分 | nonce 包裹：`prompt.py:199-226`；schema 校验：`engine.py:_segment_validator`；无 tool/function、无遥测；**「可疑指令拒绝并告警」未接线**：`scan_untrusted_instructions` 仅被测试调用，引擎未据此拒绝段落 |
| 19 | 受限文档策略（仅权限加密可开、默认不译、显式确认、输出明示） | ✅ 实现 | `PdfiumRenderService.IsPermissionRestricted`（`:417-444`）；`RestrictedDocumentDialog`；`output.py:117-144` disclosure；`FullTranslationSourceProtection` |
| 20 | 语言源/目标分设（目标必须显式） | ✅ 实现 | `TranslationConfigViewModel.cs:96-105,225-235`；`catalog.py` targetLang 默认 `zh`、sourceLang 默认 `auto`；`SettingsStore.cs:151-175` |
| 21 | 密钥/同意持久化禁止清单 | ✅ 实现 | `SettingsStore` 无密钥字段；`TranslationProviderConfig.cs:28-37` 仅 provider/endpoint/model；同意仅存指纹（`TranslationConsent.cs:42-44`）；测试：`SettingsStoreTests.cs:378-399`、`TranslationConsentServiceTests.cs:267-291`、`TranslationProviderConfigTests.cs:56-64` |

**统计：14 ✅ / 7 ⚠️ / 0 ❌；⚠️ 为 #4、#9、#10、#12、#13、#15、#18。**

---

## 6. 分期交付对齐（§实现分期与验收门）

| 阶段 | 设计退出标准 | 判定 | 依据 |
|---|---|---|---|
| **V1.1a** 引擎底座 + spike | 测试集跑通；溢出 ≤10%、裁切 ≤2、ToUnicode=100%、降级报告完整 | ✅ PASS | §3 spike VERDICT PASS |
| **V1.1b** 划词 + 整篇 + 批量 + 云端同意 | 划词/整篇/批量全流程可走通；云端「首次同意→凭据保存→跨会话→撤销同意，调用可用」 | ❌ **未达标** | 引擎链路与 UI 面板均在，但**运行时配置未接线 + 无 F5 同意模态**，UI 恒「尚未配置」；`dotnet test` 亦被环境阻塞 |
| **V1.1c** 回填 mono/dual（T20 并入） | 产物可开、可搜索、降级报告完整 | ✅ 实现 | spike PASS → T20 并入；`output.py` mono/dual + 可搜索校验 + 降级报告 sidecar |
| **V1.1d** 批量工具页完整 | ≥3 份跑通；结果按需打开；同名去重 | ✅ 实现 | `TranslationBatchViewModel`（部分失败汇总 / RetryAllFailed / 结果列表）；`test_batch_translates_multiple_files`（2 文件引擎级）；同名去重见 output 文件名序号 |
| T24（本任务） | 端到端回归 + 设计对齐 | ✅ 本报告 | — |
| F1–F4 最终验收波 | 待执行 | ⏳ 未执行 | 本报告为输入 |

> 注：计划的 Wave 2 任务 T16/T17 在 `.omo/plans/pdf-translation-v1.1.md` 标记为 `[x]`，但对应证据文件 `v11-t16-*`/`v11-t17-*` **不存在**于 `.omo/evidence/`。服务层与设置层已落地，唯独「运行时配置提供者 + 同意模态」这一连接层缺失（§8-①）。

---

## 7. 守卫项（Guardrails）核对

| 守卫 | 判定 | 证据 |
|---|---|---|
| 不新增主题 token；`ThemeManagerTests` 绿 | ✅ | `ThemeManager.cs:37-50`；`ThemeManagerTests.cs:66-93` 全等断言；翻译 XAML 零硬编码色值（未执行 dotnet，属静态判定） |
| API 密钥绝不进 settings.json/argv/env/日志 | ✅ | `EngineBridge.cs:130/134/164`；`SettingsStore` 无密钥字段；测试 `SettingsStoreTests.cs:378-399`、`TranslationConsentServiceTests.cs:267-291`；`llm.py` 不读 env/argv（`:14-15`、`__repr__` 脱敏） |
| 默认引擎 = LOCAL | ✅ | `TranslationConfigViewModel.cs:138`；`TranslationConfigViewModelTests.cs:22` |
| 同意默认未勾选 | ⚠️ | 数据层默认无同意（`SettingsStore.cs:144-147`）；**勾选框 UI 不存在** |
| 高风险模板页脚标记强制、不可关 | ✅ | `output.py:110-112`（`_marker_enabled` 高风险强制）；`FullTranslationViewModel.cs:571-573`；`HighRiskTemplateDialog.xaml:55-61`（默认未勾选） |
| 不下载模型/字体 | ✅ | 全仓无 download/onnx/huggingface；`reflow.py:247` |
| 既有 32 工具不变，总数 33 | ✅ | `backend/catalog.py` 33 个 `tool(`；`translate_batch` 为唯一新增（git `8b2d5cc` 仅 `+tool("translate_batch"`，无删改）；`CatalogTests.cs:20`、`MainViewModelTests.cs:18`、`ToolChannelsTests.cs:71` |
| 源文档只读 | ✅ | `test_translate_orchestrator.py:116-118` 断言源 PDF 仍含原文 |

---

## 8. 缺口与未决项（诚实清单）

### ① 阻断级：生产翻译入口未接线运行时配置（影响 V1.1b 退出标准）

- `MainWindow.xaml.cs:652` — `private readonly ITranslationRuntimeConfigProvider _translationConfig = new NullTranslationRuntimeConfigProvider();`（`readonly`，无后续替换）。
- `MainWindow.xaml.cs:92-94` — 整篇 VM 注入该 Null provider；`MainWindow.xaml.cs:936` 划词同源；`TranslationToolPages.cs:24`→`TranslationBatchPage(bridge, tool)`→`TranslationBatchViewModel` 默认 Null（`TranslationBatchViewModel.cs:209`）。
- 全仓 `ITranslationRuntimeConfigProvider` 的实现**只有** `NullTranslationRuntimeConfigProvider`（`SelectionTranslation.cs:51`）与测试用 `FixedConfigProvider`；没有读取 `SettingsStore.TranslationProviders` + `TranslationCredentialStore` 组装 `TranslationRuntimeConfig` 的生产实现（`new TranslationRuntimeConfig(...)` 仅出现在测试与 `TranslationBatchViewModel.cs:730` 的空值兜底）。
- 后果：划词/整篇/批量在真实应用中恒返回「翻译服务尚未配置」；云端同意无「无同意即拦截」的真实入口；F5 同意模态缺失。
- 对照：`需求总结.md §V1.1b 退出标准`「云端：首次同意 → 凭据保存 → 跨会话复用 → 撤销同意；云端 LLM 调用可用」**未满足**。
- 归因：计划 T16/T17 的服务层/设置层已落地（`TranslationConsentService`、`TranslationCredentialStore`、`SettingsStore` schema、`SettingsDialog` 翻译段），唯独连接层缺失；T16/T17 证据文件也未落盘。

### ② 自定义 prompt 未接入引擎

- `TranslationConfigViewModel.cs:129-130/242-245` 有自定义 prompt 输入与校验，`SettingsStore.cs:200-202` 持久化 `TranslationCustomPrompt`；
- 但 `FullTranslationViewModel.ToEngineTemplate` 将 `custom` 映射为 `generic`（`:580-586`），自定义正文未发送；UI 注释承认「自定义模板引擎暂不支持」（`:576-579`）。决策 #10「自定义 prompt」未真正落地。

### ③ Markdown 导出未实现

- 决策 #4 将 Markdown 列为可选；`ExportMarkdown`（`TranslationConfigViewModel.cs:150`）参与校验但引擎 `_TRANSLATE_FORMATS` 只有 both/mono/dual（`engine.py:784`）。仅勾 Markdown 时 `format` 退化为 `dual`（`FullTranslation.cs:156-158`），产出对照 PDF 而非 `.md`。属可选能力，但当前行为与勾选语义不符。

### ④ 公式/表格/旋转/RTL 的结构化检测缺失（spike 已披露，继承）

- `formula_fail`/`table_fail`/`rotation_crop`/`unsupported_script`/`reassembly_mismatch`/`segment_split` 已在 `verify.DEGRADATION_REASONS` 登记，但 reflow/extract 链路从不写出对应原因（无检测逻辑）。决策 #12/#13 的「失败显式降级并标注」在这些类别上无输出。spike 报告 §附带发现 已如实披露。

### ⑤ 可疑文档指令「拒绝并告警」未接线

- `prompt.py:151-163` 提供 `scan_untrusted_instructions`，但仅被 `test_translate_prompt.py` 调用；`engine.py` 未据此拒绝/告警段落。当前唯一防线是 nonce 包裹（正文一律当待译内容），与决策 #18「命中即拒绝该段并告警」相比少一层。

### ⑥ 本地引擎 loopback 校验缺失

- 决策 §本地引擎 loopback 校验要求「标记为『本地』的端点必须是 loopback，非 loopback 不得以本地名义调用」。全仓未发现该校验（`TranslationProviderConfig.ValidateEndpoint` 仅校验 http(s) 绝对地址 + 无内嵌密钥，`TranslationProviderConfig.cs:49-69`；无 `IsLoopback` 判定）。因 provider 本就不区分 local/cloud 数据流（见 ①），此校验目前无处生效。

### ⑦ 卸载清理凭据未接线

- `TranslationCredentialStore.DeleteAll()` 存在（`:240-250`，仅删 `AIPDF-Translate-*` 前缀），但 `Install.ps1` 卸载段（`:243-287`）未调用，也无设计要求的「删除前列出数量确认」。决策 §凭据命名与卸载清理 的卸载部分未落地。

### ⑧ 环境阻塞（非代码）

- `dotnet test` 因 SAC 被 0 执行（§2.3）→ 本报告**未验证** 847 个 WPF 用例在当前提交的行为；翻译 UI 单测（Consent/Credential/Config/Full/Selection/Batch）均未实际运行。
- Python 全量 6 errors 为 LibreOffice/平台环境（§2.1）。

---

## 9. 结论

- **引擎与回填**：v1.1a/c 达标——回填 spike 三项量化门禁全 PASS，mono/dual 可搜索、每页降级报告完整，264 个翻译单测全绿，引擎级端到端（抽取→翻译→回填→输出、批量、拒译降级、密钥卫生、源只读）通过。
- **批量**：v1.1d 形态完整（工具页注册、结果列表按需打开、部分失败汇总）。
- **UI 接线**：**v1.1b 未达标**——生产入口仍用空运行时配置提供者，翻译在应用内不可用，云端同意模态缺失。这是本回归发现的**唯一阻断级缺口**，直接对应 V1.1b 退出标准，应由 F1 计划合规审计优先处理。
- **其余**：7 条决策为部分实现（#4 Markdown 导出子项、#9 云端同意接线、#10 自定义 prompt、#12 公式/表格结构化降级、#13 RTL/竖排标注、#15 同意勾选框、#18 可疑指令拒绝告警）；无整条决策完全未实现。另有 loopback 校验与卸载清凭据两个设计子项未落地。
- **环境诚实声明**：`dotnet test` 未执行（SAC），UI 级真人走查未执行；本报告未将任何跳过项记为通过。

> 建议 F1/F2 修复入口：① 实现生产 `ITranslationRuntimeConfigProvider`（读 `SettingsStore.TranslationProviders` + `TranslationCredentialStore`，按 `TranslationConsentService` 校验同意）并在 `MainWindow.xaml.cs:652` 替换 Null；② 补 F5 同意模态（默认未勾选）；③ 接线 custom prompt / Markdown 导出 / loopback 校验 / 卸载清凭据；④ 补写 `formula_fail` 等结构化检测或明确降级说明。
