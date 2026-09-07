using System.ComponentModel;
using System.Windows.Data;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 主视图模型测试：真实工具目录加载（32 工具 / 6 组）、选中工具的变更通知与状态栏文案。
/// </summary>
public sealed class MainViewModelTests
{
    [Fact]
    public void Constructor_LoadsRealCatalogIntoGroups()
    {
        var vm = CreateViewModel();

        Assert.Equal(32, vm.Tools.Count);
        Assert.Equal(6, vm.Groups.Count);
        Assert.Equal(32, vm.Groups.Sum(g => g.Tools.Count));
        Assert.Null(vm.SelectedTool);
        Assert.Single(vm.ToolsView.GroupDescriptions);
    }

    [Fact]
    public void StatusText_InitiallyShowsCountsAndReady()
    {
        var vm = CreateViewModel();

        Assert.Contains("32", vm.StatusText);
        Assert.Contains("6", vm.StatusText);
        Assert.Contains("就绪", vm.StatusText);
    }

    [Fact]
    public void SelectingTool_RaisesPropertyChangedForSelectedToolAndStatusText()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var tool = vm.Tools[0];
        vm.SelectedTool = tool;

        Assert.Contains(nameof(MainViewModel.SelectedTool), raised);
        Assert.Contains(nameof(MainViewModel.StatusText), raised);
    }

    [Fact]
    public void SelectingTool_UpdatesStatusTextWithToolName()
    {
        var vm = CreateViewModel();

        var tool = vm.Tools[0];
        vm.SelectedTool = tool;

        Assert.Same(tool, vm.SelectedTool);
        Assert.Contains(tool.Name, vm.StatusText);
        Assert.Contains(tool.Subtitle, vm.StatusText);
    }

    [Fact]
    public void ClearingSelection_RestoresReadyStatus()
    {
        var vm = CreateViewModel();
        vm.SelectedTool = vm.Tools[0];

        vm.SelectedTool = null;

        Assert.Null(vm.SelectedTool);
        Assert.Contains("就绪", vm.StatusText);
    }

    [Fact]
    public void ToolsView_IsGroupedByCategory()
    {
        var vm = CreateViewModel();

        var description = Assert.IsType<PropertyGroupDescription>(Assert.Single(vm.ToolsView.GroupDescriptions));
        Assert.Equal(nameof(AIPDF.Windows.Models.Tool.Category), description.PropertyName);
    }

    private static MainViewModel CreateViewModel() => new();
}
