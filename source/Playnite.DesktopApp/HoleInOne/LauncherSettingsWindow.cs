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
                        else { await SteamSetup.OnStartupAsync(); CloudPreparation.Begin(); }
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("로그아웃 요청을 완료하지 못했습니다. 인터넷 연결을 확인해 주세요.", Title);
                        logout.IsEnabled = true;
                    }
                };
            }
            AddDevelopmentServerSettings(panel);
            if (LauncherSettings.Load().RequireLogin)
            {
                var steam = new Button { Content="Steam 연결 관리", Margin=new Thickness(0,6,0,6) };
                steam.Click+=(sender,args)=>new SteamSetupWindow { Owner=this }.ShowDialog(); panel.Children.Add(steam);
                var saves=new CheckBox { Content="전환할 때 Steam Cloud에도 세이브 업로드", IsChecked=LauncherSettings.Load().SteamCloudUploadEnabled, Margin=new Thickness(0,6,0,12) };
                panel.Children.Add(saves);
                RoutedEventHandler changed=(sender,args)=>{
                    var settings=LauncherSettings.Load();
                    if(InstantPlayCoordinator.ActiveStreamCount!=0 || (saves.IsChecked==true && !SteamLinkContext.HasLocalLogin))
                    { MessageBox.Show("클라우드 이용을 종료하고 이 PC에서 Steam을 연결한 후 변경해 주세요.",Title); saves.IsChecked=settings.SteamCloudUploadEnabled; return; }
                    settings.SteamCloudUploadEnabled=saves.IsChecked==true; settings.Save();
                };
                saves.Checked+=changed; saves.Unchecked+=changed;
            }
            else
            {
                AddStoreSettings(panel, "Steam 연동 설정", BuiltinStoreLibrary.SteamId);
                AddSteamCloudSettings(panel);
            }
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

        private void AddSteamCloudSettings(Panel panel)
        {
            var enabled = new CheckBox { Content = "전환할 때 Steam Cloud에도 세이브 업로드 (시험 기능)",
                IsChecked = LauncherSettings.Load().SteamCloudUploadEnabled, Margin = new Thickness(0, 12, 0, 6) };
            panel.Children.Add(enabled);
            var status = new TextBlock { Text = SteamCloudBridge.IsLinked ? "Steam 세이브 계정 연결됨" : "사용하려면 Steam 세이브 계정을 연결해 주세요.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
            var connect = new Button { Content = "Steam 세이브 계정 연결", Margin = new Thickness(0, 0, 8, 0) };
            var unlink = new Button { Content = "연결 해제" };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(connect); actions.Children.Add(unlink); panel.Children.Add(actions); panel.Children.Add(status);
            var changing = false;
            RoutedEventHandler save = (sender, args) =>
            {
                if (changing) return;
                try
                {
                    if (InstantPlayCoordinator.ActiveStreamCount != 0) throw new InvalidOperationException("클라우드 게임 창을 닫은 뒤 변경해 주세요.");
                    if (enabled.IsChecked == true && !SteamCloudBridge.IsLinked) throw new InvalidOperationException("먼저 Steam 세이브 계정을 연결해 주세요.");
                    var settings = LauncherSettings.Load(); settings.SteamCloudUploadEnabled = enabled.IsChecked == true; settings.Save();
                    status.Text = settings.SteamCloudUploadEnabled ? "다음 클라우드 게임부터 적용됨. Steam Cloud의 파일도 변경되며, 문제가 있으면 전환을 보류함." : "Steam 세이브 자동 업로드 꺼짐";
                }
                catch (Exception error)
                {
                    status.Text = error.Message; changing = true;
                    enabled.IsChecked = LauncherSettings.Load().SteamCloudUploadEnabled; changing = false;
                }
            };
            enabled.Checked += save; enabled.Unchecked += save;
            connect.Click += async (sender, args) =>
            {
                if (InstantPlayCoordinator.ActiveStreamCount != 0) { status.Text = "클라우드 게임 창을 닫은 뒤 연결해 주세요."; return; }
                connect.IsEnabled = unlink.IsEnabled = enabled.IsEnabled = false;
                using (var cancel = new CancellationTokenSource())
                {
                    var dialog = new Playnite.Controls.WindowBase { Title = "Steam 세이브 계정 연결", Width = 360, Height = 430,
                        Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowMinimizeButton = false, ShowMaximizeButton = false };
                    dialog.SetResourceReference(StyleProperty, "StandardWindowStyle");
                    var body = new StackPanel { Margin = new Thickness(20) };
                    body.Children.Add(new TextBlock { Text = "Steam 모바일 앱으로 QR을 스캔해 현재 PC와 같은 계정으로 로그인해 주세요.", TextWrapping = TextWrapping.Wrap });
                    var picture = new Image { Width = 260, Height = 260, Margin = new Thickness(0, 16, 0, 0) };
                    body.Children.Add(picture); dialog.Content = body;
                    dialog.Closed += (s, e) => cancel.Cancel();
                    try
                    {
                        using (var bridge = new SteamCloudBridge())
                        {
                            dialog.Show();
                            await bridge.AuthenticateAsync(bytes =>
                            {
                                using (var memory = new MemoryStream(bytes))
                                {
                                    var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
                                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                    bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze(); picture.Source = bitmap;
                                }
                            }, cancel.Token);
                        }
                        status.Text = "계정 연결 완료. 자동 업로드를 사용하려면 위 옵션을 켜 주세요.";
                    }
                    catch (OperationCanceledException) { status.Text = "계정 연결 취소됨"; }
                    catch (Exception error) { status.Text = error.Message; }
                    finally { dialog.Close(); connect.IsEnabled = unlink.IsEnabled = enabled.IsEnabled = true; }
                }
            };
            unlink.Click += (sender, args) =>
            {
                if (InstantPlayCoordinator.ActiveStreamCount != 0) { status.Text = "클라우드 게임 창을 닫은 뒤 연결을 해제해 주세요."; return; }
                try
                {
                    enabled.IsChecked = false;
                    if (File.Exists(SteamCloudBridge.TokenPath)) File.Delete(SteamCloudBridge.TokenPath);
                    status.Text = "이 PC에 저장된 Steam 세이브 연결 정보 삭제 완료";
                }
                catch (Exception) { status.Text = "연결 정보를 삭제하지 못함. 잠시 후 다시 시도해 주세요."; }
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
