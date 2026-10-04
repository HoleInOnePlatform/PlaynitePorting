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
            now = now.AddMinutes(30); prompt.SetBusy(false, "전환 실패: 재시도 가능");
            tick.Invoke(prompt, new object[] { null, EventArgs.Empty });
            Check(FindText(panel, "30:00") && button.IsEnabled, "retry preserves original deadline");
            now = now.AddMinutes(30); tick.Invoke(prompt, new object[] { null, EventArgs.Empty });
            Check(closed && clicks == 1, "60 minute expiry closes the window without switching");
        }
        Check(overlay.Children.Count == 0, "toast is removed when disposed");
        Console.WriteLine("10 UI checks passed.");
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
