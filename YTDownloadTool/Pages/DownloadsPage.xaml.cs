using System;
using System.Collections.Generic;
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
using YTDownloadTool.ViewModels;

namespace YTDownloadTool.Pages
{
    /// <summary>
    /// Interaction logic for DownloadsPage.xaml
    /// </summary>
    public partial class DownloadsPage : Page
    {
        public DownloadsPage()
        {
            InitializeComponent();
            DataContext = new DashboardViewModel();
        }

    }
}
