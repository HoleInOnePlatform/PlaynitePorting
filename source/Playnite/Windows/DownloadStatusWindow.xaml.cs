using System.Windows;

namespace Playnite.Windows
{
    public partial class DownloadStatusWindow : Window
    {
        public DownloadStatusWindow(string gameName)
        {
            InitializeComponent();
            GameName.Text = gameName;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
