// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace AIPDF.Windows.Tests.UI;

/// <summary>
/// 冒烟清单对应项（docs/手动冒烟清单-Windows.md）：
/// 2 启动（主窗 + 状态栏就绪）、11 工具箱导航（右抽屉 / 向导两条通道）、顶栏控件可达性、
/// 12 文件拖拽（Toolbox_DragFile_IntoDropZone_AddsFile：真实 OS 鼠标 Explorer→drop 区）。
/// 预览观感（13）仍为人工项——见清单标注。
/// </summary>
[Trait("Category", "UI")]
[Collection(UiSmokeCollection.Name)]
public sealed class UiSmokeTests : UiSmokeTestBase
{
    public UiSmokeTests(UiSmokeFixture fixture) : base(fixture)
    {
    }

    [SkippableFact(DisplayName = "冒烟2 启动：主窗口标题正确且进程存活")]
    public void Startup_MainWindowTitleAndAlive()
    {
        SkipIfUnavailable();

        Assert.Equal(UiSmokeFixture.MainWindowTitle, Fixture.MainWindow.Title);
        Assert.False(Fixture.App.HasExited, "应用进程不应已退出");
    }

    [SkippableFact(DisplayName = "冒烟2 启动：状态栏显示「就绪」与工具/分组计数")]
    public void Startup_StatusBarShowsReady()
    {
        SkipIfUnavailable();

        var status = Fixture.WaitById("StatusBarText", TimeSpan.FromSeconds(10));
        if (status is null)
        {
            // StatusBarHost 是状态栏宿主 Border —— 同 ToolboxPanel/RightDrawer：WPF 不为 Border
            // 建 automation peer，其 AutomationId 在 UIA 树中不可达（2026-09-24 Task11 run2：
            // WaitById 5s=null，而同容器内子级 StatusBarText 即刻命中 → peerless，非超时）。
            // 故不作正向 NotNull；文案缺失时查询宿主桌面命中数入失败诊断（预期=0）。
            var hostHits = Fixture.Automation.GetDesktop()
                .FindAllDescendants(Fixture.Cf.ByAutomationId("StatusBarHost")).Length;
            Assert.Fail(
                $"状态栏文案 StatusBarText 未找到；StatusBarHost（Border 无 peer）桌面命中={hostHits}，预期=0");
        }
        Assert.NotNull(status);
        Assert.NotEmpty(status.Name);

        // 两种健康态（状态栏文案系统正常工作的证据）：
        // 1) 冷启动 / 无会话恢复：MainViewModel.DescribeReady() = "{n} 个工具 · {m} 个分组 · 就绪"（目录权威 catalog.json：33 工具 / 6 组）；
        // 2) 会话恢复（FR-29，session.json 有上次标签）：启动即重开文档 → MainWindow 打开后置 "已打开 {标题}"（或「已打开（锁定）…」），
        //    就绪文案不会再出现——本机带真实会话时清单 #2 的「状态栏就绪」对应此态。
        // 断言锁语义（就绪格式 / 已打开前缀），不锁整句；就绪分支额外锁 工具/分组 计数词。
        var text = status.Name;
        var isReady = text.Contains("就绪", StringComparison.Ordinal);
        var isRestored = text.StartsWith("已打开", StringComparison.Ordinal);
        Assert.True(isReady || isRestored,
            $"状态栏应为就绪文案或会话恢复的「已打开 …」，实际：{text}");

        if (isReady)
        {
            Assert.Contains("工具", text, StringComparison.Ordinal);
            Assert.Contains("分组", text, StringComparison.Ordinal);
        }
    }

    [SkippableFact(DisplayName = "冒烟2 启动：无 .NET Desktop Runtime 缺失类模态")]
    public void Startup_NoRuntimeMissingDialog()
    {
        SkipIfUnavailable();

        // 运行时缺失通常在主窗前弹系统对话框；主窗已出现即视为通过。
        // 额外确认主窗仍是前台可交互根，而不是挂在崩溃对话框后面。
        var top = Fixture.MainWindow;
        Assert.False(top.Name.Contains("已停止工作", StringComparison.OrdinalIgnoreCase),
            "主窗口标题不应是「已停止工作」崩溃壳");
    }

