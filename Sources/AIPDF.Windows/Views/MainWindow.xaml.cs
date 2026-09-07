using System.ComponentModel;
using System.Windows;

namespace AIPDF.Windows.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Wave 5 工具组导航注册：每组一行（并行任务合并约定，T14 起建立）。
        PageOrganizer.PageOrganizerPages.Register();
        Optimizer.OptimizerToolPages.Register(); // T15：优化文件组（压缩 / 修复 / OCR）
        Workflow.WorkflowPages.Register(); // T19：工作流程组（步骤串联 / 预设 / 批量 / 记录）

        DataContextChanged += (_, _) => HookViewModel();
        HookViewModel();
    }

    private void HookViewModel()
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ShowToolPage(viewModel.SelectedTool, viewModel.Bridge);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.SelectedTool)
            && sender is ViewModels.MainViewModel viewModel)
        {
            ShowToolPage(viewModel.SelectedTool, viewModel.Bridge);
        }
    }

    /// <summary>注册表认领的工具显示对应工具页，否则回退到主窗口的占位视图。</summary>
    private void ShowToolPage(Models.Tool? tool, Bridge.EngineBridge? bridge)
    {
        var page = tool is null ? null : ToolPageRegistry.TryCreate(tool, bridge);
        if (page is not null)
        {
            ToolPageHost.Content = page;
            ToolPageHost.Visibility = Visibility.Visible;
            DetailPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            ToolPageHost.Content = null;
            ToolPageHost.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Visible;
        }
    }
}
