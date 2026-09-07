# AIPDF Windows 移植矩阵

本文件为 AIPDF 32 个工具向 Windows 平台移植的初始蓝图。上游权威来源为 `backend/catalog.py`，工具列表、分组和参数以该文件为准。状态枚举：`planned`（当前阶段）/ `working` / `degraded`。

---

## 工具矩阵

### 整理页面

| 工具 ID | 工具名 | 所属组 | 引擎入口 | Windows 状态 | 依赖 | 降级说明 |
|---------|--------|--------|----------|-------------|------|----------|
| merge | 合并 PDF | 整理页面 | engine.py:merge | planned | PyMuPDF | 无 |
| split | 拆分 PDF | 整理页面 | engine.py:split | planned | PyMuPDF | 无 |
| remove | 删除页面 | 整理页面 | engine.py:remove | planned | PyMuPDF | 无 |
| extract | 提取页面 | 整理页面 | engine.py:extract | planned | PyMuPDF | 无 |
| organize | 排列页面 | 整理页面 | engine.py:organize | planned | PyMuPDF | 无 |
| scan | 扫描为 PDF | 整理页面 | engine.py:scan | planned | vision | 纸张识别+透视校正锁定降级：Apple Vision 纸张边缘检测与透视校正无 Windows 等价 API，锁定为简化模式（仅增强，不做透视校正） |

### 优化文件

| 工具 ID | 工具名 | 所属组 | 引擎入口 | Windows 状态 | 依赖 | 降级说明 |
|---------|--------|--------|----------|-------------|------|----------|
| compress | 压缩 PDF | 优化文件 | engine.py:compress | planned | PyMuPDF | 无 |
| repair | 修复 PDF | 优化文件 | engine.py:repair | planned | PyMuPDF | 无 |
| ocr | OCR 文字识别 | 优化文件 | engine.py:ocr | planned | vision | Windows.Media.Ocr 替代 Apple Vision；中文识别需系统语言包 |

### 格式转换

| 工具 ID | 工具名 | 所属组 | 引擎入口 | Windows 状态 | 依赖 | 降级说明 |
|---------|--------|--------|----------|-------------|------|----------|
| images_to_pdf | 图片转 PDF | 格式转换 | engine.py:images_to_pdf | planned | PyMuPDF | HEIC 输入明确报错：Windows 无原生 HEIC 解码，需用户预转换为 JPG/PNG |
| pdf_to_images | PDF 转图片 | 格式转换 | engine.py:pdf_to_images | planned | PyMuPDF | 无 |
| word_to_pdf | Word 转 PDF | 格式转换 | engine.py:word_to_pdf | planned | office | LibreOffice 替代方案 |
| ppt_to_pdf | PowerPoint 转 PDF | 格式转换 | engine.py:ppt_to_pdf | planned | office | LibreOffice 替代方案 |
| excel_to_pdf | Excel 转 PDF | 格式转换 | engine.py:excel_to_pdf | planned | office | LibreOffice 替代方案 |
| pdf_to_word | PDF 转 Word | 格式转换 | engine.py:pdf_to_word | planned | PyMuPDF | 无 |
| pdf_to_ppt | PDF 转 PowerPoint | 格式转换 | engine.py:pdf_to_ppt | planned | PyMuPDF | 无 |
| pdf_to_excel | PDF 转 Excel | 格式转换 | engine.py:pdf_to_excel | planned | PyMuPDF | 无 |
| html_to_pdf | HTML 转 PDF | 格式转换 | engine.py:html_to_pdf | working | PyMuPDF（本地 HTML）/ Playwright（网址） | URL→PDF 方案：Playwright 替代 Apple WebKit 渲染，需额外安装 chromium（`playwright install chromium`）；每任务全新无 Cookie context，超时 45 秒 |
| pdfa | PDF 转 PDF/A | 格式转换 | engine.py:pdfa | planned | office | LibreOffice 替代方案 |
| markdown | PDF 转 Markdown | 格式转换 | engine.py:markdown | planned | PyMuPDF | 无 |

### 编辑内容

| 工具 ID | 工具名 | 所属组 | 引擎入口 | Windows 状态 | 依赖 | 降级说明 |
|---------|--------|--------|----------|-------------|------|----------|
| rotate | 旋转 PDF | 编辑内容 | engine.py:rotate | planned | PyMuPDF | 无 |
| numbers | 添加页码 | 编辑内容 | engine.py:numbers | planned | PyMuPDF | 无 |
| watermark | 添加水印 | 编辑内容 | engine.py:watermark | planned | PyMuPDF | 无 |
| crop | 裁剪 PDF | 编辑内容 | engine.py:crop | planned | PyMuPDF | 无 |
| edit | 编辑 PDF | 编辑内容 | engine.py:edit | planned | PyMuPDF | 无 |
| forms | PDF 表单 | 编辑内容 | engine.py:forms | planned | PyMuPDF | 无 |

### 安全保护

| 工具 ID | 工具名 | 所属组 | 引擎入口 | Windows 状态 | 依赖 | 降级说明 |
|---------|--------|--------|----------|-------------|------|----------|
| unlock | PDF 解锁 | 安全保护 | engine.py:unlock | planned | PyMuPDF | 无 |
| protect | PDF 加密 | 安全保护 | engine.py:protect | planned | PyMuPDF | 无 |
| sign | PDF 签名 | 安全保护 | engine.py:sign | planned | PyMuPDF | 无 |
| redact | 永久遮盖 | 安全保护 | engine.py:redact | planned | PyMuPDF | 无 |
| compare | 比较 PDF | 安全保护 | engine.py:compare | planned | PyMuPDF | 无 |

### 工作流程

| 工具 ID | 工具名 | 所属组 | 引擎入口 | Windows 状态 | 依赖 | 降级说明 |
|---------|--------|--------|----------|-------------|------|----------|
| workflow | 工作流程 | 工作流程 | engine.py:workflow | planned | PyMuPDF | 无 |

---

## 已知降级汇总

| 降级项 | 涉及工具 | 原始实现 | Windows 方案 | 影响 |
|--------|---------|----------|-------------|------|
| 扫描透视校正 | scan | Apple Vision 纸张边缘检测+透视变换 | 锁定降级：仅图像增强，不做透视校正 | 扫描照片无法自动纠偏 |
| HEIC 输入 | images_to_pdf, scan | macOS 原生 HEIC 解码 | 明确报错：提示用户预转换格式 | iPhone 直出照片需先转 JPG/PNG |
| URL→PDF | html_to_pdf | Apple WebKit 无头渲染 | Playwright + Chromium 替代 | 需额外下载 Chromium，渲染行为可能与 WebKit 有差异 |

---

## 一致性校验

以下脚本读取 `backend/catalog.json` 与本文件表格行数，断言两者一致（32/32）。

```python
"""一致性校验：catalog JSON 条目数 vs 矩阵 Markdown 表格行数。"""
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
