using Playnite.HoleInOne;
using System;
using System.IO;
using System.Threading;
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
            if (!string.IsNullOrWhiteSpace(LauncherLogin.Username))
            {
                panel.Children.Add(new TextBlock { Text = "로그인 계정: " + LauncherLogin.Username, Margin = new Thickness(0, 0, 0, 16) });
                var logout = new Button { Content = "로그아웃", Margin = new Thickness(0, 0, 0, 16) };
                panel.Children.Add(logout);
                logout.Click += async (sender, args) =>
                {
                    if (InstantPlayCoordinator.ActiveStreamCount != 0)
                    { MessageBox.Show("클라우드 게임 창을 닫은 뒤 로그아웃해 주세요.", Title); return; }
                    logout.IsEnabled = false;
                    try
                    {
                        var settings = LauncherSettings.Load();
                        await CloudPreparation.ResetAsync();
                        using (var backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings)))
                            await backend.LogoutAsync(CancellationToken.None);
                        LauncherLogin.Forget(); Close();
                        if (!LauncherLogin.EnsureSignedIn(settings)) Application.Current.Shutdown();
                        else CloudPreparation.Begin();
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("로그아웃 요청을 완료하지 못했습니다. 인터넷 연결을 확인해 주세요.", Title);
                        logout.IsEnabled = true;
                    }
                };
            }
            AddDevelopmentServerSettings(panel);
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

        private void AddDevelopmentServerSettings(Panel panel)
        {
            var settings = LauncherSettings.Load();
            if (!settings.CanChangeDevelopmentBackend) return;
            panel.Children.Add(new TextBlock { Text = "개발 서버 주소", Margin = new Thickness(0, 4, 0, 6) });
            var address = new TextBox { Text = settings.BackendUrl, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(address);
            panel.Children.Add(new TextBlock
            {
                Text = "EC2 퍼블릭 IP만 입력해도 됨. 저장한 주소는 다음 게임 실행부터 적용됨.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var check = new Button { Content = "연결 확인", Margin = new Thickness(0, 0, 8, 0), MinWidth = 100 };
            var save = new Button { Content = "주소 저장", MinWidth = 100 };
            actions.Children.Add(check); actions.Children.Add(save); panel.Children.Add(actions);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16) };
            panel.Children.Add(status);
            var closed = new CancellationTokenSource();
            Closed += (sender, args) => { closed.Cancel(); closed.Dispose(); };
            address.TextChanged += (sender, args) => status.Text = "";
            check.Click += async (sender, args) =>
            {
                check.IsEnabled = save.IsEnabled = address.IsEnabled = false;
                status.Text = "연결 확인 중…";
                try
                {
                    var url = DevelopmentServiceAddress.Normalize(address.Text);
                    using (var backend = new BackendConnection(url, LauncherSettings.Load().BearerToken))
                        await backend.CheckDevelopmentHealthAsync(closed.Token);
                    status.Text = "연결 성공. 이 주소를 사용하려면 ‘주소 저장’을 눌러 주세요.";
                }
                catch (LoginRequiredException) { status.Text = "접속 인증을 확인하지 못함. 팀 배포본의 접속 설정을 확인해 주세요."; }
                catch (OperationCanceledException) { if (IsVisible) status.Text = "연결 시간이 초과됨. EC2 실행 상태와 주소를 확인해 주세요."; }
                catch (InvalidDataException error) { status.Text = error.Message; }
                catch (Exception) { status.Text = "연결 실패. 서버 주소·EC2 실행 상태·5010 접속 허용을 확인해 주세요."; }
                finally { check.IsEnabled = save.IsEnabled = address.IsEnabled = true; }
            };
            save.Click += async (sender, args) =>
            {
                if (InstantPlayCoordinator.ActiveStreamCount != 0)
                { status.Text = "클라우드 게임 창을 닫은 뒤 서버 주소를 변경해 주세요."; return; }
                try
                {
                    var url = DevelopmentServiceAddress.Normalize(address.Text);
                    save.IsEnabled = check.IsEnabled = address.IsEnabled = false;
                    await CloudPreparation.ResetAsync();
                    LauncherSettings.SaveDevelopmentBackendAddress(url);
                    CloudPreparation.Begin();
                    address.Text = url;
                    status.Text = "주소 저장 완료. 새 서버에서 게임을 준비합니다. 앱을 다시 켜도 유지됩니다.";
                }
                catch (InvalidDataException error) { status.Text = error.Message; }
                catch (Exception) { status.Text = "주소를 저장하지 못함. 사용자 데이터 폴더의 쓰기 권한을 확인해 주세요."; }
                finally { save.IsEnabled = check.IsEnabled = address.IsEnabled = true; }
            };
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
