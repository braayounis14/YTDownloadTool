using System.Windows.Controls;
using System.Windows.Input;
using YTDownloadTool.ViewModels;

namespace YTDownloadTool.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView()
    {
        InitializeComponent();
    }

    private void UrlTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DownloadsViewModel vm && vm.AnalyzeUrlCommand.CanExecute(null))
        {
            vm.AnalyzeUrlCommand.Execute(null);
            e.Handled = true;
        }
    }
}
