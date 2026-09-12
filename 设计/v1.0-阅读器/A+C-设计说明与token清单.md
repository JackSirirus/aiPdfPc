# A+C 设计说明与 Token 清单

> 方向：**A+C** = 忠实补全**方案A**（克制的三栏工具式阅读器）全部未落地细节，同时以 **Paper / editorial** 语气暖化中性色、引入衬线展示标题与纸张阅读画布。
> 日期：2026-09-12 ｜ 载体：Windows WPF（`Sources/AIPDF.Windows`）
> 配套设计稿：[`pdf-reader-设计稿-A+C.html`](./pdf-reader-设计稿-A+C.html)（自包含、零外部资源）
> 基线：[`pdf-reader-设计稿.html`](./pdf-reader-设计稿.html) v1.1 ｜ [`pdf-reader-全状态设计参考.html`](./pdf-reader-全状态设计参考.html) v1.0 ｜ [`需求总结.md`](./需求总结.md) v1.2

---

## 0. 范围与硬约束

- **不改信息架构**：顶栏 44px、左栏 200px（四面板）、阅读区 62% 页宽、右抽屉 240px、状态栏 32px、字号阶梯 26/15/13/12/11、圆角 6–8px 全部保留。
- **不改 token 键名**：全仓引用 `Brush.*` / `Brush.Dark.*`，只改**值**与**新增附加键**。既有键名一个都不改。
- **任何新增 token 必须亮/暗成对**，并同步登记进 `ThemeTokens.All`（见 §7）。
- **不内嵌字体**；衬线走系统字体回退链（见 §5）。
- **本文件与配套 HTML 均为设计产物**：未改动 `Sources/`、`tests/` 或 V3 验收记录；也未运行 dotnet / npm / 浏览器。

### 设计 token 的唯一来源（事实）

| 事实 | 位置 |
|---|---|
| 亮色 token 定义（29 项） | `Sources/AIPDF.Windows/App.xaml:15-48` |
| 暗色 token 定义（29 项） | `Sources/AIPDF.Windows/App.xaml:55-84` |
| 共享样式（PageTitle/SectionTitle/Field*/Button…） | `Sources/AIPDF.Windows/App.xaml:87-143` |
| 运行时 token 枚举 | `Sources/AIPDF.Windows/Services/ThemeManager.cs:37-48`（`ThemeTokens.All`） |
| token 审计（亮/暗完整性 + 硬编码白名单） | `Sources/AIPDF.Windows/AIPDF.Windows.Tests/Services/ThemeManagerTests.cs` |

---

## 1. Token 对照表（亮 / 暗）

### 1.1 CSS 变量名 → WPF 键 1:1 映射

设计稿 HTML 必须保留既有变量名，以便与 WPF token 一一对应：

| HTML 变量 | WPF 键 |
|---|---|
| `--win` | `Brush.Window` |
| `--side` | `Brush.Sidebar` |
| `--hov` | `Brush.Hover` |
| `--bd` | `Brush.Border` |
| `--t1` | `Brush.TextPrimary` |
| `--t2` | `Brush.TextSecondary` |
| `--t3` | `Brush.TextTertiary` |
| `--acc` | `Brush.Accent` |
| `--accsoft` | `Brush.AccentSoft` |
| `--acct` | `Brush.AccentText` |
| `--readbg` | `Brush.PreviewBg`（阅读画布；**注意**：当前 WPF 阅读区实际引用 `Brush.Sidebar`，改造需切换，见 §4 Phase 3） |
| `--hl` | 文档内容色，**无主题 token**（批注高亮色板，属内容层） |
| `--ok` | `Brush.Success` |
| `--err` | `Brush.Error` |
| `--mask` | **无对应 token（新增候选）** |
| `--bdstrong` | `Brush.BorderStrong` |
| `--acchov` | `Brush.AccentHover` |
| `--accpress` | `Brush.AccentPressed` |
| `--field` | `Brush.FieldBackground` |
| `--surf` | `Brush.Surface` |
| `--hlsearch` | `Brush.SearchHighlight` |
| `--hlsearchbd` | `Brush.SearchHighlightBorder` |
| `--oksoft` | `Brush.SuccessSoft` |
| `--danger` | `Brush.Danger` |
| `--dangersoft` | `Brush.DangerSoft` |
| `--neutralsoft` | `Brush.NeutralSoft` |
| `--warnbg` | `Brush.ConverterWarnBackground`（语义偏窄，见 §6） |
| `--warntext` | `Brush.ConverterWarnText` |
| `--toolbar` | `Brush.Toolbar` |
| `--drop` | `Brush.DropZoneBackground` |
| `--drophov` | `Brush.DropZoneHoverBackground` |

