# Windows 手动冒烟清单

以下为可选的手动冒烟测试项，用于在自动测试之外快速验证核心功能。所有项目均为**可选**，不作为验收门槛。

> 本清单覆盖构建、启动、基本操作和已知降级场景。每项标注预期结果和判断标准。

---

## 构建与启动

### 1. 构建脚本正常完成（可选）

运行 `powershell -ExecutionPolicy Bypass -File scripts\build.ps1`，确认：
- 六阶段全部通过，无红色错误
- `dist/aipdf-windows/` 目录生成且包含 app/engine/helper 子目录
- 构建脚本退出码为 0

### 2. WPF 应用正常启动（可选）

运行 `dist/aipdf-windows/app/AIPDF.Windows.exe`，确认：
- 窗口正常显示，侧栏列出 32 个工具（6 组）
- 无 .NET Desktop Runtime 缺失弹窗
- 状态栏显示"就绪"

### 3. OCR 助手自检通过（可选）

在 PowerShell 中运行：

```powershell
dist\appdf-windows\helper\WindowsOcrHelper.exe --selftest
```

确认输出 JSON 中 `"ok": true`，且识别文本含 "AIPDF TEST"。

---

## 核心功能

### 4. 合并两份 PDF（可选）

准备两份单页 PDF，通过引擎合并：

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'merge','files':['a.pdf','b.pdf'],'options':{},'outputDir':'out'}))"
```

确认输出 PDF 为 2 页，页序正确。

### 5. 拆分 PDF（可选）

准备一份 3 页 PDF，拆分为单独页面：

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'split','files':['multi.pdf'],'options':{'pages':'1;3'},'outputDir':'out'}))"
```

确认输出 2 份 PDF，分别含第 1 页和第 3 页。

### 6. 加密与解锁往返（可选）

加密一份 PDF 后用密码解锁：

```powershell
# 加密
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'protect','files':['plain.pdf'],'options':{'password':'test123'},'outputDir':'out'}))"

# 解锁
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'unlock','files':['encrypted.pdf'],'options':{'password':'test123'},'outputDir':'out'}))"
```

确认加密后文件无法直接读取，解锁后可正常提取文本。

### 7. OCR 文字识别（可选）

准备一份扫描图片（含英文或中文文字），运行 OCR：

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'ocr','files':['scan.png'],'options':{},'outputDir':'out'}))"
```

确认输出 PDF 含可搜索文字层。中文识别需系统已安装中文语言包。

### 8. URL 转 PDF（可选）

将网页转换为 PDF：

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'html_to_pdf','files':[],'options':{'url':'https://example.com'},'outputDir':'out'}))"
```

确认输出 PDF 含网页内容，页数 ≥ 1。

---

## 已知降级验证

### 9. HEIC 输入报错（可选）

准备一份 .heic 图片文件，尝试转 PDF：

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'images_to_pdf','files':['photo.heic'],'options':{},'outputDir':'out'}))"
```

确认引擎返回错误信息，提示需转换格式。

### 10. 扫描无透视校正（可选）

准备一张倾斜拍摄的文档照片，运行 scan 工具。确认输出图像有增强但无透视校正（文档边缘仍倾斜）。

---

## WPF 界面

### 11. 工具页导航（可选）

在 WPF 界面中点击侧栏不同工具，确认：
- 右侧区域切换到对应工具页
- 状态栏更新为当前工具名称
- 拖拽区域正常显示

### 12. 文件拖拽添加（可选）

将 PDF 文件拖拽到工具页的拖拽区域，确认文件出现在文件列表中。

### 13. PDF 预览与框选（可选）

在编辑工具（如裁剪、签名）中打开 PDF，确认：
- 页面正常渲染（非白屏）
- Ctrl+滚轮可缩放
- PageUp/PageDown 翻页
- 框选区域显示橙色虚线框

---

## 环境依赖

### 14. LibreOffice 转换（可选）

如已安装 LibreOffice，准备一份 .docx 文件，转换为 PDF：

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.dispatch({'operation':'word_to_pdf','files':['doc.docx'],'options':{},'outputDir':'out'}))"
```

确认输出 PDF 含文档内容。未安装 LibreOffice 时应提示用户。

### 15. 引擎健康检查（可选）

```powershell
.venv\Scripts\python.exe -c "import backend.engine as e; print(e.health())"
```

确认返回 JSON 中 `ok` 为 true，`office` 和 `vision` 字段反映实际可用性。
