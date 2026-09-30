using Playnite.SDK.Models;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Playnite.Windows
{
    public partial class DownloadStatusWindow : Window
    {
        private readonly Game game;
        private readonly DispatcherTimer refreshTimer;
        private bool refreshing;

        public DownloadStatusWindow(Game game)
        {
            InitializeComponent();
            this.game = game;
            GameName.Text = game.Name;
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            refreshTimer.Tick += async (sender, args) => await RefreshProgressAsync();
            Loaded += async (sender, args) =>
            {
                refreshTimer.Start();
                await RefreshProgressAsync();
            };
            Closed += (sender, args) => refreshTimer.Stop();
        }

        private async Task RefreshProgressAsync()
        {
            if (refreshing)
            {
                return;
            }

            refreshing = true;
            try
            {
                var result = await Task.Run(() => LocalDownloadProgress.Read(game));
                if (!IsLoaded)
                {
                    return;
                }

                ProgressText.Text = result.Percent.HasValue ? $"{result.Percent.Value:0}%" : "—";
                DownloadProgress.Value = result.Percent ?? 0;
                StatusText.Text = result.Status;
            }
            catch (Exception)
            {
                ProgressText.Text = "—";
                DownloadProgress.Value = 0;
                StatusText.Text = "다운로드 상태를 읽을 수 없음";
            }
            finally
            {
                refreshing = false;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