### 1.2 完整 29 token 表：CURRENT（现 App.xaml）→ PROPOSED（A+C）

**亮色（`App.xaml:15-48`）**

| WPF 键（`Brush.*`） | CURRENT | PROPOSED A+C | 说明 |
|---|---|---|---|
| `Brush.Window` | `#FFFFFF` | `#FBFAF7` | 暖白窗底（Paper 核心） |
| `Brush.Sidebar` | `#F6F6F8` | `#F4F1EA` | 暖调侧栏 |
| `Brush.Hover` | `#EBEBEF` | `#ECE7DD` | 暖 hover |
| `Brush.Border` | `#E4E4E9` | `#E4DDCF` | 暖边框 |
| `Brush.BorderStrong` | `#C9C9D1` | `#C9C0AE` | 暖强边框（虚线/拖拽槽） |
| `Brush.TextPrimary` | `#1D1D1F` | `#2A251E` | 暖墨 |
| `Brush.TextSecondary` | `#6E6E73` | `#6E6659` | 暖次要文字 |
| `Brush.TextTertiary` | `#9A9AA0` | `#9C9384` | 暖三级文字 |
| `Brush.Accent` | `#3159D9` | `#3159D9` | **保留蓝**（改为「参与交互」是行为层，非改值） |
| `Brush.AccentHover` | `#2749AE` | `#2749AE` | 保留 |
| `Brush.AccentPressed` | `#1F3B8F` | `#1F3B8F` | 保留 |
| `Brush.AccentSoft` | `#E4EAFB` | `#E7EBF8` | 暖冷中和的强调浅底 |
| `Brush.AccentText` | `#2A4CBB` | `#2A4CBB` | 保留 |
| `Brush.Error` | `#C4321F` | `#C4321F` | 保留 |
| `Brush.FieldBackground` | `#FFFFFF` | `#FFFDF8` | 暖白输入底 |
| `Brush.Surface` | `#FAFAFC` | `#F7F4EC` | 暖表面 |
| `Brush.SearchHighlight` | `#66FFD84D` | `#66FFD84D` | **内容标记，亮暗一致，不改**（琥珀） |
| `Brush.SearchHighlightBorder` | `#CCFFB300` | `#CCFFB300` | 同上，不改 |
| `Brush.Success` | `#1F7A3D` | `#1F7A3D` | 保留（保存胶囊绿点） |
| `Brush.SuccessSoft` | `#E4F3E9` | `#E4F3E9` | 保留 |
| `Brush.Danger` | `#B3382E` | `#B3382E` | 保留 |
| `Brush.DangerSoft` | `#FBEAE8` | `#FBEAE8` | 保留 |
| `Brush.NeutralSoft` | `#F0F0F3` | `#EFEAE0` | 暖中性浅底 |
| `Brush.ConverterWarnBackground` | `#FCF3E3` | `#FBF2DF` | 微暖（并为通用警示复用，见 §6） |
| `Brush.ConverterWarnText` | `#8A5300` | `#8A5300` | 保留 |
| **`Brush.PreviewBg`（阅读画布）** | `#EDEFF2` | **`#ECE7DC`** | A+C 关键：暖纸画布 |
| `Brush.Toolbar` | `#F6F6F8` | `#F4F1EA` | 暖工具条 |
| `Brush.DropZoneBackground` | `#FBFBFC` | `#FCFAF4` | 暖拖拽槽 |
| `Brush.DropZoneHoverBackground` | `#F0F4FE` | `#EEF1FA` | 暖拖拽槽悬停 |

**暗色（`App.xaml:55-84`）** — 暖黑，而非纯灰

