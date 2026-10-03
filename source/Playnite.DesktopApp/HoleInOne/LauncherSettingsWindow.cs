using Playnite.HoleInOne;
using System;
using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class LauncherSettingsWindow : Playnite.Controls.WindowBase
    {
        public LauncherSettingsWindow()
        {
            Title = "HoleInOne 설정"; Width = 600; SizeToContent = SizeToContent.Height;
            SetResourceReference(StyleProperty, "StandardWindowStyle");
            Resources.Add(typeof(TextBlock), Application.Current.FindResource("BaseTextBlockStyle"));
            ShowMinimizeButton = false; ShowMaximizeButton = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
            AddStoreSettings(panel, "Steam 연동 설정", BuiltinStoreLibrary.SteamId);
            AddStoreSettings(panel, "Epic 연동 설정", BuiltinStoreLibrary.EpicId);
            panel.Children.Add(new TextBlock
            {
                Text = "스토어 설정에서 계정 연결과 미설치 게임 가져오기를 켜고 인증한 뒤 확인을 누르면 됨. 라이브러리를 새로고침하면 보유 게임이 표시됨.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10)
            });
            var close = new Button { Content = "닫기", Margin = new Thickness(0, 5, 0, 0) }; panel.Children.Add(close);
            close.Click += (sender, args) => Close();
        }

        private void AddStoreSettings(Panel panel, string label, Guid id)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 5, 0, 5) };
            button.Click += (sender, args) =>
            {
                try { DesktopApplication.Current.MainModel.OpenPluginSettings(id); }
                catch (Exception error) { MessageBox.Show(error.Message, Title); }
            };
            panel.Children.Add(button);
        }

    }
}
