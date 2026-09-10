# PDFium 绑定选型报告 v1.0（决策 18 · spike 实测版）

> 本文是需求总结.md:142（PRD 阶段产出 2）的交付物：为决策 18 的「具体绑定库」空位填入结论。决策 18 本身（渲染栈替换为持久句柄 PDFium 绑定，渲染 + 文本层同栈）已在需求总结.md:81 裁决，本文不重开该决策，只回答"用哪个库"。
> 预算依据 R24 初值（需求总结.md:135）：单页渲染 P95 < 200ms、缓存 ≤ 256MB、搜索 P95 < 500ms。渲染管线现状问题与持久句柄要求见需求总结.md:94。
> 全部数据来自 2026-09-10 在本机完成的四候选对照 spike（`tmp/pdfium-spike/`，已 gitignore，本报告自包含复现说明）。语料全部为合成文件，未读取任何个人文档。

**决策门结论（先行）**：**维持决策 18，推荐 PDFiumCore 154.0.8035 作为持久句柄绑定。** Docnet.Core / DtronixPdf / PDFtoImage 三家均无注释与表单 API，命中一票否决规则出局；唯一幸存者 PDFiumCore 六维全部达标。

**最后更新**：2026-09-10
**阶段**：v1.0 PRD

---

## 1. 版本固定清单

| 组件 | 固定版本 | 许可 | 角色 |
|------|---------|------|------|
| **PDFiumCore** | **154.0.8035** | Apache-2.0 | **推荐：持久句柄主绑定** |
| bblanchon.PDFium native（pdfium.dll） | 154.0.8035（对应 PDFium revision = pdfium/main） | BSD-3-Clause | PDFiumCore 配套 native，随包分发 |
| Docnet.Core | 2.6.0 | MIT | 候选 A（一票否决出局） |
| DtronixPdf | 1.3.1 | MIT | 候选 C（一票否决出局） |
| PDFtoImage | 5.4.0 | MIT | 现状基线（决策 18 的被替换对象） |
| .NET SDK | 8.0.x（实测 8.0.424） | — | 目标框架 net8.0 |

版本固定策略：PDFiumCore 随 bblanchon.PDFium 约每 2 周发版（2026-09-01 最新），产品侧应**锁定具体版本号**（154.0.8035），升级走显式验证流程（见 §10 触发条件 2）。

## 2. 机器规格

| 项目 | 规格 |
|------|------|
| CPU | AMD Ryzen 5 5600U with Radeon Graphics |
| RAM | 16 GB（约 13.9 GiB 可用） |
| GPU | AMD Radeon（集成）+ NVIDIA GeForce MX450（独立） |
| OS | Windows 11 Build 26200 |
| .NET | 8.0.424（位于 `%LOCALAPPDATA%\Microsoft\dotnet`，不在 PATH） |

与 `03-验收预算表.md` 硬件基线一致，spike 数字可直接用于该表校准。

## 3. 方法论

- **计时**：`System.Diagnostics.Stopwatch`；每场景 **3 次预热 + 30 次实测**；**P95 = 升序排序后取 `sorted[floor((n-1)*0.95)]`**（`Bench.P95`，Bench.cs:34-39）。
- **冷启动**：text1000（1000 页）加载文档 + 首页渲染一次的总耗时，衡量"打开文档到首帧"。
- **线程测试**：同一已打开文档上，2 线程经 `ManualResetEventSlim` barrier 同步起跑，并发执行「渲染第 10 页 ∥ 第 21 页文本操作」；与各自串行 P95 对比得**阻塞比 blocking_ratio = 并发耗时 / 串行耗时**（越接近 1 越好，>1 表示锁竞争开销）。
- **搜索**：cjk500 全文查 `SECRET-123`（预期 500 命中），逐页 `FPDFText_FindStart`/`FindNext`；flags=0（PDFium 默认大小写不敏感——R13 CJK 搜索设计需知）。
- **公平性处理**：Docnet.Core 以 `ExcludeAssets="native"` 剔除其自带旧 pdfium.dll，四家共享 bblanchon 154 单一 native（Docnet 只用稳定 FPDF_* 导出，兼容）。
- **渲染非空校验**：采样像素校验和（每 4097 字节取 1 字节求和，0 = SUSPICIOUS-BLANK），只验证"非空白 + 尺寸正确"，不作跨库像素等价断言。
- **损坏 PDF 探针**：子进程隔离执行（`corrupt <candidate>` 模式），退出码 0 = 优雅处理，非 0 = 原生崩溃。
- **未覆盖项（如实声明）**：缓存 ≤256MB 属渲染服务 LRU 字节预算（产品层实现），非绑定层职责，spike 未测；native 内存 working-set 差值法亦未纳入本轮。

