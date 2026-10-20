using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class LocalDownloadPrompt : IDisposable
    {
        private readonly Panel host;
        private readonly Border banner;
        private readonly TextBlock status;
        private readonly Button retry;
        public event EventHandler RetryRequested;
        public event EventHandler OpenSteamRequested;

        public LocalDownloadPrompt(Panel host, bool showSteam = true)
        {
            this.host = host ?? throw new InvalidOperationException("게임 웹뷰의 표시 영역을 찾을 수 없음.");
            var layout = new StackPanel();
            layout.Children.Add(new TextBlock { Text = "PC 다운로드", FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 14 };
            layout.Children.Add(status);
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var openSteam = new Button { Content = "PC Steam 열기", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8) };
            openSteam.Click += (_, __) => OpenSteamRequested?.Invoke(this, EventArgs.Empty);
            retry = new Button { Content = "다운로드 다시 시도", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
            retry.Click += (_, __) =>
            {
                if (!retry.IsEnabled) return;
                retry.IsEnabled = false;
                RetryRequested?.Invoke(this, EventArgs.Empty);
            };
            if (showSteam) actions.Children.Add(openSteam);
            actions.Children.Add(retry);
            layout.Children.Add(actions);
            banner = new Border { Child = layout, MaxWidth = 440, Padding = new Thickness(20), Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(242, 28, 35, 47)), CornerRadius = new CornerRadius(12) };
            host.Children.Add(banner);
        }

        public void Update(string message, bool canRetry)
        {
            status.Text = message;
            retry.IsEnabled = canRetry;
        }

        public void Dispose() => host.Children.Remove(banner);
    }
}