| WPF 键（`Brush.Dark.*`） | CURRENT | PROPOSED A+C | 说明 |
|---|---|---|---|
| `Brush.Dark.Window` | `#1B1C1E` | `#1C1A17` | 墨/暖黑窗底 |
| `Brush.Dark.Sidebar` | `#212225` | `#23201A` | 暖暗侧栏 |
| `Brush.Dark.Hover` | `#2E2F33` | `#302C24` | 暖暗 hover |
| `Brush.Dark.Border` | `#3A3B40` | `#3A3529` | 暖暗边框 |
| `Brush.Dark.BorderStrong` | `#4A4B52` | `#4C4636` | 暖暗强边框 |
| `Brush.Dark.TextPrimary` | `#E8E9EC` | `#EDE7DA` | 暖白墨 |
| `Brush.Dark.TextSecondary` | `#B4B6BC` | `#B3AA99` | 暖次文字 |
| `Brush.Dark.TextTertiary` | `#85878E` | `#877F6F` | 暖三级文字 |
| `Brush.Dark.Accent` | `#5B82F0` | **`#7E9CD8`** | **去饱和暖蓝** |
| `Brush.Dark.AccentHover` | `#6E92F5` | `#90ACDF` | 暖蓝悬停 |
| `Brush.Dark.AccentPressed` | `#4A6FD8` | `#6B88C4` | 暖蓝按下 |
| `Brush.Dark.AccentSoft` | `#25304D` | `#272F41` | 暖暗强调浅底 |
| `Brush.Dark.AccentText` | `#9DB8FA` | `#A9C0EA` | 暖蓝文字 |
| `Brush.Dark.Error` | `#F1776A` | `#E88B7D` | 暖化错误 |
| `Brush.Dark.FieldBackground` | `#26272B` | `#262219` | 暖暗输入底 |
| `Brush.Dark.Surface` | `#232427` | `#201D17` | 暖暗表面 |
| `Brush.Dark.SearchHighlight` | `#66FFD84D` | `#66FFD84D` | 不改（内容标记） |
| `Brush.Dark.SearchHighlightBorder` | `#CCFFB300` | `#CCFFB300` | 不改 |
| `Brush.Dark.Success` | `#66C88A` | `#7FC08E` | 暖化成功 |
| `Brush.Dark.SuccessSoft` | `#1E3327` | `#1F2C22` | 暖化成功浅底 |
| `Brush.Dark.Danger` | `#F08A80` | `#E88B7D` | 暖化危险 |
| `Brush.Dark.DangerSoft` | `#3A2422` | `#38231F` | 暖化危险浅底 |
| `Brush.Dark.NeutralSoft` | `#2A2B2F` | `#2C2820` | 暖中性浅底 |
| `Brush.Dark.ConverterWarnBackground` | `#3A2F1C` | `#3A2F1C` | 保留 |
| `Brush.Dark.ConverterWarnText` | `#E6B36A` | `#E6B36A` | 保留 |
| **`Brush.Dark.PreviewBg`（阅读画布）** | `#17181A` | **`#26221C`** | A+C 关键：暖纸暗画布 |
| `Brush.Dark.Toolbar` | `#212225` | `#23201A` | 暖暗工具条 |
| `Brush.Dark.DropZoneBackground` | `#232427` | `#23201A` | 暖暗拖拽槽 |
| `Brush.Dark.DropZoneHoverBackground` | `#26314D` | `#2C3346` | 暖暗拖拽槽悬停 |

> 说明：`Brush.Dark.ConverterWarnBackground/Text` 与 `Brush.Dark.SearchHighlight*` 保持现值，是刻意为控范围的「不动项」。

### 1.3 纯 CSS / WPF 装饰量（无 token 语义）

| 名称 | A+C 值 | 归属 |
|---|---|---|
| 页影 | `0 1px 2px rgba(74,58,36,.08), 0 2px 8px rgba(74,58,36,.06)` | WPF：`DropShadowEffect` 参数，不是 Brush |
| 弹层影 | `0 4px 18px rgba(60,46,26,.16)` | 同上 |
| 模态影 | `0 10px 40px rgba(48,36,20,.28)` | 同上 |
| 纸纹 | 内联 SVG `feTurbulence` 噪点，`opacity .28`，`mix-blend-mode:multiply`（暗色 `screen .05`） | 装饰层，可选；见 §5 |

---

## 2. 字号与字体

### 2.1 字号阶梯（不变）

沿用 `App.xaml:86` 注释与 `MainWindow`：**26 页标题 / 15 区块标题 / 13 正文与字段 / 12 辅助 / 11 脚注**。A+C 不新增字号。