## 4. 语料规格

7 个合成 PDF，由 `gen_corpus.py`（PyMuPDF）生成，无个人文档：

| 文件 | 规格 | 用途 |
|------|------|------|
| cjk500.pdf | 500 页 CJK 文本（china-s 字体），每页含 `SECRET-123` 标记 | 搜索 / 文本提取 |
| text1000.pdf | 1000 页纯 ASCII 文本 | 冷启动 |
| mixed100.pdf | 100 页混合文本/矢量/图像 + 第 1 页 AcroForm 文本字段 `bench_field`（初值 initial-value） | 渲染 / 表单 |
| encrypted.pdf | 10 页 AES-256 加密（用户/属主密码 `SECRET-123`） | 健壮性 |
| rotated.pdf | 10 页 /Rotate 90 | 健壮性 |
| hugepage.pdf | 单页 10000×10000 pt | 健壮性（限幅） |
| garbage-header.pdf | 有效 PDF 前 1024 字节替换为伪随机垃圾 | 损坏探针 |

## 5. 原始数据表

原始 CSV（UTF-8，不变文化小数点）位于 `tmp/pdfium-spike/results/`：

| 文件 | 内容 | 行数 |
|------|------|------|
| `results/render.csv` | 渲染 P95（96/150dpi）+ 冷启动 | 8 |
| `results/thread.csv` | 并发/串行 P95 + 阻塞比 | 4 |
| `results/search.csv` | 全文搜索 P95 + 命中数 + 实现方式 | 4 |
| `results/text.csv` | CJK 提取正确率 + 单页耗时 | 4 |
| `results/api.csv` | 注释/表单 API 存在性 + 验证细节 | 8 |
| `results/robustness.csv` | 加密/旋转/巨页/损坏头 4 场景 | 16 |
| `results/baseline.csv` | PDFtoImage 基线（render.csv 子集） | 2 |

关键数字摘录（完整表见 §6 各维度）：

| 候选 | 渲染 P95 96dpi | 渲染 P95 150dpi | 冷启动 | 搜索 P95 | CJK 提取 | 阻塞比 | 注释 API | 表单 API |
|------|---------------|----------------|--------|----------|----------|--------|----------|----------|
| PDFiumCore | 3.98ms | 6.52ms | 2.84ms | 55.86ms | 100% / 0.15ms | 1.24 | ✅ | ✅ |
| Docnet.Core | 3.30ms | 7.44ms | 4.30ms | 70.34ms | 100% / 0.20ms | 1.21 | ❌ | ❌ |
| DtronixPdf | 2.05ms | 3.83ms | 3.16ms | 61.39ms | 100% / 0.20ms | 0.98 | ❌ | ❌ |
| PDFtoImage | 13.45ms | 14.33ms | 159.15ms | n/a | n/a | 1.53 | ❌ | ❌ |

## 6. 六维度逐项结论

六维与 spike 程序的维度编号一一对应（Program.cs dim 1–6）；健壮性为附测证据，不计入六维。

### 6.1 维度一：线程安全模型与并发阻塞（thread.csv）

PDFium 全部 API 非线程安全（fpdfview.h 明确声明），全局单锁是硬约束（需求总结.md:94、R12）。本维验证：全局锁/串行化下并发渲染 ∥ 文本操作是否崩溃、锁开销多大。

