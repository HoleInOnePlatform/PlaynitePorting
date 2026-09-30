using Playnite.Controls;
using Playnite.SDK.Models;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Playnite.Windows
{
    public partial class DownloadStatusWindow : WindowBase
    {
        private readonly Game game;
        private readonly DispatcherTimer refreshTimer;
        private bool refreshing;

        public DownloadStatusWindow(Game game)
        {
            InitializeComponent();
            var standardWindowStyle = TryFindResource("StandardWindowStyle") as Style;
            if (standardWindowStyle != null)
            {
                Style = standardWindowStyle;
            }

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
                var status = await Task.Run(() => LocalDownloadStatus.Read(game));
                if (!IsLoaded)
                {
                    return;
                }

                StatusText.Text = status;
            }
            catch (Exception)
            {
                StatusText.Text = "다운로드 상태를 읽을 수 없음";
            }
            finally
            {
                refreshing = false;
            }
        }

    }
}