### 2.2 字体族

| 用途 | 字体栈 | WPF 落点 |
|---|---|---|
| UI + 正文 | `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei, system-ui, sans-serif` | `MainWindow.xaml:10` 根 Window（子视图继承） |
| 代码构建对话框 | 同上 | `SettingsDialog.cs:50`、`AboutDialog.cs:37` |
| 图标字形 | `Segoe MDL2 Assets` | 保持不动（多处 glyph run） |
| **衬线展示标题** | `"Source Han Serif SC","Noto Serif SC","Songti SC","SimSun",Georgia,"Times New Roman",serif` | 新增 `FontFamily` 资源 / 样式 setter |

**衬线仅用于**：起始页 hero 标题、页面/文档标题（26）、区块标题 `.h2`（15）、右抽屉与面板 section 标题、工具页标题、模态标题。**正文、表单、按钮、状态栏一律 sans**。

> WPF 的 `FontFamily` 支持逗号回退链，可直接写 `"Source Han Serif SC, Noto Serif SC, SimSun, Georgia"`。注意字体族名中含空格时 WPF 的解析规则：整串作为回退链需谨慎；建议用单个 `FontFamily` 字符串逗号分隔，或分三段 `FontFamily` 回退测试。

---

## 3. 动效规范

统一：**120–160ms，`ease-out`（CSS `cubic-bezier(.22,.61,.36,1)`，本稿 `--dur:140ms`）**，尊重 `prefers-reduced-motion`。

| 场景 | 属性 | 时长 | 备注 |
|---|---|---|---|
| 标签 hover / 激活 | background、color、box-shadow（2px 下划线） | 140ms | 不用描边 |
| 左栏面板切换 / 导航项 | background、color | 140ms | 面板内容可 120ms 淡入 |
| 缩略图选中 | outline-color、box-shadow | 140ms | 2px accent 描边 |
| 保存胶囊状态 morph | background、color | 160ms | 四态切换；保存中呼吸 1.1s |
| 右抽屉展开 / 收起 | opacity + translateX 8px | 160ms | WPF 用 Storyboard |
| 锁定遮罩 | opacity fade | 150–170ms | 内卡片 rise 8px |
| 卡片 hover 抬升 | translateY(-2px) + shadow | 140ms | 起始页 recent / tool tile |
| Toast | translateY(8px) + fade | 150ms | 右下角 |
| 骨架屏 | 渐变位移 | 1.4s 循环 | 非交互动效 |

WPF 对应：`DoubleAnimation` / `ColorAnimation`（`Duration=0:0:0.14`，`EasingFunction=QuadraticEase EaseOut`）；Hover/Selected 建议用 `VisualStateManager` 或 `Trigger.EnterActions`。

---

## 4. 分阶段实施计划（先做哪些屏 + 精确文件）

> 原则：**先换值（Phase 0），再换字体/形态（Phase 1–2），最后做纸感（Phase 3）与工具页收口（Phase 4）**。Phase 0 即可让全应用「暖」起来，风险最低。

### Phase 0 — Token 换值（零结构改动，最高优先）
只改 `App.xaml` 的**值**，键名与数量不变。
- `Sources/AIPDF.Windows/App.xaml:15-48`（亮色 29 键）
- `Sources/AIPDF.Windows/App.xaml:55-84`（暗色 29 键）
- 重点：`Brush.Window / Sidebar / Hover / Border / BorderStrong / Text* / PreviewBg / Toolbar / NeutralSoft / Surface / FieldBackground / DropZone*`。
- **阅读画布切换**：当前阅读区实际用 `Brush.Sidebar` 作背景（`ReaderView.xaml:266`），要让侧栏（暖 #F4F1EA）与画布（暖纸 #ECE7DC）分离，需把该处改为 `{DynamicResource Brush.PreviewBg}`（token 已存在，**无需新增**）。

