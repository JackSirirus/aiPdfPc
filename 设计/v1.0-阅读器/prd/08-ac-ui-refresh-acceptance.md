# A+C UI Refresh — V3 Acceptance Record

> 日期：2026-09-12｜计划：`.omo/plans/aipdf-ac-ui-refresh.md`

## 交付内容

| 阶段 | 提交 | 说明 |
|---|---|---|
| P0 暖色 token | `3614af9` | 29 亮+29 暗换值（键名不变）+ PreviewBg 阅读画布 |
| P1 衬线标题 | `42399cc` | FontFamily.Display + PageTitle/SectionTitle + 3 字典副本 |
| P2 选中态/胶囊 | `cdb5526` | 活动标签/导航/缩略图/目录书签 + 保存胶囊四态 + 焦点环 |
| P3 暖纸确认 | `4a3e3fa` | 页影 #000000 注释记录 |
| P4 新 token | `da52ebb` | OverlayMask/OnAccent + 迁移 scrim/锁定/White |
| 设计稿 | `278b283` | HTML 设计稿 + token/迁移说明 |

## 终审

| 门 | 结论 |
|---|---|
| F1 合规/审计 | APPROVE（37/37 Must Have · 10/10 Must NOT · 12/12 Tasks） |
| F2 质量 | APPROVE（847/847 · 新告警 0 · StaticResource 残留 0 · 审计 17/17） |
| F3 实机 QA | APPROVE（6/6 截图 + 像素验证：暖纸/衬线/选中态/OverlayMask/OnAccent/锁定遮罩） |

## 审计要点
- token 键名零变更；新增 OverlayMask + OnAccent 成对（亮/暗）；`ThemeTokens.All` 对齐
- `StaticResource Brush.*` 残留 **0**；主题引用全走 `DynamicResource` / `SetResourceReference`
- 斜线/衬线仅用于展示标题（6 处 + 3 副本同步）；正文/UI 保持 sans
- 硬编码 `#000000` 阴影保留并注释原因（审计跳过 + ThemeManager 不换 Color）
- 无 slop（TODO/FIXME/HACK = 0）；无白名单例外；信息架构/字号阶梯不变

## 已知限制
- 动态主题切换实时生效但 `Application.Resources` 替换需把 style setter 改为 DynamicResource（已完成）
- CJK 衬线在 Windows 上大概率命中 SimSun（宋体）；落地后目视校验标题换行
- 纸纹（feTurbulence 瓦片噪点）为**可选/延期**，仅画布背景层，WPF 可近似或直接省略
- 全仓 `dotnet test` 847/847 通过（含 ThemeManager 审计）；F3 实机 6/6 通过