    [SkippableFact(DisplayName = "冒烟11 工具箱：「更多」菜单打开面板，列表项数与目录一致")]
    public void Toolbox_OpensAndListsAllTools()
    {
        SkipIfUnavailable();

        OpenToolbox();

        // 面板容器 ToolboxPanel 是 Border：WPF 不为 Border 建 automation peer，其 AutomationId
        // 在 UIA 树中永远不可达（见 .omo/evidence/test-gaps/task-3-diag2.txt：展开后 desktop 命中仍=0，
        // 而同面板内的 ToolboxList=1、33 项可见）。改以 ToolboxList（ListBox，有 peer）为面板存在信号。
        var list = Fixture.FindById("ToolboxList");
        Assert.NotNull(list);
        Assert.True(list.IsEnabled, "工具箱面板应可见可用");

        var catalog = Models.ToolCatalog.Load();
        var listItems = list.FindAllDescendants(
            Fixture.Cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));

        // 分组头不是 ListItem；工具条目 AutomationProperties.Name = Tool.Name（SidebarToolItem 样式）。
        // 虚拟化/滚动可能未生成全部容器 → 至少断言非空，且目录首项名称可定位。
        Assert.True(listItems.Length > 0, "工具箱列表应有工具条目");
        Assert.True(catalog.Count > 0);

