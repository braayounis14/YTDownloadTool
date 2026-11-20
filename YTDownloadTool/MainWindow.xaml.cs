using System.Windows;
using YTDownloadTool.Pages;

namespace YTDownloadTool
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            MainFrame.Navigate(new DownloadsPage());
        }

        private void Downloads_Click(object sender, RoutedEventArgs e)
        {
            MainFrame.Navigate(new DownloadsPage());
        }

        private void Library_Click(object sender, RoutedEventArgs e)
        {
            MainFrame.Navigate(new LibraryPage());
        }
    }
}
