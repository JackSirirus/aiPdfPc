# AIPDF Windows 移植矩阵（终稿）

本文件为 AIPDF 33 个工具向 Windows 平台移植的终稿状态（v1.1 新增 PDF 翻译，翻译工具尚未纳入本矩阵验证）。上游权威来源为 `backend/catalog.py`，工具列表、分组和参数以该文件为准。

状态枚举：`working`（功能正常）/ `degraded`（有已知降级）/ `planned`（尚未验证）。

证据基于 2026-09-08 收口验证（HEAD bfd4ffc，589.27 MB 自包含发行目录），引擎级验证见 `tests/test_engine.py` 全量运行结果。

---

## 工具矩阵

### 整理页面

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| merge | 合并 PDF | working | 无 | 收口 QA merge E2E（2026-09-08） |
| split | 拆分 PDF | working | 无 | 收口 QA 全量引擎测试（2026-09-08） |
| remove | 删除页面 | working | 无 | 收口 QA 全量引擎测试（2026-09-08） |
| extract | 提取页面 | working | 无 | 收口 QA 全量引擎测试（2026-09-08） |
| organize | 排列页面 | working | 无 | 收口 QA 全量引擎测试（2026-09-08） |
| scan | 扫描为 PDF | degraded | 透视校正锁定降级：Apple Vision 纸张边缘检测无 Windows 等价 API，仅图像增强，不做透视校正 | 收口 QA 全量引擎测试（2026-09-08） |

### 优化文件

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| compress | 压缩 PDF | working | 无 | 收口 QA 压缩验证 + 16/16 页面切换（2026-09-08） |
| repair | 修复 PDF | working | 无 | 收口 QA 修复验证（2026-09-08） |
| ocr | OCR 文字识别 | working | Windows.Media.Ocr 替代 Apple Vision；中文识别需系统语言包 | 收口 QA OCR E2E 实跑 PASS（2026-09-08） |

### 格式转换

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| images_to_pdf | 图片转 PDF | degraded | HEIC 输入明确报错：Windows 无原生 HEIC 解码，需用户预转换为 JPG/PNG | `tests/test_engine.py` |
| pdf_to_images | PDF 转图片 | working | 无 | `tests/test_engine.py` |
| word_to_pdf | Word 转 PDF | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | 收口 QA 转换器验证 + 页面切换（2026-09-08） |
| ppt_to_pdf | PowerPoint 转 PDF | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | 收口 QA 转换器验证 + 页面切换（2026-09-08） |
| excel_to_pdf | Excel 转 PDF | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | 收口 QA 转换器验证 + 页面切换（2026-09-08） |
| pdf_to_word | PDF 转 Word | working | 无 | `tests/test_engine.py` |
| pdf_to_ppt | PDF 转 PowerPoint | working | 无 | `tests/test_engine.py` |
| pdf_to_excel | PDF 转 Excel | working | 无 | `tests/test_engine.py` |
| html_to_pdf | HTML 转 PDF | working | Playwright + Chromium 替代 WebKit；需额外安装 Chromium（构建脚本自动完成） | 收口 QA 转换器验证 + 页面切换（2026-09-08） |
| pdfa | PDF 转 PDF/A | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | 收口 QA 转换器验证 + 页面切换（2026-09-08） |
| markdown | PDF 转 Markdown | working | 无 | `tests/test_engine.py` |

### 编辑内容

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| rotate | 旋转 PDF | working | 无 | 收口 QA 裁剪/旋转验证（2026-09-08） |
| numbers | 添加页码 | working | 无 | 收口 QA 编辑器验证（2026-09-08） |
| watermark | 添加水印 | working | 无 | 收口 QA 编辑器验证（2026-09-08） |
| crop | 裁剪 PDF | working | 无 | 收口 QA 裁剪/旋转验证（2026-09-08） |
| edit | 编辑 PDF | working | 无 | 收口 QA 编辑器验证 + 页面切换（2026-09-08） |
| forms | PDF 表单 | working | 无 | 收口 QA 编辑器验证（2026-09-08） |

### 安全保护

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| unlock | PDF 解锁 | working | 无 | 收口 QA 解锁验证（2026-09-08） |
| protect | PDF 加密 | working | 无 | 收口 QA 安全套件验证 + 页面切换（2026-09-08） |
| sign | PDF 签名 | working | 无 | 收口 QA 安全套件验证（2026-09-08） |
| redact | 永久遮盖 | working | 无 | 收口 QA 安全套件验证（2026-09-08） |
| compare | 比较 PDF | working | 无 | 收口 QA 比较验证（2026-09-08） |

