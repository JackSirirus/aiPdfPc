// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Threading;
using System.Windows;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.Views;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 转换组页面实例化回归测试（T15）。修复前，转换组 11 个工具页在运行时全部抛
/// XamlParseException：共享子控件 FileListPanel/ToolRunBar 的 StaticResource 引用了
/// ConverterResources.xaml 里的组内键，而该字典只合并在各宿主页面的 Resources 中；
/// UserControl 的 BAML 是独立解析的，解析期只看得见自身资源与应用级资源，看不见宿主
/// 页面的资源。异常沿 MainWindow.ShowToolPage 抛出后被 UIA/绑定边界吞掉，表现为
/// "选中转换工具后停留在占位视图或上一页"。本测试镜像 MainWindow 的真实创建路径
/// （ToolPageRegistry → 各组 TryCreate），在 STA 线程上逐个实例化 11 个转换页，
/// 保证页面始终可创建、DataContext 挂上对应视图模型（依赖缺失时依赖提示由
/// ConverterToolViewModelBase.DependencyWarning 呈现，不影响页面打开）。
/// </summary>
public sealed class ConverterPageTests
{
    private static readonly IReadOnlyList<Tool> Catalog = ToolCatalog.Load();

    private static Tool Tool(string id) => Catalog.First(t => t.Id == id);

    private static readonly string[] ConverterToolIds =
    {
        "images_to_pdf", "pdf_to_images", "word_to_pdf", "ppt_to_pdf", "excel_to_pdf",
        "pdf_to_word", "pdf_to_ppt", "pdf_to_excel", "html_to_pdf", "pdfa", "markdown",
    };

    private static App? _app;

    /// <summary>页面对象具有 UI 线程亲和性：整条创建路径跑在同一个 STA 线程上。</summary>
    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null)
        {
            return;
        }

        _app = new App();
        // 载入 App.xaml 应用级 token（Brush.*、FieldTextBox 等）；
        // StartupUri 只在 Run() 时生效，测试不会打开窗口。
        _app.InitializeComponent();
    }

    [Fact]
    public void TryCreate_AllElevenConverterPagesInstantiateLikeMainWindow()
    {
        Assert.Equal(11, ConverterToolIds.Length);

        RunOnSta(() =>
        {
            EnsureApplication();

            // 与 MainWindow 构造相同的注册表；先清空保证测试隔离。
            ToolPageRegistry.Reset();
            Views.PageOrganizer.PageOrganizerPages.Register();
            Views.Converter.ConverterPages.Register();
            Views.Optimizer.OptimizerToolPages.Register();
            Views.Security.SecurityPages.Register();
            Views.Editor.EditorPages.Register();
            Views.Workflow.WorkflowPages.Register();

            foreach (var id in ConverterToolIds)
            {
                var tool = Tool(id);

                // 与 MainWindow.ShowToolPage 完全相同的入口：未经修复时此处抛
                // XamlParseException（"Cannot find resource named 'ConverterCard'"）。
                var page = ToolPageRegistry.TryCreate(tool, bridge: null);

                Assert.True(page is not null, $"工具 {id} 的页面应能创建（回归：运行时 XamlParseException 导致页面打不开）");
                var viewModel = Assert.IsAssignableFrom<ConverterToolViewModelBase>(page!.DataContext);
                Assert.Equal(id, viewModel.Tool.Id);
            }
        });
    }
}
