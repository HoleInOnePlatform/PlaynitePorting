using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class GamePreparationWindow : Playnite.Controls.WindowBase
    {
        public GamePreparationWindow(string gameName)
        {
            Title = "HoleInOne · 게임 준비";
            Width = 460; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowMinimizeButton = false; ShowMaximizeButton = false; ResizeMode = ResizeMode.NoResize;
            SetResourceReference(StyleProperty, "StandardWindowStyle");
            Resources.Add(typeof(TextBlock), Application.Current.FindResource("BaseTextBlockStyle"));
            var panel = new StackPanel { Margin = new Thickness(24) };
            Content = panel;
            panel.Children.Add(new TextBlock { Text = gameName, FontSize = 22, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock {
                Text = "게임 서버를 준비하고 있습니다. 준비가 끝나면 플레이 화면이 열립니다.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) });
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
            var cancel = new Button { Content = "취소", Margin = new Thickness(0, 18, 0, 0), MinHeight = 34 };
            cancel.Click += (sender, args) => Close();
            panel.Children.Add(cancel);
        }
    }
}
