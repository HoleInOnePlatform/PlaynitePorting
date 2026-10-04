using Playnite.HoleInOne;
using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class AccountLoginWindow : Playnite.Controls.WindowBase
    {
        public AccountLogin Account { get; private set; }
        private bool closed;

        public AccountLoginWindow(string backendUrl)
        {
            Title = "HoleInOne 로그인";
            Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            SetResourceReference(StyleProperty, "StandardWindowStyle");
            Resources.Add(typeof(TextBlock), Application.Current.FindResource("BaseTextBlockStyle"));
            ShowMinimizeButton = false; ShowMaximizeButton = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(32) }; Content = panel;
            panel.Children.Add(new TextBlock { Text = "HoleInOne", FontSize = 30, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "로그인하고 바로 플레이하세요.", Margin = new Thickness(0, 8, 0, 28) });
            panel.Children.Add(new TextBlock { Text = "아이디", Margin = new Thickness(0, 0, 0, 6) });
            var username = new TextBox { MinHeight = 36, MaxLength = 128 }; panel.Children.Add(username);
            panel.Children.Add(new TextBlock { Text = "비밀번호", Margin = new Thickness(0, 16, 0, 6) });
            var password = new PasswordBox { MinHeight = 36, MaxLength = 256 }; panel.Children.Add(password);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 42, Margin = new Thickness(0, 14, 0, 10) };
            panel.Children.Add(status);
            var submit = new Button { Content = "로그인", IsDefault = true, MinHeight = 42 }; panel.Children.Add(submit);
            var cancel = new Button { Content = "취소", IsCancel = true, MinHeight = 32, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(cancel);
            var cancellation = new CancellationTokenSource();
            Closed += (sender, args) => { closed = true; cancellation.Cancel(); password.Clear(); };
            Loaded += (sender, args) => username.Focus();
            submit.Click += async (sender, args) =>
            {
                if (!submit.IsEnabled) return;
                if (string.IsNullOrWhiteSpace(username.Text) || password.Password.Length == 0)
                { status.Text = "아이디와 비밀번호를 입력해 주세요."; return; }
                submit.IsEnabled = false; username.IsEnabled = false; password.IsEnabled = false;
                status.Text = "로그인 중…";
                try
                {
                    using (var backend = new BackendConnection(backendUrl))
                        Account = await backend.LoginAsync(username.Text.Trim(), password.Password, cancellation.Token);
                    if (!closed) DialogResult = true;
                }
                catch (LoginRequiredException error) { if (!closed) status.Text = error.Message; }
                catch (OperationCanceledException) { if (!closed) status.Text = "서버 응답이 늦어지고 있습니다. 잠시 후 다시 시도해 주세요."; }
                catch (HttpRequestException error)
                {
                    if (!closed) status.Text = error.Message.Contains("5분") ? error.Message : "서버에 연결할 수 없습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.";
                }
                catch (Exception) { if (!closed) status.Text = "로그인할 수 없습니다. 잠시 후 다시 시도해 주세요."; }
                finally
                {
                    password.Clear();
                    if (!closed) { submit.IsEnabled = true; username.IsEnabled = true; password.IsEnabled = true; password.Focus(); }
                }
            };
        }
    }

    // Kept in memory for this launch only. No password or session token is written to disk.
    public static class LauncherLogin
    {
        private static AccountLogin account;
        private static string service;
        public static string Username => account?.Username;
        public static bool EnsureSignedIn(LauncherSettings settings)
        {
            if (!settings.RequireLogin) return true;
            if (service == settings.BackendUrl && account != null && account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return true;
            Forget();
            var dialog = new AccountLoginWindow(settings.BackendUrl) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true) return false;
            account = dialog.Account; service = settings.BackendUrl;
            return true;
        }
        public static string TokenFor(LauncherSettings settings) => settings.RequireLogin
            ? (service == settings.BackendUrl ? account?.AccessToken : null) : settings.BearerToken;
        public static void Forget() { account = null; service = null; }
    }
}