| 候选 | 并发渲染 P95 | 并发文本 P95 | 串行渲染 P95 | 串行文本 P95 | 阻塞比 | 结论 |
|------|-------------|-------------|-------------|-------------|--------|------|
| PDFiumCore | 2.42ms | 0.18ms | 1.94ms | 0.09ms | **1.24** | ✅ 全局单锁下无崩溃，锁开销可接受；无内置锁，需封装层自实现 |
| Docnet.Core | 3.47ms | 0.37ms | 2.87ms | 0.11ms | 1.21 | ✅ 性能达标（内置全局静态锁，DocLib.cs:13-22）；API 面否决 |
| DtronixPdf | 2.22ms | 0.31ms | 2.27ms | 0.12ms | **0.98** | ✅ dispatcher 串行化零额外开销；API 面否决 |
| PDFtoImage | 19.97ms | 12.59ms | 13.03ms | 5.88ms | 1.53 | ⚠️ 达标但重解析放大并发耗时 |

四家均无原生崩溃。DtronixPdf 的 dispatcher 模式（阻塞比 0.98）可作为 PDFiumCore 全局锁实现的参考；Docnet 的全局静态锁是更简单但同粒度的方案。

### 6.2 维度二：文本提取质量（text.csv）

cjk500 第 1 页提取，验证 CJK（china-s）与 ASCII 标记 `SECRET-123` 完整性：

| 候选 | 正确率 | 单页耗时 | 提取样本 |
|------|--------|---------|---------|
| PDFiumCore | **100%** | 0.15ms | `本地 PDF 工具测试 第1页 … SECRET-123 marker 1 …`（len=132，secret=True cjk=True） |
| Docnet.Core | 100% | 0.20ms | 同上 |
| DtronixPdf | 100% | 0.20ms | 同上 |
| PDFtoImage | n/a | — | **无文本 API** |

结论：三家有文本 API 的候选 CJK 提取全部正确；PDFiumCore 达标且最快。文本层与渲染同栈（需求总结.md:95）在 PDFiumCore 上成立。

### 6.3 维度三：文本搜索（search.csv）

cjk500 全文搜索 `SECRET-123`，预算 P95 < 500ms（R24）：

| 候选 | 搜索 P95 | 命中数 | 实现方式 | 结论 |
|------|---------|--------|---------|------|
| PDFiumCore | **55.86ms** | 500 | 原生 `FPDFText_FindStart`/`FindNext` 逐页 | ✅ 达标，唯一原生搜索 |
| Docnet.Core | 70.34ms | 500 | 降级：逐页 GetText 扫描 | ⚠️ 性能达标但无原生搜索 API |
| DtronixPdf | 61.39ms | 500 | 降级：逐页 GetText 扫描 | ⚠️ 同上 |
| PDFtoImage | n/a | — | 无文本 API | ❌ 能力缺失 |

结论：PDFiumCore 原生搜索 55.86ms，为预算的 11%；两家降级扫描虽也达标，但暴露 API 缺口（无 FPDFText_Find*）。注意 flags=0 默认大小写不敏感，R13 的 CJK 搜索启发式需在此语义上设计。

### 6.4 维度四：渲染性能与冷启动（render.csv）

mixed100 第 10 页（文本+矩形+图像）渲染 P95，预算 < 200ms（R24）；冷启动 = text1000 加载 + 首页渲染：

| 候选 | 96dpi P95 | 150dpi P95 | 冷启动（1000 页） | 输出尺寸 / 采样校验和 |
|------|-----------|-----------|------------------|---------------------|
| PDFiumCore | 3.98ms | 6.52ms | **2.84ms** | 793×1123px / 217377；1240×1754px / 532955 |
| Docnet.Core | 3.30ms | 7.44ms | 4.30ms | 793×1122px / 16384；1239×1754px / 40117 |
| DtronixPdf | 2.05ms | 3.83ms | 3.16ms | 793×1122px / 16384；1239×1754px / 40117 |
| PDFtoImage | 13.45ms | 14.33ms | 159.15ms | 793×1123px / 217377；1240×1754px / 532955 |

