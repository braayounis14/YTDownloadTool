using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using YTDownloadTool.Pages;
using YTDownloadTool.Services;
using YTDownloadTool.ViewModels;

namespace YTDownloadTool
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            if (!AuthService.IsLoggedIn)
               // MainFrame.Navigate(new DashboardPage
                MainFrame.Navigate(new DownloadsPage());
            else
                        MainFrame.Navigate(new LoginPage());
        }

        private void Dashboard_Click(object sender, RoutedEventArgs e)
        {
            if (!AuthService.IsLoggedIn)
            {
                MainFrame.Navigate(new LoginPage());
                return;
            }

            MainFrame.Navigate(new DashboardPageTest());
        }

        private void Library_Click(object sender, RoutedEventArgs e)
        {
            if (!AuthService.IsLoggedIn)
            {
                MainFrame.Navigate(new LoginPage());
                return;
            }

            MainFrame.Navigate(new LibraryPage());
        }
    }
}