### Phase 1 — 衬线展示字体（先小范围）
- `App.xaml`：新增 `FontFamily` 资源 + 给 `PageTitle`(87)、`SectionTitle`(96) 增加 `FontFamily` setter（只这两类，避免污染正文）。
- `MainWindow.xaml:10`：根字体保持 sans；仅对需要衬线的元素单独绑定。
- `Views/StartPage/StartPageView.xaml:142-146`（hero 标题）。
- `Views/Reader/ReaderView.xaml`：面板/抽屉 section 标题。
- `Views/Settings/SettingsDialog.cs:50`、`Views/Settings/AboutDialog.cs:37`：代码构建对话框标题单独设衬线。
- **同步副本**（本地样式重定义处，见 §4 备注）：`PageOrganizerResources.xaml:11` / `ToolPageStyles.xaml:20` / `WorkflowResources.xaml:28`。

### Phase 2 — A 细节：状态与选中（方案A 未执行项）
- **活动标签** = 白底 + 2px 强调下划线：`MainWindow.xaml` `TabStripItem`（180-212，选中触发器 204-207 已用 `Brush.Window`+`Brush.Accent`）→ 明确为「白底 + 底边 2px」。
- **活动导航项** = 白底 + 强调文字 + 半粗：`MainWindow.xaml` `ReaderPanelToggle`（243-272，现为 `AccentSoft`/`AccentText` 填充）→ 改为白底 + accent 文字 + `FontWeight=SemiBold`。
- **选中缩略图** = 2px 强调描边：`Views/Reader/Panels/ThumbPanel.xaml`（当前**无** `ItemContainerStyle`，走系统默认高亮）→ 新增 `ListBoxItem` 样式，`IsSelected` 触发器加 2px `Brush.Accent` 描边。
- **选中目录/书签**（当前系统默认）：`OutlinePanel.xaml`、`BookmarkPanel.xaml` 补 `IsSelected` → `Brush.AccentSoft`/`Brush.AccentText`（搜索面板 `SearchPanel.xaml:54` 已 token 化，对齐即可）。
- **保存胶囊四态**：`MainWindow.xaml:747-765`（现 BasedOn `SecondaryButton`）→ 常态 `AccentSoft` 底 + `AccentText` 文字 + `Success` 绿点；`保存中` → `ConverterWarn*`；`失败` → `DangerSoft`/`Danger`；`只读` → `NeutralSoft`/`TextSecondary`。
- **焦点环** = accent：`App.xaml` 增加全局 `FocusVisualStyle`（2px `Brush.Accent` 外环）；输入框聚焦用 `Accent` 边框 + `AccentSoft` 3px 环。

### Phase 3 — 纸感画布与阴影（可选但方向核心）
- 阅读区背景切 `Brush.PreviewBg`（`ReaderView.xaml:266`）。
- **纸纹**：`ReaderView.xaml` 阅读区叠加极低不透明度 `DrawingBrush`（瓦片噪点）或极细两层渐变；**只用画布背景层，不承载语义、不进页面位图**（见 §5）。
- **页影暖化**：`MainWindow.xaml:568,638` 的 `#000000` `DropShadowEffect` → 若要暖化，见 §6 的第 3 项（token 或白名单，二选一）。

### Phase 4 — 工具页 / 模态收口
- 工具资源字典（暖色值无需改，随 Phase 0 自动生效；若改圆角/字号需逐个改）：
  `Views/PageOrganizer/PageOrganizerResources.xaml:11-174`、`Views/Optimizer/ToolPageStyles.xaml:11-257`、`Views/Converter/ConverterResources.xaml:11-101`、`Views/Security/SecurityPageStyles.xaml:10-66`、`Views/Workflow/WorkflowResources.xaml:19-213`。
- **设置（7 项）**：`Views/Settings/SettingsDialog.cs`——默认缩放 / 默认左栏面板 / 最近文件上限 / 主题 / 打印 DPI / 默认输出目录 / 默认渲染 DPI。A+C 只改样式，不改字段。
- **向导**：`Views/Wizard/WizardDialog.cs`（含遮罩 scrim，见 §6）。
- **打印**：`Services/PrintService.cs` 为逻辑；打印对话框为 WPF 打印管线 UI，A+C 只改视觉。
- **锁定遮罩**：`ReaderView.xaml:755-792`（背景现为 `Brush.Sidebar`；`🔒` 硬编码 emoji）→ 遮罩改 `Brush.OverlayMask`（新增候选，见 §6），其余沿用。