说明（如实记录）：
- PDFiumCore 与 PDFtoImage 输出**逐字节一致**（校验和相同，含巨页 4162557）——两者同用 bblanchon pdfium 154，像素输出一致是预期行为。
- Docnet/DtronixPdf 尺寸差 1px（1122/1239）且采样校验和数值不同，系 spike 适配器像素格式/步长差异所致；校验和仅用于非空白判定（0 = SUSPICIOUS-BLANK，四家均非 0），不作跨库像素等价断言。
- 四家渲染 P95 全部远低于 200ms 预算；**冷启动差距（2.84 vs 159.15ms）才是决策 18 的核心收益**，详见 §8。

结论：PDFiumCore 达标。DtronixPdf 渲染数字略优（2.05ms），但渲染性能不是区分维度（全部达标），不构成推荐依据。

### 6.5 维度五：注释 API（一票否决项）（api.csv）

决策 19（标准注释操作：add_highlight_annot + FreeText 笔记）与决策 17（批注就地原子写回）硬依赖 FPDFAnnot_* + FPDF_SaveWithVersion：

| 候选 | 注释 API | 已验证 API 链 | 保存+重载验证 |
|------|---------|--------------|--------------|
| PDFiumCore | **TRUE** | `FPDFPage_CreateAnnot(HIGHLIGHT)`、`FPDFAnnot_SetRect`、`FPDFAnnot_SetColor`、`FPDFAnnot_SetStringValue`、`FPDF_SaveWithVersion`、`FPDFPage_GetAnnotCount` | ✅ 保存后重载 annots=3（含 1 个既有 widget——widget 亦属 annotation） |
| Docnet.Core | FALSE | — | ❌ 否决 |
| DtronixPdf | FALSE | — | ❌ 否决 |
| PDFtoImage | FALSE | — | ❌ 否决 |

PDFiumCore 实测链路：HIGHLIGHT（SetRect 50,640→400,700 + SetColor 黄色 α120 + SetStringValue "bench highlight"）与 FREETEXT 笔记创建 → `FPDF_SaveWithVersion`（filewrite 回调，PDF 版本 17）→ 重载 `FPDFPage_GetAnnotCount` ≥ 2 通过。产物：`results/annot_out.pdf`。

### 6.6 维度六：表单 API（一票否决项）（api.csv）

决策 1（交互表单工具）硬依赖表单字段读写与持久化：

| 候选 | 表单 API | 已验证 API 链 | 读/写/持久化 |
|------|---------|--------------|-------------|
| PDFiumCore | **TRUE** | `FPDFDOC_InitFormFillEnvironment`、`FORM_OnAfterLoadPage`、`FPDFAnnot_GetFormFieldAtPoint`/`GetFormFieldName`/`GetFormFieldType`/`GetFormFieldValue`、`FORM_SetFocusedAnnot`、`FORM_SelectAllText`、`FORM_ReplaceSelection`、`FORM_ForceToKillFocus`、`FPDF_SaveWithVersion` | ✅ 读=TRUE 写=TRUE 持久化=TRUE（`bench_field`：initial-value → **BENCH-42**，type=6 文本字段） |
| Docnet.Core | FALSE | — | ❌ 否决 |
| DtronixPdf | FALSE | — | ❌ 否决 |
| PDFtoImage | FALSE | — | ❌ 否决 |

实现要点（封装层必须处理，详见 §11 坑清单）：字段写入走键盘模拟路径（现代 fpdf_formfill.h 已无 FORM_Get/SetTextFieldValue）；`FPDF_FORMFILLINFO` 需 Version=1 + 全部回调 no-op 委托并**手动保活**（native 持有该结构指针至 ExitFormFillEnvironment）。XFA 不支持，仅 AcroForm。产物：`results/form_out.pdf`。

### 附：健壮性附测（不计入六维，robustness.csv）