### 工作流程

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| workflow | 工作流程 | working | 无 | 收口 QA 工作流程验证 + 页面切换（2026-09-08） |

### 翻译（v1.1 新增）

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| translate_batch | 批量翻译 | planned | v1.1 新增，尚未验证 | v1.1 设计阶段 |

> **T23 依赖说明**：翻译 LLM 客户端（`backend/translate/llm.py`）使用 Python 标准库 `urllib.request` 发起 HTTPS 请求，不引入任何新的第三方依赖。

---

## 已知降级汇总

| 降级项 | 涉及工具 | 原始实现 | Windows 方案 | 影响 |
|--------|---------|----------|-------------|------|
| 扫描透视校正 | scan | Apple Vision 纸张边缘检测+透视变换 | 锁定降级：仅图像增强，不做透视校正 | 扫描照片无法自动纠偏 |
| HEIC 输入 | images_to_pdf | macOS 原生 HEIC 解码 | 明确报错：提示用户预转换格式 | iPhone 直出照片需先转 JPG/PNG |
| URL→PDF | html_to_pdf | Apple WebKit 无头渲染 | Playwright + Chromium 替代 | 需额外下载 Chromium，渲染行为可能与 WebKit 有差异 |

---

## 统计

- **working**: 29/33
- **degraded**: 3/33（scan、images_to_pdf、html_to_pdf）
- **planned**: 1/33（translate_batch，v1.1 新增）

全部 33 个工具已在 Windows 平台验证通过或纳入规划（working / degraded / planned），无遗留未规划工具。

---

## 一致性校验

以下脚本读取 `backend/catalog.json` 与本文件表格行数，断言两者一致（33/33）。

```python
"""一致性校验：catalog JSON 条目数 vs 矩阵 Markdown 行数。"""
import json, re, sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent

# 读取 catalog JSON（上游权威来源）
catalog_path = root / "backend" / "catalog.json"
try:
    data = json.loads(catalog_path.read_text(encoding="utf-8"))
except UnicodeDecodeError:
    data = json.loads(catalog_path.read_text(encoding="gbk"))
catalog_count = len(data)
catalog_ids = sorted(t["id"] for t in data)

# 读取矩阵 Markdown，提取表格行中的工具 ID
matrix_path = root / "docs" / "windows-port-matrix.md"
text = matrix_path.read_text(encoding="utf-8")
# 匹配表格行：以 | 开头，第二个单元格为工具 ID（字母/下划线）
tool_ids = []
for line in text.splitlines():
    line = line.strip()
    if not line.startswith("|") or line.startswith("|--") or line.startswith("| 工具"):
        continue
    cells = [c.strip() for c in line.split("|")]
    # cells[0] 和 cells[-1] 为空（| 分割产生）
    if len(cells) >= 4:
        tool_id = cells[1]
        if re.match(r"^[a-z_]+$", tool_id):
            tool_ids.append(tool_id)
matrix_count = len(tool_ids)
matrix_ids = sorted(tool_ids)

print(f"catalog.json: {catalog_count} 条工具")
print(f"矩阵 Markdown: {matrix_count} 行工具条目")

if catalog_count != matrix_count:
    print(f"❌ 数量不一致: catalog={catalog_count}, matrix={matrix_count}")
    missing_in_matrix = set(catalog_ids) - set(matrix_ids)
    extra_in_matrix = set(matrix_ids) - set(catalog_ids)
    if missing_in_matrix:
        print(f"   矩阵缺少: {missing_in_matrix}")
    if extra_in_matrix:
        print(f"   矩阵多余: {extra_in_matrix}")
    sys.exit(1)

if catalog_ids != matrix_ids:
    print("❌ 工具 ID 列表不一致")
    print(f"   catalog: {catalog_ids}")
    print(f"   matrix:  {matrix_ids}")
    sys.exit(1)

print(f"✅ 通过: {catalog_count}/{catalog_count} 工具一致")
```

运行方式：

```sh
.venv\Scripts\python.exe docs/windows-port-matrix.py
```

或直接在本文件所在目录执行内联校验：

```sh
.venv\Scripts\python.exe -c "exec(open('docs/windows-port-matrix.md', encoding='utf-8').read().split('```python')[1].split('```')[0])"
```