> **副本注意**：`PageTitle / SectionTitle / FieldLabel / FieldHint / PrimaryButton / SecondaryButton` 在 `App.xaml` 与四个分组字典中**重复定义**。改**颜色**只需改 `App.xaml`（本地定义引用 `DynamicResource`，会自动跟随）；改**字体/圆角/内边距/字重**必须逐个副本同步。

---

## 5. CJK 衬线约束 + 纸纹注意事项

### 5.1 衬线（CJK）
- **不内嵌字体**。Source Han Serif SC / Noto Serif SC 单字重 10–20 MB+，打包会显著增大安装体积，且涉及 OFL 再分发义务（需携带许可与来源说明）。项目约定是**系统字体 + 优雅回退**。
- 回退链：`"Source Han Serif SC","Noto Serif SC","Songti SC","SimSun",Georgia,"Times New Roman",serif`。Windows 原生一般命中 **SimSun（宋体）**，得到朴素的印刷/公文观感；若用户装了思源宋体则更好。
- **风险**：不同字重/字面宽度差异会让标题换行点变化；落地后需在 26/15 两档字号下目视校验标题不溢出、行高不跳。衬线**只用于展示标题**，正文/UI 保持 sans，避免可读性与渲染开销问题。
- 图标字形 `Segoe MDL2 Assets` 明确排除在衬线替换之外。

### 5.2 纸纹（Paper grain）
- 定位为**纯装饰**：不承载任何语义、不指示状态、不参与交互。
- **只铺在阅读画布背景层**（页面之外）；**绝不铺在页面/位图上**，因此不影响打印、导出与 OCR/批注坐标。
- 候选用**极低不透明度**瓦片噪点（CSS 内联 SVG `feTurbulence`；WPF 可用 `DrawingBrush` + `TileMode=Tile`，`Opacity ≤ 0.06–0.10`）。
- **坑**：(a) 瓦片尺寸与设备像素不对齐会产生摩尔纹/接缝，需选非整除尺寸并目视高 DPI 屏；(b) 过强会降低文字对比、增加视觉噪声；(c) 大面积平铺有渲染成本，滚动时可能掉帧；(d) 无法与 CSS 完全一致——**WPF 实现允许直接省略**，仅保留暖色画布即可成立。
- 建议提供「关闭纸纹」入口或用 `SystemParameters`/`prefers-reduced-motion` 同级的无障碍开关；`需求总结.md` 已将「夜间阅读模式」列为后续候选，纸纹同样应可降级。

---

## 6. 需新增 token / 暗色对应项（显式清单）

> 规则：新增 token **必须**同时改 ①`Services/ThemeManager.cs:37-48` 的 `ThemeTokens.All`；②`App.xaml` 亮 `Brush.<name>` + 暗 `Brush.Dark.<name>`；③使用处必须 `{DynamicResource ...}`。否则 `ThemeManagerTests` 的完整性/审计断言会失败。