| 场景 | PDFiumCore | Docnet.Core | DtronixPdf | PDFtoImage |
|------|-----------|-------------|------------|------------|
| encrypted-aes256（密码 SECRET-123） | ok（793×1123px，220827） | ok（631） | ok（631） | ok（220827） |
| rotated-90 | ok（1123×793px，220858） | ok（138） | ok（138） | ok（220858） |
| hugepage-10000pt | ok（**限幅 4096×4096px**，4162557） | ok（4872） | ok（4872） | ok（4162557） |
| corrupt-garbage-header | graceful：`FPDF_LoadMemDocument64` 返回 null（`FPDF_GetLastError=3`） | graceful：抛 DocnetLoadDocumentException（ErrorCode=3） | graceful：抛 InvalidOperationException | graceful：抛 PdfInvalidFormatException |

四家全部优雅处理，子进程隔离探针确认**无原生崩溃**。PDFiumCore 的 null + GetLastError 模式与限幅策略（巨页 cap 4096px）可直接映射到渲染服务的错误处理与超纹理限幅设计（需求总结.md:94）。

## 7. 决策门评估

### 7.1 一票否决规则（先行应用）

**规则**：候选缺注释 API 或表单 API 任一项 → 直接出局，不看其余维度。
**依据**：决策 19（批注引擎标准注释操作）与决策 1（交互表单）是 v1.0 必需项，批注就地写回（决策 17）依赖 FPDFAnnot_* + FPDF_SaveWithVersion；没有这两类 API 的绑定无法承载阅读器核心功能，渲染性能再好也不可推荐。

### 7.2 否决执行

| 候选 | 注释 | 表单 | 判定 | 附加理由（记录在案，非否决主因） |
|------|------|------|------|--------------------------------|
| Docnet.Core 2.6.0 | ❌ | ❌ | **一票否决** | 维护停滞（2023-09 后无稳定版）；无原生搜索 API |
| DtronixPdf 1.3.1 | ❌ | ❌ | **一票否决** | 锁定 PDFiumCore 134 旧版（供应链风险）；低频维护（2025-03 后无发布） |
| PDFtoImage 5.4.0 | ❌ | ❌ | **一票否决** | 无持久句柄、无文本 API——本就是决策 18 的被替换对象 |

### 7.3 幸存者 PDFiumCore 六维预算核对

| 维度 | 预算 / 要求 | 实测 | 判定 |
|------|------------|------|------|
| 渲染 P95 96dpi | < 200ms | 3.98ms | ✅（预算的 2%） |
| 渲染 P95 150dpi | < 200ms | 6.52ms | ✅ |
| 冷启动（1000 页加载+首帧） | 对照基线 159.15ms | 2.84ms | ✅（56 倍改善） |
| 搜索 P95（500 页全文） | < 500ms | 55.86ms | ✅（预算的 11%） |
| CJK 文本提取 | 正确 | 100%（0.15ms/页） | ✅ |
| 并发（全局单锁） | 无原生崩溃、锁开销可接受 | 阻塞比 1.24，无崩溃 | ✅ |
| 注释 API | 必需（一票否决项） | 创建/保存/重载全通 | ✅ |
| 表单 API | 必需（一票否决项） | 读/写/持久化全通 | ✅ |
| 缓存 ≤ 256MB | 渲染服务 LRU 字节预算（产品层） | spike 未测，非绑定层职责 | ○ 移交渲染服务实现 |

### 7.4 三路径判定

- **路径 1：维持决策 18 + 推荐具体库** ← **命中**。推荐 **PDFiumCore 154.0.8035**。
- 路径 2（部分达标加权推荐）：不适用——唯一幸存者全维达标，无需加权。
- 路径 3（全失败回退）：不适用——存在达标候选。

### 7.5 不利点如实记录（不改变结论）

