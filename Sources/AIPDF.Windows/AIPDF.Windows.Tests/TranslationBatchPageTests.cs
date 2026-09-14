// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T21 批量翻译工具页实例化回归测试（镜像 ConverterPageTests 的真实创建路径）：
// ToolPageRegistry → TranslationToolPages.Resolve → TranslationBatchPage。
// 页面 XAML 独立解析自身资源（合并 Views/Optimizer/ToolPageStyles.xaml），
// 任何 StaticResource 前向引用都会在此暴露为 XamlParseException。
using System.Threading;
using System.Windows;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 批量翻译工具页实例化回归：保证 translate_batch 经 ToolPageRegistry 始终可创建，
/// 且 DataContext 挂上对应视图模型（选中工具后不再停留在占位视图）。
/// </summary>
public sealed class TranslationBatchPageTests
{
    private static readonly IReadOnlyList<Tool> Catalog = ToolCatalog.Load();

    private static Tool Tool(string id) => Catalog.First(t => t.Id == id);

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
        // 载入 App.xaml 应用级 token（Brush.*、FieldTextBox 等）；StartupUri 只在 Run() 时生效。
        _app.InitializeComponent();
    }

    [Fact]
    public void TryCreate_BatchTranslationPage_InstantiatesLikeMainWindow()
    {
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
            Views.Translation.TranslationToolPages.Register();

            var tool = Tool("translate_batch");
            var page = ToolPageRegistry.TryCreate(tool, bridge: null);

            Assert.True(page is not null, "批量翻译页应能创建（回归：运行时 XamlParseException 导致页面打不开）");
            var viewModel = Assert.IsType<TranslationBatchViewModel>(page!.DataContext);
            Assert.Equal("translate_batch", viewModel.Tool.Id);
            Assert.Equal(ToolChannel.Wizard, ToolChannels.ChannelFor(tool)); // 通道回归：批量翻译走向导通道
        });
    }
}
