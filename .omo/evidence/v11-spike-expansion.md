# v1.1a 回填 spike：**扩写方向**量化门禁报告

> 门禁依据：`设计/v1.1-翻译/需求总结.md §实现分期与验收门（V1.1a）`（溢出 ≤10% / 裁切 ≤2 / ToUnicode=100%）
> 证据提交：`6f6c291`（`git rev-parse --short HEAD`）
> 生成时间：2026-09-14 23:38:38（耗时 44.38s）
> 运行：`.venv\Scripts\python.exe tests\spike_translate_reflow_expansion.py`
> 样例：全部为 PyMuPDF 内存合成，**不读取任何真实或个人文档**。

## 为什么需要这份报告

原 `tests/spike_translate_reflow.py` 的量化门禁只跑 EN→ZH **收缩**方向（每块 [0.50, 0.80]），因此结构上无法触发产品的主导失败模式——**扩写溢出**。对抗性审查据此判定「门禁结构上抓不到主导失败模式」。本报告把 mock 译文设为**与源等长（ratio 1.00）**与**源 ×1.5（ratio 1.50）**，在同一 4 样例上重跑完整引擎链路，按同一套阈值判定，并**如实记录**数值。

## 方法

- LLM：patch `translate.llm._open`，mock 返回确定性伪中文译文，字符数 = `max(2, round(源字符数 × ratio))`，ratio=1.00 / 1.50；**无任何真实网络请求**。因此这是「等长 / 扩写」方向的诚实压力测试（测回填排版，不测模型质量）。
- 溢出页 = 带 `overflow_margin`(S2) 或 `clip`(S3) 的页；裁切块数 = S3 的块级调用次数（去重前计数，按输出形态数折算成单次渲染口径）；ToUnicode = 可提取译文页 / 应有译文页（扫描光栅页豁免，用 mock 译文前缀命中判定）。
- 与收缩门禁使用**同一套** `spike_translate_reflow` 度量代码，避免口径漂移。

## 扩写方向：三项量化门禁（逐 ratio）

### ratio = 1.00（mock 译文 = 源字符数 × 1.00）

| 样例 | 总页 | 溢出页(S2/S3) | 裁切页(S3) | 最大裁切块/页 | 应有译文页 | 可提取 | ToUnicode |
|------|-----:|--------------:|-----------:|--------------:|-----------:|-------:|----------:|
| 合成双栏 | 3 | 0 (0%) | — | 0 | 3 | 3 | 100% |
| 表格密集 | 3 | 3 (100%) | — | 0 | 3 | 3 | 100% |
| 公式密集 | 3 | 3 (100%) | — | 0 | 3 | 3 | 100% |
| 扫描件 | 1 | 0 (0%) | — | 0 | 0 | 0 | 100% |

| 指标 | 阈值 | 实测 | 判定 |
|------|------|------|------|
| 溢出页占比（S2∪S3） | ≤ 10% | 6/10 = 60.0% | FAIL |
| 每页裁切块数 | ≤ 2 | 0 | PASS |
| ToUnicode 覆盖率 | = 100% | 9/9 = 100.0% | PASS |

**ratio=1.00 VERDICT: FAIL**

本 ratio 实际写出的降级原因（页级计数）：`font_shrink`×9，`overflow_margin`×6，`raster_overlay`×1，`table_fail`×3。

### ratio = 1.50（mock 译文 = 源字符数 × 1.50）

| 样例 | 总页 | 溢出页(S2/S3) | 裁切页(S3) | 最大裁切块/页 | 应有译文页 | 可提取 | ToUnicode |
|------|-----:|--------------:|-----------:|--------------:|-----------:|-------:|----------:|
| 合成双栏 | 3 | 3 (100%) | — | 0 | 3 | 3 | 100% |
| 表格密集 | 3 | 3 (100%) | — | 0 | 3 | 3 | 100% |
| 公式密集 | 3 | 3 (100%) | — | 0 | 3 | 3 | 100% |
| 扫描件 | 1 | 0 (0%) | — | 0 | 0 | 0 | 100% |

| 指标 | 阈值 | 实测 | 判定 |
|------|------|------|------|
| 溢出页占比（S2∪S3） | ≤ 10% | 9/10 = 90.0% | FAIL |
| 每页裁切块数 | ≤ 2 | 0 | PASS |
| ToUnicode 覆盖率 | = 100% | 9/9 = 100.0% | PASS |

**ratio=1.50 VERDICT: FAIL**

本 ratio 实际写出的降级原因（页级计数）：`font_shrink`×5，`overflow_margin`×9，`raster_overlay`×1，`table_fail`×3。

## 检测原因端到端确认（HEAD 接线后）

原 `v11-spike-report.md` 称 `unsupported_script`/`table_fail`/`formula_fail`/`rotation_crop` 「当前 reflow/extract 链路从不写出」——该结论早于 commit `d1a0e81`（结构化检测接线）。下表用完整 `engine.dispatch` 链路确认现状：

**扩写主样例（ratio 1.50）触发：**

- `rotation_crop`：扩写主样例未触发（见下方专项样例）。
- `unsupported_script`：扩写主样例未触发（见下方专项样例）。
- `table_fail`：扩写主样例触发 3 页（见下方专项样例）。
- `formula_fail`：扩写主样例未触发（见下方专项样例）。