1. **C 风格 API、无高层封装**：CppSharp 自动生成，命名不一致（`FPDFTextLoadPage` 去下划线 vs `FPDF_SaveWithVersion` 保留），无 PdfDocument 类，句柄生命周期需封装层管理。
2. **需自实现全局单锁**：PDFiumCore 无内置锁（fpdfview.h 声明非线程安全）；DtronixPdf dispatcher（阻塞比 0.98）与 Docnet 静态锁可作参考实现。
3. **公开 API 的 FPDF_WCHAR\* 输入封送缺陷**：`FPDFText_FindStart` 等输入参数经 CppSharp 封送为空缓冲（实测任意词都"命中" index 0）；解法为对 pdfium.dll 直接 DllImport（标准 C 导出名）+ fixed 指针绕过，spike 已验证可行。
4. **FPDF_FORMFILLINFO 回调委托不被持根**：native 调用已 GC 的委托会致命崩溃（"callback on collected delegate"）；所有 no-op 委托须存静态列表保活。
5. **`FPDFText_FindNext` 返回值与文档不符**：本 build 返回 1/0（命中/无），命中位置用 `FPDFText_GetSchResultIndex`、长度用 `FPDFText_GetSchCount`。

以上均归入封装层一次性工作量，不影响选型结论。

## 8. PDFtoImage 基线对比（现状架构代价量化）

| 场景 | PDFtoImage 5.4.0（现状） | PDFiumCore 154.0.8035 | 差距 |
|------|------------------------|----------------------|------|
| 冷启动（1000 页加载 + 首页渲染） | 159.15ms | 2.84ms | **56.0×** |
| 渲染 P95 96dpi | 13.45ms | 3.98ms | **3.4×** |
| 渲染 P95 150dpi | 14.33ms | 6.52ms | 2.2× |
| 并发阻塞比（渲染∥文本） | 1.53 | 1.24 | 重解析放大 |
| 文本搜索 | 无 API（CSV 记 -1） | 55.86ms | 不可比（能力缺失） |
| 文本提取 | 无 API | 0.15ms/页 | 不可比（能力缺失） |

**根因**：PDFtoImage 无状态逐页调用——`GetPageSizes` 一次 + `ToImage` 一次 = 每页渲染**两次全量解析**整个 PDF（与需求总结.md:94 对现状 `PdfPreviewDocument` 的描述一致），且整文件读入内存（打开瞬时峰值 2× 文件大小）。持久句柄消除重复解析，是 56× 冷启动差距与 3.4× 渲染差距的来源。本表同时量化了决策 18 的收益上限：**打开文档到首帧从 ~159ms 降至 ~3ms**，为"打开即读"体验提供数据支撑。

## 9. 许可证记录

| 组件 | 许可 | 与本项目 AGPL-3.0-only 的兼容性 |
|------|------|--------------------------------|
| PDFiumCore | Apache-2.0 | **兼容**。FSF 认可 Apache-2.0 与 GPLv3/AGPLv3 单向兼容；作为依赖引入不改变项目自有代码的 AGPL-3.0-only；须保留其 Apache-2.0 声明与 NOTICE 条款 |
| pdfium.dll（PDFium Authors） | BSD-3-Clause | 兼容。bblanchon 包内 native `LICENSE` 文件含完整声明（"# BEGIN PDFium license / Copyright 2014 The PDFium Authors"） |
| Docnet.Core | MIT | 兼容（未选用） |
| DtronixPdf | MIT | 兼容（未选用） |
| PDFtoImage | MIT | 兼容（现状使用中；新栈若移除则其传递依赖 SkiaSharp/MIT 一并消失） |
| PDFiumSharp | MS-RL | **不兼容**（reciprocal 源码分发义务与 AGPL-3.0-only 冲突）——预筛阶段已排除 |

义务落地（按 AGENTS.md）：选定 PDFiumCore 后，须将 Apache-2.0 许可文本与 pdfium.dll 的 BSD-3-Clause 声明纳入 `THIRD_PARTY_NOTICES.md`，依赖变更时运行 `.venv/bin/python scripts/collect_licenses.py` 更新许可快照并核对差异；不擅自更改许可证或去掉版权声明。

## 10. 建议与回退触发条件

### 10.1 建议

