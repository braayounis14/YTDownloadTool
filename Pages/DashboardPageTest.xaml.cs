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
    public partial class DashboardPageTest : Page
    {
        public DashboardPageTest()
        {
            InitializeComponent(); // must exist now
            DataContext = new DashboardPageViewModel();
        }
    }
}

