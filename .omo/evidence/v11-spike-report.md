# v1.1a 回填 spike 量化验收报告

> 门禁：`需求总结.md §实现分期与验收门（V1.1a）`；回填规格：`翻译-回填排版规格.md §8/§9`
> 生成时间：2026-09-15 00:35:23（耗时 73.78s）
> 运行：`.venv\Scripts\python.exe tests\spike_translate_reflow.py`
> 样例：全部为 PyMuPDF 内存合成，**不读取任何真实或个人文档**。

## 方法

- 链路：`engine.dispatch({operation: translate})` → extract → chunk → prompt → LLM(mock) → verify → reflow → output。
- LLM：patch `translate.llm._open`，mock 解析 prompt 中 `<untrusted>` 正文并返回确定性伪中文译文；**无任何真实网络请求**。门禁方向为默认 EN→ZH（目标语言默认中文），字数收缩比按源文本确定性取 [0.50, 0.80]（模拟真实波动）。
- 扫描页：patch `translate.extract._vision_ocr_boxes` 注入固定 OCR 框（真实平台 OCR 在合成图像上的结果不稳定，会让度量不可复现；真实 OCR 链路由 `tests/test_translate_extract.py` 覆盖）。
- 溢出页 = 带 `overflow_margin`(S2) 或 `clip`(S3) 的页；裁切块数 = S3 的块级调用次数（计数型报告，去重前计数，并按输出形态数折算成单次渲染口径）；ToUnicode 覆盖率 = 可提取**译文**页 / 应有译文页（扫描光栅页按规格 §8.5 豁免，且以 mock 译文前缀命中判定，避免页脚标记把不可搜索页算成可搜索）。

## 门禁：每样例度量

| 样例 | 总页 | 块数 | 应有译文页 | 可提取译文页 | 溢出页(S2/S3) | 裁切页(S3) | 最大裁切块/页 | 缩字页(S1) | 扫描光栅页 |
|------|-----:|-----:|-----------:|-------------:|--------------:|-----------:|--------------:|-----------:|-----------:|
| 合成双栏 | 3 | 30 | 3 | 3 | — | — | 0 | 1, 2, 3 | — |
| 表格密集 | 3 | 33 | 3 | 3 | — | — | 0 | 1, 2, 3 | — |
| 公式密集 | 3 | 12 | 3 | 3 | — | — | 0 | 1, 2, 3 | — |
| 扫描件 | 1 | 1 | 0 | 0 | — | — | 0 | — | 1 |

## 门禁：回填决策路径覆盖（块级）

| 决策/降级原因 | 触发块次数 |
|---------------|-----------:|
| `font_shrink` | 43 |
| `raster_overlay` | 1 |

## 三项量化门禁

| 指标 | 阈值 | 实测 | 判定 |
|------|------|------|------|
| 溢出页占比（S2/S3） | ≤ 10% | 0/10 = 0.0% | PASS |
| 每页裁切块数 | ≤ 2 | 0 | PASS |
| ToUnicode 覆盖率 | = 100% | 9/9 = 100.0% | PASS |

> 参考：设计原文「溢出页占比（`clip` 标记页 / 总页数）」按 S3-only 口径为 0/10 = 0.0%。本报告门禁采用任务口径（S2∪S3，更严格）。

## VERDICT: PASS

## 灵敏度检查（非门禁）：扩写方向下 S2/S3 是否被触发

门禁样例按默认 EN→ZH 收缩方向构造，实测只触发 S0/S1。为避免 S2/S3 门禁指标形同虚设，下面用更大的 mock 字数比复跑同一测试集，确认溢出/裁切机制确实会触发且计数正确。**此段不参与 VERDICT 判定。**

| CJK 字数比 | 溢出页占比 | 裁切页 | 最大裁切块/页 | ToUnicode 覆盖 |
|-----------:|-----------:|-------:|-------------:|---------------:|
| 1.0 | 6/10 = 60% | 0 | 0 | 9/9 = 100% |
| 1.5 | 9/10 = 90% | 0 | 0 | 9/9 = 100% |

## 计量机制自检（S2/S3 计数）

- S3 `clip` 计数：自检页触发 1 块 → PASS。
- S2 `overflow_margin` 计数：自检页触发 1 块 → PASS。
- 说明：门禁样例在默认 EN→ZH 方向只触发 S0/S1；S2 在灵敏度检查中触发；S3 未在这组样例出现，但由本自检与 `tests/test_translate_reflow.py::test_s3_clips_as_last_resort` 覆盖。

## 附带发现（不改变上述三项门禁结论）

- 原文残留页（§8.3 原文缺失校验）：0 页。
- `search_verify_fail` 页：—。
- 降级报告 schema（每页 `degraded`+`reasons`，§9.2）：PASS；报告按设计为稀疏结构（只列降级页），`summary.total_pages` 覆盖全文。
- **门禁适用边界（诚实披露）**：4 个门禁样例在默认 EN→ZH 收缩下回填只走到 S0/S1（缩字），S2/S3 未在门禁样例中触发；上表灵敏度检查证明扩写时会触发且计数正确。若产品要覆盖扩写/极密排版，T24 应补一个扩写方向样例再复核。
- **原因覆盖缺口**：`formula_fail`、`table_fail`、`rotation_crop`、`unsupported_script`、`reassembly_mismatch`、`segment_split` 已登记在 `verify.DEGRADATION_REASONS`，但当前 reflow/extract 链路**从不写出**这些原因（公式/表格/旋转/RTL 未做结构化检测）。此为 T8 的已知能力边界，不影响本 spike 的三项排版门禁，但 T20/T24 应如实披露或补齐。
- 样例 `scanned` 仅 1 页且为光栅叠加（豁免可搜索性），其 `raster_overlay` 路径已由注入 OCR 触发；未验证真实 OCR 识别质量。
- 源文件只读校验：全部样例源文件 SHA-256 未变。

## 结论与 T20 路由

回填 mono/dual **通过** V1.1a 量化门禁（默认 EN→ZH 方向），**允许 T20 并入回填产物**（mono + dual 新标签、可搜索校验、每页降级报告）。

> 边界：该 PASS 以默认 EN→ZH 收缩方向为准；扩写/极密排版方向的溢出率见上方灵敏度检查，T24 应补测。

## 逐页降级明细（门禁测试集）

### 合成双栏（double_column）

- 第 1 页：`font_shrink`
- 第 2 页：`font_shrink`
- 第 3 页：`font_shrink`

### 表格密集（table_dense）

- 第 1 页：`font_shrink`
- 第 2 页：`font_shrink`
- 第 3 页：`font_shrink`

### 公式密集（formula_dense）

- 第 1 页：`font_shrink`
- 第 2 页：`font_shrink`
- 第 3 页：`font_shrink`

### 扫描件（scanned）

- 第 1 页：`raster_overlay`