- **主选**：PDFiumCore 154.0.8035 作为持久句柄绑定（渲染 + 文本层同栈）。
- **配套工程**（封装层，一次性投入）：
  1. 全局单锁（参考 DtronixPdf dispatcher / Docnet 静态锁模式，spike 阻塞比 1.24 可接受）；
  2. 句柄生命周期管理（FPDF_LoadDocument → 页尺寸一次解析全局缓存 → FPDF_CloseDocument）；
  3. WCHAR 封送绕过层：对 pdfium.dll 直接 DllImport（`FPDFText_FindStart`、`FPDFAnnot_SetStringValue`、`FPDFAnnot_GetFormField*`、`FORM_ReplaceSelection` 等）+ fixed 指针；
  4. `FPDF_FORMFILLINFO` 委托静态保活列表；
  5. 错误处理映射（null + `FPDF_GetLastError`）与超纹理限幅（巨页 cap 4096px 已验证）。
- **明确不做**：不推荐任何缺注释/表单 API 的候选；不因 DtronixPdf 渲染数字略优（2.05ms vs 3.98ms，均在预算 2% 内）改变否决结果。

### 10.2 回退路径

- **旧回退（已被取代）**：手写 P/Invoke——预筛结论为被 PDFiumCore 的 CppSharp 全量签名完全取代，无需投入。仅当 §10.3 条件 3 成立时重启，可参考 PDFiumCore 的生成逻辑在项目内 fork 适配。
- **现状回退（不推荐）**：维持 PDFtoImage——56× 冷启动差距与无文本/注释/表单 API 使阅读器核心功能不可实现，仅作为决策 18 推翻时的兜底。

### 10.3 重新触发选型的条件

1. **生产实测超预算**：渲染 P95 ≥ 200ms 或搜索 P95 ≥ 500ms（排除缓存未命中、并发干扰等实现因素后仍超）。
2. **上游 API 回归**：PDFiumCore 未来版本出现破坏性变更且上游短期内未修复（bblanchon 每 2 周发版节奏下，锁定版本 + 显式升级验证是默认策略）。
3. **封送缺陷扩大**：WCHAR 输入封送问题扩展到无法用 DllImport 绕过的 API 面（如结构体布局错误导致崩溃）。
4. **许可政策变化**：Apache-2.0 与 AGPL-3.0-only 的兼容性被项目政策重新评估。

## 11. 复现说明

### 11.1 目录结构（tmp/ 已 gitignore，本节自包含）

```
tmp/pdfium-spike/
├── gen_corpus.py          # 语料生成（Python + PyMuPDF）
├── corpus/                # 7 个合成 PDF（§4）
├── PdfiumSpike.csproj     # 版本固定（§1）+ ExcludeAssets=native 说明
├── Program.cs             # 入口：6 维 × 4 候选 + 基线 + corrupt 子进程模式
├── Bench.cs               # 计时引擎：Measure/P95/RunConcurrent/Csv
├── Candidates/            # 每绑定适配器
│   ├── PdfiumCoreBench.cs #   含全局锁、DllImport 绕过层、注释/表单验证
│   ├── DocnetBench.cs
│   ├── DtronixPdfBench.cs
│   └── PdfToImageBench.cs
├── results/               # 7 CSV + annot_out.pdf + form_out.pdf
├── apidump/               # 反射 API dump 工具（验证 API 面用）
└── run1..4.log            # 四轮运行日志（最终数据取 run4）
```

### 11.2 运行命令

```powershell
# 1) 生成语料（需 Python 3.12+ 与 PyMuPDF）
python tmp/pdfium-spike/gen_corpus.py tmp/pdfium-spike/corpus

# 2) 全量基准（6 维 × 4 候选 + PDFtoImage 基线，输出 results/*.csv）
dotnet run --project tmp/pdfium-spike -c Release

# 3) 损坏 PDF 子进程探针（退出码 0 = 优雅，非 0 = 原生崩溃）
dotnet run --project tmp/pdfium-spike -c Release -- corrupt PDFiumCore
```

注意：Windows Smart App Control（评估模式）会拦截确定性构建哈希（0x800711C7 FileLoadException），csproj 已设 `Deterministic=false` 规避（仅 spike，不入产品）。

### 11.3 关键代码片段

