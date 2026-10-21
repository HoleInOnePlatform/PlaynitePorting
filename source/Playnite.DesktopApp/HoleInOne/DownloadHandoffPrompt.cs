using Playnite.HoleInOne;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Playnite.DesktopApp.HoleInOne
{
    // Native controls remain visible when the remote streaming page navigates or reloads.
    internal sealed class DownloadHandoffPrompt : IDisposable
    {
        private readonly Window window;
        private readonly Panel host;
        private readonly Border banner;
        private Border transferScreen;
        private TextBlock transferStatus;
        private readonly TextBlock remaining;
        private readonly TextBlock status;
        private readonly Button transfer;
        private readonly DispatcherTimer timer;
        private readonly HandoffDeadline deadline;
        private readonly Func<DateTimeOffset> utcNow;
        public event EventHandler TransferRequested;

        public DownloadHandoffPrompt(Window window, Panel overlayHost, Func<DateTimeOffset> utcNow = null)
        {
            this.window = window;
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            deadline = new HandoffDeadline(this.utcNow());
            host = overlayHost ?? throw new InvalidOperationException("게임 웹뷰의 표시 영역을 찾을 수 없음.");
            var layout = new StackPanel();
            layout.Children.Add(new TextBlock
            {
                Text = "로컬 다운로드 완료", Foreground = Brushes.White,
                FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
            });
            layout.Children.Add(new TextBlock
            {
                Text = "로컬 다운로드가 완료되었습니다. 게임을 저장 후 전환 버튼을 눌러주세요. 60분 후 게임이 자동으로 종료되니, 세이브 데이터에 유의해주세요",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 14
            });
            var actions = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            remaining = new TextBlock { Foreground = Brushes.White, FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
            transfer = new Button { Content = "전환", Padding = new Thickness(24, 8, 24, 8), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            transfer.Click += TransferClicked;
            DockPanel.SetDock(transfer, Dock.Right);
            actions.Children.Add(transfer);
            var countdown = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            countdown.Children.Add(new TextBlock { Text = "자동 종료까지", Foreground = Brushes.LightGray, FontSize = 12 });
            countdown.Children.Add(remaining);
            actions.Children.Add(countdown);
            layout.Children.Add(actions);
            status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGoldenrodYellow,
                FontSize = 13, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed
            };
            layout.Children.Add(status);
            banner = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(242, 28, 35, 47)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(72, 89, 112)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(16),
                MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Child = layout
            };
            host.Children.Add(banner);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += Tick;
            window.Closed += WindowClosed;
            Tick(null, EventArgs.Empty);
            timer.Start();
        }

        public void SetBusy(bool busy, string message)
        {
            if (busy && transferScreen == null)
            {
                transferStatus = new TextBlock { Foreground = Brushes.White, FontSize = 20, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620, Margin = new Thickness(32), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                transferScreen = new Border { Background = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                    Child = transferStatus, Focusable = true };
                host.Children.Insert(host.Children.IndexOf(banner), transferScreen);
                transferScreen.Focus();
            }
            if (transferStatus != null) transferStatus.Text = "로컬 게임으로 전환\n\n" + message + "\n\n완료될 때까지 이 창을 열어 두세요.";
            transfer.IsEnabled = !busy && !deadline.HasExpired(utcNow());
            transfer.Content = busy ? "전환 중…" : "전환";
            status.Text = message;
            status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        public void ThrowIfExpired()
        {
            if (deadline.HasExpired(utcNow()))
            {
                window.Close();
                throw new OperationCanceledException("60분 전환 대기 시간이 종료됨.");
            }
        }

        private void TransferClicked(object sender, RoutedEventArgs e)
        {
            if (!transfer.IsEnabled) return;
            if (deadline.HasExpired(utcNow())) { window.Close(); return; }
            SetBusy(true, "로컬 게임으로 전환하는 중입니다. 잠시 기다려주세요.");
            TransferRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Tick(object sender, EventArgs e)
        {
            var seconds = deadline.RemainingSeconds(utcNow());
            remaining.Text = string.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
            if (seconds == 0) { timer.Stop(); window.Close(); }
        }

        private void WindowClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= Tick;
            transfer.Click -= TransferClicked;
            window.Closed -= WindowClosed;
            host.Children.Remove(banner);
            if (transferScreen != null) host.Children.Remove(transferScreen);
        }
    }
}
