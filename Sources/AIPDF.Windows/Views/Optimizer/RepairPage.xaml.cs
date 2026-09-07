using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Optimizer;

public partial class RepairPage : UserControl
{
    public RepairViewModel ViewModel { get; }

    public RepairPage(EngineBridge? bridge, Tool tool)
    {
        InitializeComponent();
        ViewModel = new RepairViewModel(bridge, tool);
        DataContext = ViewModel;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ViewModel.AddFiles(files);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