**P95 计时（Bench.cs）**：

```csharp
public const int WarmupIterations = 3;
public const int MeasureIterations = 30;

public static double P95(double[] xs) {
    var sorted = xs.OrderBy(x => x).ToArray();
    return sorted[(int)Math.Floor((sorted.Length - 1) * 0.95)];
}
```

**全局单锁模式（PdfiumCoreBench.cs，所有 FPDF_* 调用包在 lock 内）**：

```csharp
public static readonly object Lock = new();
// 每个操作：
lock (Lock) { var page = fpdfview.FPDF_LoadPage(doc.Handle, idx); /* ... */ }
```

**WCHAR 封送绕过（对 pdfium.dll 直接 DllImport + fixed 指针）**：

```csharp
const string Lib = "pdfium";
[DllImport(Lib, EntryPoint = "FPDFText_FindStart", CallingConvention = CallingConvention.Cdecl)]
public static extern IntPtr FPDFText_FindStart(IntPtr text_page, ushort* findwhat, ulong flags, int start_index);

var buf = Utf16.ToBuffer(term);          // 手动 UTF-16 编码
fixed (ushort* p = buf) {
    var h = PdfiumNative.FPDFText_FindStart(tp.__Instance, p, 0, 0);
    // 命中位置 = FPDFText_GetSchResultIndex，长度 = FPDFText_GetSchCount
}
```

**表单写入链（headless 键盘模拟路径）**：

```csharp
// FPDFDOC_InitFormFillEnvironment(Version=1 + 全部回调 no-op 委托，静态保活)
// → FORM_OnAfterLoadPage → FPDFAnnot_GetFormFieldAtPoint(读)
// → FORM_SetFocusedAnnot → FORM_SelectAllText → FORM_ReplaceSelection("BENCH-42")
//   → FORM_ForceToKillFocus → FPDF_SaveWithVersion → 重载验证持久化
```

**注释验证链**：

```csharp
var annot = fpdf_annot.FPDFPageCreateAnnot(page, 9 /*FPDF_ANNOT_HIGHLIGHT*/);
fpdf_annot.FPDFAnnotSetRect(annot, rect);
fpdf_annot.FPDFAnnotSetColor(annot, FPDFANNOT_COLORTYPE_Color, 255, 255, 0, 120);
PdfiumNative.FPDFAnnot_SetStringValue(annot.__Instance, "Contents", p); // DllImport 绕过
fpdf_save.FPDF_SaveWithVersion(doc, fwrite, 0, 17);                      // filewrite 回调 → byte[]
// 重载后 FPDFPage_GetAnnotCount >= 2
```

### 11.4 已知坑清单（复现时必读）

1. 公开 API 的 `FPDF_WCHAR*` **输入**参数封送为空缓冲（输出缓冲正常）→ 一律 DllImport 绕过。
2. `FPDFText_FindNext` 本 build 返回 1/0，非文档写的 index/-1；位置/长度用 `GetSchResultIndex`/`GetSchCount`。
3. `FPDF_FORMFILLINFO` 回调委托必须静态保活，否则 "callback on collected delegate" 致命崩溃。
4. 方法命名不一致：fpdf_text/fpdf_annot 去前缀下划线（`FPDFTextLoadPage`），fpdf_save 保留（`FPDF_SaveWithVersion`）。
5. Docnet.Core 自带旧 pdfium.dll 与 bblanchon 154 冲突 → `ExcludeAssets="native"`。
6. 搜索 flags=0 默认大小写不敏感。
7. dotnet 8.0.424 位于 `%LOCALAPPDATA%\Microsoft\dotnet`，不在 PATH。

---

## 关联

- 需求总结.md:81（决策 18）、:94（渲染管线约束）、:135（R24 预算初值）、:142（本选型任务）
- `01-工具通道映射表.md`（R7）、`03-验收预算表.md`（R24，指标 1–5 已用本报告数据校准）
- `tmp/pdfium-spike/CANDIDATES.md`（候选预筛与版本矩阵）
- AGENTS.md（许可义务与验证要求）