| # | 新增项 | 亮 | 暗 | 触发原因 | 影响面 |
|---|---|---|---|---|---|
| 1 | `Brush.OverlayMask` / `Brush.Dark.OverlayMask` | `rgba(43,37,28,.45)` ≈ `#732B251C` | `rgba(10,8,5,.56)` ≈ `#8F0A0805` | 模态/锁定遮罩目前无 token：向导 scrim 在 `WizardDialog.cs` 硬编码 `Color.FromArgb(0x66,0x10,0x14,0x1C)`（审计白名单项），ReaderView 锁定用 `Brush.Sidebar` | 需改 `WizardDialog.cs`、`ReaderView.xaml:755-792`；并同步 `DialogCodeColorWhitelist`（否则白名单项与 token 重叠） |
| 2 | `Brush.OnAccent` / `Brush.Dark.OnAccent` | `#FFFFFF` | `#FFFFFF`（或暖白 `#FDFBF6`） | 现有 **12 处** `Foreground/Stroke="White"` 硬编码（MainWindow 330/544/691/710/727、StartPage 406、ReaderView 437/751、PageOrganizerResources 68、ToolPageStyles 191、ConverterResources 76、WorkflowResources 76）。命名色当前被审计放行，但语义应 token 化 | 替换 12 处为 `{DynamicResource Brush.OnAccent}`；纯替换，风险低 |
| 3 | 页影颜色（二选一，**建议暂缓**） | 暖影 `#4A3A24` | `#000000` | `MainWindow.xaml:568,638` 的 `DropShadowEffect Color="#000000"`。审计**显式跳过** `#000000` 阴影，若改成暖色会变成「白名单外硬编码」而失败 | 方案 A：保持 `#000000`（零改动、审计通过，仅牺牲暖度）。方案 B：新增 `Color.Shadow` 资源，但 `ThemeManager` 只换 `Brush.*`，需要扩展 Runtime 支持 Color token（超出当前机制，成本高）。**A+C 设计稿的暖影为 CSS 视觉目标；WPF 落地建议方案 A，或在 `ThemeManagerTests` 白名单登记暖影 hex 并接受审计例外** |
| 4 | 通用警示 token（**建议不新增，复用**） | 复用 `Brush.ConverterWarnBackground`/`Text` | 同左 | HTML 的 `--warnbg/--warntext` 已映射到 `ConverterWarn*`；语义偏「转换器」但可承载通用警示 | 不新增，避免审计链改动。若坚持语义纯净，则新增 `Brush.WarnBackground/WarnText`（+暗）并迁移 `ConverterResources.xaml:20-40` 等处 |
| 5 | 选中列表高亮（**不必新增**） | 复用 `Brush.Accent` / `Brush.AccentSoft` / `Brush.AccentText` | 同左 | 缩略图/目录/书签当前用系统默认选中（未 token 化） | 新增 `ItemContainerStyle`，复用既有 accent token，**无需新 token** |
| 6 | 衬线字体 | `FontFamily` 资源（非 Brush） | —— | 展示标题 | 不是颜色 token，`ThemeManager` 不管字体，无暗色对应；作为 `FontFamily` 资源或样式 setter |

> 结论：**真正需要新增且成对的 token = 1 个（`Brush.OverlayMask`）**；强烈建议再补 **1 个（`Brush.OnAccent`）**；其余均可复用既有 token 或作为样式/资源处理。

---

## 7. 审计与验证说明（不修改测试，只说明门槛）

当前 token 审计的实际机制（供实现时对照，**本任务未改动**）：

- `ThemeTokens.All` 共 **29** 项（`Services/ThemeManager.cs:37-48`）。
- `ThemeManagerTests.cs:86-88`：断言 `App.xaml` 亮色键集 **等于** `ThemeTokens.All`（双向严格）。
- `ThemeManagerTests.cs:90-92`：断言每个亮色 token 都有 `Brush.Dark.*` 对应（单向；多出的暗色键不会失败）。
- **没有** `Assert.Equal(29, ...)` 的字面断言——「29」是数组长度隐含值。若要硬化，可在 `ThemeManagerTests.cs:83` 附近补一条长度断言（属测试改动，**本次未做**）。
- 审计还强制：主题引用必须是 `DynamicResource`（`{StaticResource Brush.*}` 会失败，`ThemeManagerTests.cs:163-166`）；白名单外的硬编码 hex / `Color.From*` 会失败（`ContentColorWhitelist`：ReaderView 4 色 + PdfPreviewControl 2 色；`DialogCodeColorWhitelist`：WizardDialog scrim；命名色 `White/Black/Transparent` 放行）。
- 现有硬编码 hex 分布：`App.xaml` 58（token 源）+ `ReaderView.xaml` 9（批注/绘制内容色）+ `PdfPreviewControl.xaml` 2（框选叠层内容色）+ `MainWindow.xaml` 2（`#000000` 阴影，审计跳过）。即：**真正的意外绕过只有 MainWindow 两处阴影**。

因此 A+C 的落地方式为：**只改 `App.xaml` 的值 + 必要的样式/选中态 + 至多 1–2 个新增 token 的成对登记**，不触碰测试与验收记录。

---

### 附：本次交付

1. `设计/v1.0-阅读器/pdf-reader-设计稿-A+C.html` — 自包含设计稿（亮/暗主题切换；起始页、阅读态三栏、工具页、向导三步、设置 7 项、打印、锁定遮罩、批注浮菜单、保存胶囊四态、空/加载/错误、Toast、Token 色卡）。零外部网络资源，保留原 CSS 变量名。
2. `设计/v1.0-阅读器/A+C-设计说明与token清单.md` — 本文件。

未改动：`Sources/**`、`tests/**`、V3 验收记录。未运行 dotnet / npm / 浏览器。
