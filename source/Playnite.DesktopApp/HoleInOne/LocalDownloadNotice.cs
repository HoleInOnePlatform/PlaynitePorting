using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Playnite.DesktopApp.HoleInOne
{
    // Failure-only notification. Normal downloads add nothing to the game overlay.
    public sealed class LocalDownloadNotice : IDisposable
    {
        private readonly Panel host;
        private readonly Border notice;
        private readonly TextBlock text;
        private readonly Button openSteam;
        private readonly DispatcherTimer timer;
        private readonly Func<DateTimeOffset> now;
        private DateTimeOffset until;
        public event EventHandler OpenSteamRequested;

        public LocalDownloadNotice(Panel host, Func<DateTimeOffset> now = null)
        {
            this.host = host ?? throw new InvalidOperationException("게임 웹뷰의 표시 영역을 찾을 수 없음.");
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 14 };
            var layout = new StackPanel();
            layout.Children.Add(text);
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            openSteam = new Button { Content = "PC Steam 열기", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
            openSteam.Click += (_, __) => { Hide(); OpenSteamRequested?.Invoke(this, EventArgs.Empty); };
            var close = new Button { Content = "닫기", Padding = new Thickness(10, 5, 10, 5) };
            close.Click += (_, __) => Hide();
            actions.Children.Add(openSteam);
            actions.Children.Add(close);
            layout.Children.Add(actions);
            notice = new Border { Child = layout, MaxWidth = 420, Padding = new Thickness(14), Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(242, 28, 35, 47)), CornerRadius = new CornerRadius(8) };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += Tick;
        }

        public void Show(string message, bool canOpenSteam)
        {
            text.Text = message;
            openSteam.Visibility = canOpenSteam ? Visibility.Visible : Visibility.Collapsed;
            if (!host.Children.Contains(notice)) host.Children.Add(notice);
            until = now().AddSeconds(12);
            timer.Start();
        }

        private void Tick(object sender, EventArgs args) { if (now() >= until) Hide(); }
        public void Hide() { timer.Stop(); host.Children.Remove(notice); }
        public void Dispose() { Hide(); timer.Tick -= Tick; }
    }
}
