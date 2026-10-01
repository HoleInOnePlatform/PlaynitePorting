using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Playnite.GameLink
{
    internal static class BackendConnectionDialog
    {
        internal static bool EnsureConfigured(bool force = false)
        {
            NativeBackendConfiguration current = null; string error = null;
            try { current = NativeBackendConfiguration.Load(); }
            catch (NativeBackendConfigurationException e) { error = e.Message; }
            if (current != null && !force) return true;
            var app = System.Windows.Application.Current;
            if (app == null) throw new NativeBackendConfigurationException("즉시 플레이의 백엔드 연결 설정이 필요합니다.");
            if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Show(current, error));
            return Show(current, error);
        }
        private static bool Show(NativeBackendConfiguration current, string error)
        {
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "즉시 플레이 백엔드 연결", FontSize = 22, Margin = new Thickness(0, 0, 0, 16) });
            panel.Children.Add(new TextBlock { Text = "홀인원 백엔드 주소와 발급받은 인증 토큰을 입력해 주세요.\n로컬 개발 백엔드는 영상 재생을 지원하지 않습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            panel.Children.Add(new TextBlock { Text = "백엔드 주소" });
            var url = new TextBox { Text = current?.Address.AbsoluteUri ?? "", Margin = new Thickness(0, 6, 0, 12), MinHeight = 30 };
            panel.Children.Add(url); panel.Children.Add(new TextBlock { Text = "인증 토큰" });
            var token = new PasswordBox { Margin = new Thickness(0, 6, 0, 12), MinHeight = 30 };
            // Do not display, copy or prefill a saved secret.
            panel.Children.Add(token);
            var message = new TextBlock { Text = error ?? "토큰은 이 Windows 사용자 계정으로 암호화해 저장합니다.", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(message);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 80, Padding = new Thickness(8) };
            var save = new Button { Content = "저장하고 연결", IsDefault = true, MinWidth = 120, Padding = new Thickness(8), Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
            var window = new Window { Title = "홀인원 연결 설정", Content = panel, Width = 560, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White, Foreground = Brushes.Black };
            cancel.Click += (s, e) => { window.DialogResult = false; };
            save.Click += (s, e) =>
            {
                try
                {
                    var value = new NativeBackendConfiguration(url.Text, string.IsNullOrEmpty(token.Password) && current?.Address.AbsoluteUri == url.Text.Trim() ? current.Token : token.Password);
                    value.Save();
                    // A settings edit explicitly replaces process-level developer overrides.
                    Environment.SetEnvironmentVariable("HIO_BACKEND_URL", null);
                    Environment.SetEnvironmentVariable("HIO_USER_TOKEN", null);
                    token.Clear(); window.DialogResult = true;
                }
                catch (NativeBackendConfigurationException ex) { message.Text = ex.Message; message.Foreground = Brushes.Firebrick; }
                catch { message.Text = "연결 설정을 저장할 수 없습니다. 파일 접근 권한을 확인해 주세요."; message.Foreground = Brushes.Firebrick; }
            };
            window.Loaded += (s, e) => url.Focus();
            return window.ShowDialog() == true;
        }
    }
}
