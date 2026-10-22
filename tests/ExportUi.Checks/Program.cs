using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.DesktopApp.HoleInOne;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var now = DateTimeOffset.Parse("2026-10-03T12:00:00+09:00");
        var panel = new Grid();
        var game = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(47, 68, 61)),
            Child = new TextBlock { Text = "게임 화면 영역", Foreground = Brushes.White, FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var overlay = new Grid();
        panel.Children.Add(game);
        panel.Children.Add(overlay);
        var window = new Window { Content = panel };
        var closed = false;
        window.Closed += (_, __) => closed = true;
        var clicks = 0;
        using (var notice = new LocalDownloadNotice(overlay, () => now))
        {
            Check(overlay.Children.Count == 0, "normal download adds no overlay or permanent PC download panel");
            var opened = 0;
            notice.OpenSteamRequested += (_, __) => opened++;
            notice.Show("로컬 다운로드를 시작할 수 없습니다. 같은 계정으로 PC Steam에 로그인하면 다운로드를 시작합니다. 클라우드 플레이는 계속할 수 있습니다.", true);
            Layout(panel, 1100, 650);
            var preview = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32);
            preview.Render(panel);
            var downloadImage = new PngBitmapEncoder();
            downloadImage.Frames.Add(BitmapFrame.Create(preview));
            using (var output = File.Create(args[0] + "-local-download.png")) downloadImage.Save(output);
            var buttons = FindButtons(overlay).ToArray();
            Check(!buttons.Any(b => (string)b.Content == "다운로드 다시 시도"), "no persistent retry control");
            Check(VisualTreeHelper.HitTest(panel, new Point(1050, 100)).VisualHit == game, "failure notice leaves top-right game input unobstructed");
            buttons.Single(b => (string)b.Content == "PC Steam 열기").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(opened == 1 && overlay.Children.Count == 0, "opening Steam dismisses the one-time notice");
            notice.Show("로컬 다운로드가 중단되었습니다. PC Steam에서 상태를 확인해 주세요.", false);
            Check(FindButtons(overlay).Single(b => (string)b.Content == "PC Steam 열기").Visibility == Visibility.Collapsed,
                "download errors only offer Steam launch when waiting for authentication");
            now = now.AddSeconds(12);
            typeof(LocalDownloadNotice).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(notice, new object[] { null, EventArgs.Empty });
            Check(overlay.Children.Count == 0, "failure notice disappears after twelve seconds");
            notice.Show("test", true);
            FindButtons(overlay).Single(b => (string)b.Content == "닫기").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(overlay.Children.Count == 0, "user can dismiss notice immediately");
        }
        Check(overlay.Children.Count == 0, "failure notice is removed on disposal");
        using (var prompt = new DownloadHandoffPrompt(window, overlay, () => now))
        {
            prompt.TransferRequested += (_, __) => clicks++;
            var tick = typeof(DownloadHandoffPrompt).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
            var button = Find<Button>(panel);
            Check(FindText(panel, "60:00"), "prompt starts at 60:00");
            Check(FindText(panel, "로컬 다운로드가 완료되었습니다. 게임을 저장 후 전환 버튼을 눌러주세요. 60분 후 게임이 자동으로 종료되니, 세이브 데이터에 유의해주세요"), "exact requested warning is displayed");
            Layout(panel, 1100, 650);
            Check(game.ActualWidth == 1100 && game.ActualHeight == 650, "toast does not shrink the game viewport");
            var toast = (Border)overlay.Children[0];
            var origin = toast.TranslatePoint(new Point(0, 0), panel);
            Check(origin.X > 600 && origin.Y == 16 && toast.ActualWidth <= 440, "toast overlays the top right of the game");
            Check(VisualTreeHelper.HitTest(panel, new Point(20, 400)).VisualHit == game, "game receives pointer input outside toast");
            var bitmap = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(args[0])) encoder.Save(output);
            Layout(panel, 360, 640);
            origin = toast.TranslatePoint(new Point(0, 0), panel);
            Check(origin.X >= 0 && origin.X + toast.ActualWidth <= 360 && toast.ActualHeight <= 640,
                "toast fits a narrow viewport without clipping");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(clicks == 1 && !button.IsEnabled, "transition button rejects duplicate clicks while busy");
            Layout(panel, 1100, 650);
            Check(overlay.Children.Count == 2 && VisualTreeHelper.HitTest(panel, new Point(20, 400)).VisualHit != game,
                "handoff screen covers remote reconnect controls and blocks underlying game input");
            var transferring = new RenderTargetBitmap(1100, 650, 96, 96, PixelFormats.Pbgra32);
            transferring.Render(panel);
            var transferImage = new PngBitmapEncoder(); transferImage.Frames.Add(BitmapFrame.Create(transferring));
            using (var output = File.Create(args[0] + "-transferring.png")) transferImage.Save(output);
            now = now.AddMinutes(30); prompt.SetBusy(false, "전환 실패: 재시도 가능");
            Layout(panel, 1100, 650);
            Check(overlay.Children.Count == 1 && VisualTreeHelper.HitTest(panel, new Point(20, 400)).VisualHit == game,
                "failed handoff removes transfer screen and restores game input");
            tick.Invoke(prompt, new object[] { null, EventArgs.Empty });
            Check(FindText(panel, "30:00") && button.IsEnabled, "retry preserves original deadline");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(panel, 1100, 650);
            Check(clicks == 2 && overlay.Children.Count == 2 && !button.IsEnabled &&
                VisualTreeHelper.HitTest(panel, new Point(20, 400)).VisualHit != game,
                "retry recreates transfer screen and blocks duplicate requests");
            now = now.AddMinutes(30); tick.Invoke(prompt, new object[] { null, EventArgs.Empty });
            Check(closed && clicks == 2, "60 minute expiry closes the window without switching");
        }
        Check(overlay.Children.Count == 0, "toast is removed when disposed");
        Console.WriteLine("Local download and handoff UI checks passed.");
    }

    private static System.Collections.Generic.IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        if (parent is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in FindButtons(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Layout(FrameworkElement panel, double width, double height)
    { panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout(); }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var found = Find<T>(VisualTreeHelper.GetChild(root, i)); if (found != null) return found; }
        return null;
    }
    private static bool FindText(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(VisualTreeHelper.GetChild(root, i), text)) return true;
        return false;
    }
}
