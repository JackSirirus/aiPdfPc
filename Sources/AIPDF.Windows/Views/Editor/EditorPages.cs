// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Editor;

/// <summary>
/// 编辑内容组的页面解析器（T17）：经 ToolPageRegistry（Wave 5 并行约定）在 MainWindow 注册，
/// 选中「编辑内容」组的工具时创建对应页面，其余工具返回 null 交给下一个解析器。
/// 页面视图模型持有执行回调：经引擎桥 stdin（UTF-8 JSON）执行，反馈展示在工具页内。
/// </summary>
public static class EditorPages
{
    private static bool _registered;

    /// <summary>本解析器覆盖的工具 id（与 backend/catalog.py 编辑内容组一致，不新增工具）。</summary>
    public static readonly IReadOnlyList<string> SupportedToolIds =
        ["rotate", "numbers", "watermark", "crop", "edit", "forms"];

    public static FrameworkElement? Resolve(Tool tool, EngineBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Id switch
        {
            "rotate" => Wire(new RotatePage(), new RotateViewModel(tool, Runner(bridge)), bridge),
            "numbers" => Wire(new PageNumbersPage(), new PageNumbersViewModel(tool, Runner(bridge)), bridge),
            "watermark" => Wire(new WatermarkPage(), new WatermarkViewModel(tool, Runner(bridge)), bridge),
            "crop" => Wire(new CropPage(), new CropViewModel(tool, Runner(bridge)), bridge),
            "edit" => Wire(new EditPage(), new EditViewModel(tool, Runner(bridge)), bridge),
            "forms" => Wire(new FormsPage(), new FormsViewModel(tool, Runner(bridge)), bridge),
            _ => null,
        };
    }

    /// <summary>
    /// V3 T8：把引擎桥注入编辑工具 VM，供向导快照执行（<c>IWizardToolAdapter.ExecuteItemAsync</c>）使用。
    /// 工具页自身执行仍走 <see cref="Runner"/> 委托，既有语义不变（向导参数步复用同一 fresh 页面实例）。
    /// </summary>
    private static FrameworkElement Wire(FrameworkElement page, EditorToolViewModel viewModel, EngineBridge? bridge)
    {
        viewModel.WizardBridge = bridge;
        page.DataContext = viewModel;
        return page;
    }

    /// <summary>注册进全局工具页注册表（MainWindow 启动时调用一次；重复调用无副作用）。</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        ToolPageRegistry.AddResolver(Resolve);
    }

    /// <summary>
    /// 构造执行回调：把视图模型的参数映射经引擎桥发出。引擎业务错误（Ok=false）与
    /// 传输异常都按引擎原文展示在工具页反馈区；进度与完成状态不上抛主窗口状态栏。
    /// </summary>
    private static ExecuteEditorToolDelegate Runner(EngineBridge? bridge) =>
        async (viewModel, files, outputDir, cancellationToken) =>
        {
            if (bridge is null)
            {
                viewModel.ErrorText = "引擎未就绪：未配置执行桥。";
                return;
            }

            try
            {
                var result = await bridge.ExecuteAsync(new EngineRequest
                {
                    Operation = viewModel.Tool.Id,
                    Files = [.. files],
                    Options = viewModel.BuildOptions(),
                    OutputDir = outputDir,
                }, cancellationToken);

                // 反馈区复用 ErrorText：失败红色提示、完成展示输出目录与文件数。
                viewModel.ErrorText = result.Ok
                    ? $"完成 {viewModel.Tool.Name}：{result.Outputs.Count} 个文件 → {result.OutputDir}"
                    : $"失败：{result.Error}";
            }
            catch (OperationCanceledException)
            {
                viewModel.ErrorText = "已取消。";
            }
            catch (EngineBridgeException error)
            {
                viewModel.ErrorText = $"引擎异常：{error.Message}";
            }
        };
}
