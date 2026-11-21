using System.ComponentModel;
using System.Windows;
using YTDownloadTool.ViewModels;

namespace YTDownloadTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowCloseAfterGracefulStop;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_allowCloseAfterGracefulStop && _viewModel.HasActiveDownloads)
        {
            var answer = MessageBox.Show(
                "One or more downloads or livestream recordings are still active.\n\n" +
                "Click Yes to stop them safely (active livestream recordings will be finalized and saved) and exit.\n" +
                "Click No to keep Tidejar running.",
                "Active Downloads in Progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            IsEnabled = false;
            Title = "Tidejar — Finalizing active operations...";

            try
            {
                await _viewModel.ShutdownActiveDownloadsAsync();
            }
            finally
            {
                _allowCloseAfterGracefulStop = true;
                Close();
            }

            return;
        }

        base.OnClosing(e);
    }
}