**专项检测样例（ratio 1.00，完整链路）：**

| 样例 | 执行 | 报告中出现的原因 |
|------|------|------------------|
| 旋转页 | OK | `font_shrink`, `rotation_crop` |
| 竖排页 | OK | `unsupported_script` |
| 高密度公式页 | OK | `formula_fail`, `overflow_margin` |

| 目标原因 | 证据来源 | 端到端触发 |
|----------|----------|-----------|
| `rotation_crop` | 旋转页 | 是 |
| `unsupported_script` | 竖排页 | 是 |
| `table_fail` | 扩写主样例 3 页 | 是 |
| `formula_fail` | 高密度公式页 | 是 |

## 诚实判定与已知限制

- ratio 1.00：溢出 60.0%（阈值 10%），最大裁切块 0（阈值 2），ToUnicode 100%。
- ratio 1.50：溢出 90.0%（阈值 10%），最大裁切块 0（阈值 2），ToUnicode 100%。

**扩写方向未达标（诚实结论）。** 产品在目标语言比源语言更长时（中译英、英译德等高扩写语言对）会**大面积溢出到页边**，远超设计 ≤10% 门禁。这不是 mock 调参导致的误报——mock 只按字符长度模拟「译文更长」这一真实事实；实际溢出由 S2/S3 机制触发，说明该机制在扩写下会被大量使用。

**这不是静默失败**：设计已要求每页降级报告留痕（规格 §9）。下方「不静默」一节核对结果区是否真的把 `overflow_margin`/`clip` 呈现给用户；若缺口存在，则如实标注。

## 不静默：overflow/clip 的用户可见性核对

- **单文件结果区（阅读器 F4）确实逐页呈现 overflow_margin / clip**：`Sources/AIPDF.Windows/ViewModels/Translation/FullTranslationViewModel.cs:120-142` 的 `ReasonLabels` 含 `overflow_margin`（:123「该页译文超出原文块范围」）与 `clip`（:124「该页存在被裁切的译文」）；`Sources/AIPDF.Windows/Services/FullTranslation.cs:255-352` 的 `ParseOutcome` 读取 `report.pages[].reasons`；渲染在 `Sources/AIPDF.Windows/Views/Translation/FullTranslationPanel.xaml:300-348`（`Text="{Binding LabelText}"` :342）。后端同源文案见 `backend/translate/verify.py:80-101`。
- **回填已优先页边溢出、再裁切，并逐页写显式标记**（非静默）：`backend/translate/reflow.py:406-431`（S2 `overflow_margin` :415-425，S3 `clip` :427-431），且报告 sidecar（`<stem>-降级报告.json`）同目录落盘（`backend/engine.py:940-941`）。因此溢出不会无声丢失。
- **缺口 1 · 无聚合溢出告警**：`report.summary` 只有 `degraded_pages`/`unsearchable_pages`/`total_pages`（`backend/translate/verify.py:315-323`），`overflow_margin` 与 `font_shrink` 一起被并入 `degraded_pages`，用户无法一眼看出「N 页溢出」；设计 §9.3 要求的「F4 顶部显示聚合」未细分到溢出/裁切。
- **缺口 2 · 批量页只显示数量**：批量工具页只呈现降级页**数量**，不呈现逐页原因标签（`Sources/AIPDF.Windows/ViewModels/TranslationBatchViewModel.cs:92,148-152`）；`FullTranslationViewModel.ReasonLabels`/`DescribeReason` 未在批量路径复用。
- **缺口 3 · 引擎结果无机器可读顶层告警**：整篇 translate 结果（`backend/engine.py:1009-1012`）不含顶层 `overflow`/`warning` 字段；消费方必须自行遍历 `report.pages[].reasons` 才能发现溢出。

## 缓解建议

- **实现（建议落点）**：在整篇结果顶层加机器可读聚合，如 `results[].overflow = {pages, count, clipPages, warn}`，由 `report.pages[].reasons` ∩ {`overflow_margin`, `clip`} 计算；任何消费方（含批量页）据此即可非静默。再在 F4 顶部显示「本文件 N 页译文超出原文范围，其中 M 页被裁切」（落实设计 §9.3 的聚合要求）。
- **实现（建议落点）**：批量工具页补齐逐项降级原因（复用 `ReasonLabels`），至少显示溢出/裁切计数，消除「数量可看、原因不可看」的不对称。
- **产品策略**：扩写方向远超 ≤10% 门禁（60–90%），**不建议**在扩写语言对（中→英、英→德等）默认开启 PDF 回填；可在开启前显式提示「该方向排版质量未验证」，或按设计 §未达标降级先提供文本/Markdown 导出。
- **门禁口径**：≤10% 应按**语言对方向**分别定义（收缩方向 vs 扩写方向），不应以单一 EN→ZH 收缩门禁代表全部方向。扩写方向需单独定阈并单独放行。

## 结论

扩写方向门禁的数值必须与收缩方向报告合并阅读。收缩方向 `v11-spike-report.md` 的 PASS 不代表扩写方向安全；本报告用独立提交（`6f6c291`）钉住扩写方向的真实表现。若产品要对外承诺「PDF 回填可用」，必须先落实上述缓解（结果区显式告警 + 溢出/裁切非静默），再决定是否以限制语言对或提高阈值的方式放行。

