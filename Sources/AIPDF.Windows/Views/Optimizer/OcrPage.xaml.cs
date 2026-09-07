using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Optimizer;

public partial class OcrPage : UserControl
{
    public OcrViewModel ViewModel { get; }

    public OcrPage(EngineBridge? bridge, Tool tool)
    {
        InitializeComponent();
        ViewModel = new OcrViewModel(bridge, tool);
        DataContext = ViewModel;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // 语言列表只在首次展示页面时检测一次；手动「重新检测」走 RefreshLanguagesCommand。
        ViewModel.LoadLanguagesOnceAsync();
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
