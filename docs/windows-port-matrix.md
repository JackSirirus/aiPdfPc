# AIPDF Windows 移植矩阵（终稿）

本文件为 AIPDF 32 个工具向 Windows 平台移植的终稿状态。上游权威来源为 `backend/catalog.py`，工具列表、分组和参数以该文件为准。

状态枚举：`working`（功能正常）/ `degraded`（有已知降级）/ `planned`（尚未验证）。

证据路径以 `.omo/evidence/` 下文件为准；引擎级验证见 `tests/test_engine.py` 全量运行结果。

---

## 工具矩阵

### 整理页面

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| merge | 合并 PDF | working | 无 | `.omo/evidence/task-14-merge.txt` |
| split | 拆分 PDF | working | 无 | `.omo/evidence/task-14-fullsuite.txt` |
| remove | 删除页面 | working | 无 | `.omo/evidence/task-14-fullsuite.txt` |
| extract | 提取页面 | working | 无 | `.omo/evidence/task-14-fullsuite.txt` |
| organize | 排列页面 | working | 无 | `.omo/evidence/task-14-fullsuite.txt` |
| scan | 扫描为 PDF | degraded | 透视校正锁定降级：Apple Vision 纸张边缘检测无 Windows 等价 API，仅图像增强，不做透视校正 | `.omo/evidence/task-14-fullsuite.txt` |

### 优化文件

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| compress | 压缩 PDF | working | 无 | `.omo/evidence/task-15-optimizer.txt` |
| repair | 修复 PDF | working | 无 | `.omo/evidence/task-15-optimizer.txt` |
| ocr | OCR 文字识别 | working | Windows.Media.Ocr 替代 Apple Vision；中文识别需系统语言包 | `.omo/evidence/task-15-optimizer.txt` |

### 格式转换

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| images_to_pdf | 图片转 PDF | degraded | HEIC 输入明确报错：Windows 无原生 HEIC 解码，需用户预转换为 JPG/PNG | `tests/test_engine.py` |
| pdf_to_images | PDF 转图片 | working | 无 | `tests/test_engine.py` |
| word_to_pdf | Word 转 PDF | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | `.omo/evidence/task-16-converter-tests.txt` |
| ppt_to_pdf | PowerPoint 转 PDF | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | `.omo/evidence/task-16-converter-tests.txt` |
| excel_to_pdf | Excel 转 PDF | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | `.omo/evidence/task-16-converter-tests.txt` |
| pdf_to_word | PDF 转 Word | working | 无 | `tests/test_engine.py` |
| pdf_to_ppt | PDF 转 PowerPoint | working | 无 | `tests/test_engine.py` |
| pdf_to_excel | PDF 转 Excel | working | 无 | `tests/test_engine.py` |
| html_to_pdf | HTML 转 PDF | working | Playwright + Chromium 替代 WebKit；需额外安装 Chromium（构建脚本自动完成） | `.omo/evidence/task-16-converter-tests.txt` |
| pdfa | PDF 转 PDF/A | working | LibreOffice 替代方案；未安装 LibreOffice 时提示用户 | `.omo/evidence/task-16-converter-tests.txt` |
| markdown | PDF 转 Markdown | working | 无 | `tests/test_engine.py` |

### 编辑内容

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| rotate | 旋转 PDF | working | 无 | `.omo/evidence/task-17-crop.txt` |
| numbers | 添加页码 | working | 无 | `.omo/evidence/task-17-fullsuite.txt` |
| watermark | 添加水印 | working | 无 | `.omo/evidence/task-17-fullsuite.txt` |
| crop | 裁剪 PDF | working | 无 | `.omo/evidence/task-17-crop.txt` |
| edit | 编辑 PDF | working | 无 | `.omo/evidence/task-17-fullsuite.txt` |
| forms | PDF 表单 | working | 无 | `.omo/evidence/task-17-fullsuite.txt` |

### 安全保护

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| unlock | PDF 解锁 | working | 无 | `.omo/evidence/task-18-roundtrip.txt` |
| protect | PDF 加密 | working | 无 | `.omo/evidence/task-18-security.txt` |
| sign | PDF 签名 | working | 无 | `.omo/evidence/task-18-security.txt` |
| redact | 永久遮盖 | working | 无 | `.omo/evidence/task-18-security.txt` |
| compare | 比较 PDF | working | 无 | `.omo/evidence/task-18-fullsuite.txt` |

### 工作流程

| 工具 ID | 工具名 | Windows 状态 | 降级说明 | 证据 |
|---------|--------|-------------|----------|------|
| workflow | 工作流程 | working | 无 | `.omo/evidence/task-19-batch.txt` |

---

## 已知降级汇总

| 降级项 | 涉及工具 | 原始实现 | Windows 方案 | 影响 |
|--------|---------|----------|-------------|------|
| 扫描透视校正 | scan | Apple Vision 纸张边缘检测+透视变换 | 锁定降级：仅图像增强，不做透视校正 | 扫描照片无法自动纠偏 |
| HEIC 输入 | images_to_pdf | macOS 原生 HEIC 解码 | 明确报错：提示用户预转换格式 | iPhone 直出照片需先转 JPG/PNG |
| URL→PDF | html_to_pdf | Apple WebKit 无头渲染 | Playwright + Chromium 替代 | 需额外下载 Chromium，渲染行为可能与 WebKit 有差异 |

---

## 统计

- **working**: 29/32
- **degraded**: 3/32（scan、images_to_pdf、html_to_pdf）
- **planned**: 0/32

全部 32 个工具已在 Windows 平台验证通过（working 或 degraded），无遗留 planned 状态。

---

## 一致性校验

以下脚本读取 `backend/catalog.json` 与本文件表格行数，断言两者一致（32/32）。

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