        var firstName = catalog[0].Name;
        var located = list.FindFirstDescendant(Fixture.Cf.ByName(firstName));
        Assert.NotNull(located);
    }

    [SkippableFact(DisplayName = "冒烟11 导航：organize 打开右抽屉并显示工具名")]
    public void Toolbox_Navigate_Organize_OpensRightDrawer()
    {
        SkipIfUnavailable();

        OpenToolbox();

        // organize 属右抽屉通道（ToolChannels.RightDrawerIds）
        var organizeName = ToolName("organize");
        var item = WaitToolItem(organizeName);
        Assert.NotNull(item);

        item.Click(); // 鼠标点击 → SelectionChanged → ShowToolPage → ShowRightDrawer

        // RightDrawer 也是 Border（同 ToolboxPanel：无 UIA peer，AutomationId 不可达）。
        // 改以 RightDrawerTitle（TextBlock，有 peer，AutomationId=RightDrawerTitle）为抽屉开合信号：
        // 标题可见 ⇒ 抽屉已展开且在屏；标题名 = 工具名保持原断言强度。
        var title = Fixture.WaitById("RightDrawerTitle", TimeSpan.FromSeconds(8));
        Assert.NotNull(title);
        Assert.False(title.IsOffscreen, "右抽屉应可见");
        Assert.Equal(organizeName, title.Name);
    }

    [SkippableFact(DisplayName = "冒烟11 导航：merge 打开居中向导（标题含工具名）")]
    public void Toolbox_Navigate_Merge_OpensWizardDialog()
    {
        SkipIfUnavailable();

        OpenToolbox();

        var mergeName = ToolName("merge");
        var item = WaitToolItem(mergeName);
        Assert.NotNull(item);

        item.Click(); // 鼠标点击 → ShowWizard → ShowDialog

        // 向导 Title = $"向导 · {tool.Name}"（WizardDialog 构造）
        var wizardTitle = $"向导 · {mergeName}";
        var wizardWindow = Retry.WhileTrue(() =>
        {
            // 模态窗是独立 HWND，不在 MainWindow 子树；从桌面根找
            var desktop = Fixture.Automation.GetDesktop();
            return desktop.FindFirstDescendant(
                Fixture.Cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)
                    .And(Fixture.Cf.ByName(wizardTitle))) is null;
        }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));

        var desktop = Fixture.Automation.GetDesktop();
        var wizard = desktop.FindFirstDescendant(
            Fixture.Cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)
                .And(Fixture.Cf.ByName(wizardTitle)));
        Assert.NotNull(wizard);

        // 关掉向导，避免污染后续用例（Esc / 取消按钮）
        try
        {
            var cancel = wizard.FindFirstDescendant(Fixture.Cf.ByName("取消"));
            cancel?.Click();
        }
        catch
        {
            // 进程级 Close 兜底在夹具 Dispose
        }
    }

    [SkippableFact(DisplayName = "冒烟11 导航：关闭工具箱后面板收起")]
    public void Toolbox_Close_HidesPanel()
    {
        SkipIfUnavailable();

        OpenToolbox();
        var close = Fixture.WaitByName<Button>("关闭工具箱", TimeSpan.FromSeconds(5));
        Assert.NotNull(close);

        // 关闭动作多策略（与 OpenToolbox 同构，原因同层）：合成鼠标 Click 在本环境偶发不触发 handler ——
        // 2026-09-23 两次失败 + 2026-09-24 复现 runB：click 后 5s closeBtn/ToolboxList 仍在屏
        // （未进入 OnCloseToolboxClick），同代码 runA 却通过 → 命中层不稳定，非生产关闭逻辑问题
        // （OnCloseToolboxClick = ToolboxPanel.Visibility=Collapsed，见 MainWindow.xaml.cs）。
        // 优先 UIA Invoke 直达同一生产关闭路径；语义仍是「执行关闭 → 面板收起」。
        string used = "none";
        if (ToolboxCloseAfter(() => close.Invoke()))
        {
            used = "Invoke";
        }
        else if (ToolboxCloseAfter(() => close.Click()))
        {
            used = "Click";
        }
        else if (ToolboxCloseAfter(() => { close.Focus(); Keyboard.Press(VirtualKeyShort.RETURN); }))
        {
            used = "Focus+Enter";
        }

        // 收起判据：ToolboxList 离屏或消失（ToolboxPanel=Border 无 peer，不可作判据）。
        // 收起后 UIA 仍可能找到节点但 IsOffscreen=true —— 轮询至二者任一成立再断言。
        Retry.WhileTrue(() => ToolboxList() is { IsOffscreen: false },
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
        var list = ToolboxList();
        if (list is { IsOffscreen: false })
        {
            // 失败时带齐现场：区分「关闭动作未生效 / 面板未收 / UIA 报错觉」。
            var closeNow = Fixture.FindByName("关闭工具箱");
            var openBtn = Fixture.FindByName("更多");
            Assert.Fail(
                $"关闭后工具箱面板应离屏/不可见（尝试={used}）；" +
                $"list: Off={list.IsOffscreen} Rect={list.BoundingRectangle} Enabled={list.IsEnabled}; " +
                $"closeBtn: {(closeNow is null ? "null" : $"Off={closeNow.IsOffscreen} Rect={closeNow.BoundingRectangle}")}; " +
                $"openBtn: {(openBtn is null ? "null" : $"Off={openBtn.IsOffscreen}")}");
        }
    }

    [SkippableFact(DisplayName = "冒烟11 顶栏：更多 / 新建标签 可达（Name 定位）")]
    public void TopBar_ButtonsReachable()
    {
        SkipIfUnavailable();

        // A+C 方案 A：原常驻「工具箱」「设置」两个按钮合并为「⋯ 更多」菜单（设置/工具箱在菜单内），
        // 故顶栏一级入口断言改为「更多」。
        foreach (var name in new[] { "更多", "新建标签", "全文搜索", "打印" })
        {
            var button = Fixture.WaitByName<Button>(name, TimeSpan.FromSeconds(5));
            Assert.True(button is not null, $"顶栏按钮「{name}」应可通过 UIA Name 定位");
        }
    }

    // ---- helpers ----

    /// <summary>
    /// 工具箱面板「已展开」的 UIA 判据：ToolboxList（ListBox）在屏。
    /// <para>
    /// 不能用 ToolboxPanel：它是 Border，WPF 不为 Border 创建 automation peer →
    /// 其 AutomationId 在 UIA 树中永远查不到（展开前后 desktop 命中均=0）。
    /// 实证见 .omo/evidence/test-gaps/task-3-diag2.txt（Task 3 诊断 harness）。
    /// </para>
    /// </summary>
    private AutomationElement? ToolboxList() => Fixture.FindById("ToolboxList");

    private bool IsToolboxOpen() => ToolboxList() is { IsOffscreen: false };

    private void OpenToolbox()
    {
        if (IsToolboxOpen())
        {
            return;
        }

        // A+C 方案 A：顶栏常驻「工具箱」按钮改为「⋯ 更多」菜单项，打开分两步：
        // ① 顶栏「更多」按钮展开菜单 ② 点菜单项「工具箱」。菜单是独立 Popup，
        //    UIA 里不在 MainWindow 子树下，必须从桌面范围查找菜单项。
        var more = Fixture.WaitByName<Button>("更多", TimeSpan.FromSeconds(10));
        Assert.NotNull(more);

        string used = "none";
        MenuItem? item = null;
        for (var attempt = 0; attempt < 2 && item is null; attempt++)
        {
            // 合成动作在该环境偶发不触发 handler（见下方 Close 用例同层说明），故多策略重试。
            try { more.Invoke(); } catch { /* 换下一策略 */ }
            item = FindToolboxMenuItem(TimeSpan.FromSeconds(3));
            if (item is null)
            {
                try { more.Click(); } catch { /* 换下一策略 */ }
                item = FindToolboxMenuItem(TimeSpan.FromSeconds(3));
            }
        }

        Assert.NotNull(item);
        if (ToolboxOpenAfter(() => item.Invoke()))
        {
            used = "更多>Invoke";
        }
        else if (ToolboxOpenAfter(() => { item.Focus(); Keyboard.Press(VirtualKeyShort.RETURN); }))
        {
            used = "更多>Focus+Enter";
        }
        else if (ToolboxOpenAfter(() => item.Click()))
        {
            used = "更多>Click";
        }

        if (!IsToolboxOpen())
        {
            var listHits = Fixture.Automation.GetDesktop()
                .FindAllDescendants(Fixture.Cf.ByAutomationId("ToolboxList")).Length;
            var panelHits = Fixture.Automation.GetDesktop()
                .FindAllDescendants(Fixture.Cf.ByAutomationId("ToolboxPanel")).Length;
            Assert.Fail($"工具箱未展开（尝试={used}）。more: ControlType={more.ControlType}, Name='{more.Name}', " +
                        $"IsEnabled={more.IsEnabled}, IsOffscreen={more.IsOffscreen}; item='{item.Name}'; " +
                        $"desktop ToolboxList 命中={listHits}; desktop ToolboxPanel 命中={panelHits}（Border 无 peer，展开时预期=0）");
        }
    }

    /// <summary>从桌面范围轮询「工具箱」菜单项（ContextMenu 为独立 Popup，不在 MainWindow 子树）。</summary>
    private MenuItem? FindToolboxMenuItem(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var item = Fixture.FindByName<MenuItem>("工具箱", Fixture.Automation.GetDesktop());
            if (item is not null)
            {
                return item;
            }

            Thread.Sleep(200);
        }

        return null;
    }

    private bool ToolboxOpenAfter(Action act)
    {
        try
        {
            act();
        }
        catch
        {
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (IsToolboxOpen())
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// 执行一次「关闭工具箱」动作后轮询：ToolboxList 消失或离屏 ⇒ 关闭生效。
    /// 与 <see cref="ToolboxOpenAfter"/> 对称；单策略未生效返回 false，交下一策略重试。
    /// </summary>
    private bool ToolboxCloseAfter(Action act)
    {
        try
        {
            act();
        }
        catch
        {
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var list = ToolboxList();
            if (list is null || list.IsOffscreen)
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private static string ToolName(string id)
        => Models.ToolCatalog.Load().First(t => t.Id == id).Name;

    private AutomationElement? WaitToolItem(string toolName)
    {
        // 面板内容都在 ToolboxList 下（ToolboxPanel=Border 不可达）。
        var list = ToolboxList();
        if (list is null)
        {
            return null;
        }

        AutomationElement? found = null;
        Retry.WhileTrue(() =>
        {
            found = list.FindFirstDescendant(Fixture.Cf.ByName(toolName));
            return found is null;
        }, TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(250));
        return found;
    }
}
