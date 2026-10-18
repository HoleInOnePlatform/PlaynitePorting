using Playnite.HoleInOne;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;
            var panel = new StackPanel { Margin = new Thickness(20) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
            AddStorageSettings(panel);
            AddProtectedGamesSettings(panel);
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

        private void AddStorageSettings(Panel panel)
        {
            var settings = LauncherSettings.Load();
            panel.Children.Add(new TextBlock { Text = "저장공간 관리", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 8) });
            panel.Children.Add(new TextBlock { Text = "Steam 설치 폴더 (등록된 라이브러리의 steamapps\\common)" });
            var steam = new TextBox { Text = settings.SteamInstallDirectory, Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(steam);
            panel.Children.Add(new TextBlock { Text = "Epic 설치 폴더" });
            var epic = new TextBox { Text = settings.DownloadDirectory, Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(epic);
            panel.Children.Add(new TextBlock { Text = "최대 저장공간 (두 폴더 합계, GiB / 1 GiB = 1,024³ 바이트)" });
            var maximum = new TextBox { Text = settings.MaximumStorageGiB.ToString(), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(maximum);
            panel.Children.Add(new TextBlock { Text = "설치 시 용량 한도를 넘으면 가장 오래 플레이하지 않은 게임부터 정리 대상을 계산하고, 추가 확인 없이 해당 게임을 자동 제거합니다. 플레이 기록이 없는 게임을 먼저 정리하며 실행 중인 게임은 제외합니다. 폴더 변경은 다음 설치부터 적용되며 기존 게임을 이동하지 않습니다. Steam 새 위치는 Steam 설정 → 저장공간에서 먼저 등록해 주세요.", TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var save = new Button { Content = "저장공간 설정 저장", Margin = new Thickness(0, 0, 8, 0) };
            var refresh = new Button { Content = "현재 사용량 확인" };
            actions.Children.Add(save); actions.Children.Add(refresh); panel.Children.Add(actions);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
            panel.Children.Add(status);
            save.Click += async (sender, args) =>
            {
                save.IsEnabled = refresh.IsEnabled = steam.IsEnabled = epic.IsEnabled = maximum.IsEnabled = false;
                try
                {
                    if (StorageManagement.IsBusy) throw new InvalidOperationException("게임 설치가 끝난 뒤 저장공간 설정을 변경해 주세요.");
                    if (!long.TryParse(maximum.Text, out var capacity)) throw new ArgumentException("최대 용량은 GiB 단위의 정수로 입력해 주세요.");
                    var updated = LauncherSettings.Load();
                    updated.SteamInstallDirectory = steam.Text.Trim(); updated.DownloadDirectory = epic.Text.Trim();
                    updated.MaximumStorageGiB = capacity;
                    updated.ValidateStorage();
                    status.Text = "현재 사용량 확인 중…";
                    var used = await System.Threading.Tasks.Task.Run(() => StorageManagement.Used(updated));
                    if (used > checked((ulong)capacity * 1024 * 1024 * 1024) &&
                        MessageBox.Show(this, $"현재 사용량({used / (1024d * 1024 * 1024):N2} GiB)이 설정할 최대 용량({capacity:N0} GiB)보다 큽니다.\n\n설정을 저장해도 지금 게임을 삭제하지 않습니다. 다음 설치 시 공간 확보가 필요하면 보호하지 않은 기존 게임을 추가 확인 없이 자동 제거합니다.\n\n이 용량으로 저장하시겠습니까?",
                            "저장공간 한도 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    { status.Text = "설정 저장을 취소했습니다. 기존 한도를 유지합니다."; return; }
                    if (StorageManagement.IsBusy) throw new InvalidOperationException("게임 설치가 시작되어 설정을 저장하지 않았습니다. 설치 완료 후 다시 시도해 주세요.");
                    if (!IsVisible) return;
                    updated.ProtectedGameIds = LauncherSettings.Load().ProtectedGameIds;
                    updated.Save();
                    status.Text = "저장했습니다. 다음 설치부터 적용됩니다.";
                }
                catch (Exception error) { status.Text = error.Message; }
                finally { save.IsEnabled = refresh.IsEnabled = steam.IsEnabled = epic.IsEnabled = maximum.IsEnabled = true; }
            };
            refresh.Click += async (sender, args) =>
            {
                refresh.IsEnabled = false; status.Text = "저장된 설치 폴더의 사용량 계산 중…";
                try
                {
                    var current = LauncherSettings.Load();
                    var used = await System.Threading.Tasks.Task.Run(() => StorageManagement.Used(current));
                    status.Text = $"현재 사용량: {used / (1024d * 1024 * 1024):N2} GiB / 최대 {current.MaximumStorageGiB:N0} GiB";
                }
                catch (Exception error) { status.Text = "사용량 확인 실패: " + error.Message; }
                finally { refresh.IsEnabled = true; }
            };
        }

        private void AddProtectedGamesSettings(Panel panel)
        {
            panel.Children.Add(new TextBlock { Text = "자동 제거 제외 게임", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 6) });
            panel.Children.Add(new TextBlock { Text = "체크한 게임은 공간 확보 과정에서 절대 제거하지 않습니다. 변경은 즉시 저장됩니다. 보호한 게임도 현재 사용량에는 포함되며, 공간이 부족하면 설치를 중단합니다.", TextWrapping = TextWrapping.Wrap });
            var list = new StackPanel();
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 4) });
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
            var refresh = new Button { Content = "설치된 게임 목록 새로고침" };
            panel.Children.Add(refresh); panel.Children.Add(status);
            Action reload = () =>
            {
                list.Children.Clear();
                var settings = LauncherSettings.Load();
                var games = DesktopApplication.Current.Database.Games.Where(game => game.IsInstalled).OrderBy(game => game.Name).ToList();
                if (games.Count == 0) list.Children.Add(new TextBlock { Text = "현재 설치된 게임이 없습니다." });
                foreach (var game in games)
                {
                    var checkbox = new CheckBox { Content = new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.White },
                        IsChecked = settings.IsGameProtected(game.Id), Margin = new Thickness(0, 4, 0, 4), ToolTip = game.InstallDirectory };
                    var reverting = false;
                    RoutedEventHandler save = (sender, args) =>
                    {
                        if (reverting) return;
                        try
                        {
                            if (StorageManagement.IsBusy) throw new InvalidOperationException("설치 또는 공간 확보가 진행 중입니다. 완료 후 보호 설정을 변경해 주세요.");
                            var current = LauncherSettings.Load();
                            current.ProtectedGameIds = current.ProtectedGameIds ?? new HashSet<Guid>();
                            if (checkbox.IsChecked == true) current.ProtectedGameIds.Add(game.Id);
                            else current.ProtectedGameIds.Remove(game.Id);
                            current.Save();
                            status.Text = checkbox.IsChecked == true ? game.Name + ": 자동 제거에서 제외했습니다." : game.Name + ": 자동 제거 제외를 해제했습니다.";
                        }
                        catch (Exception error)
                        {
                            status.Text = error.Message;
                            reverting = true;
                            checkbox.IsChecked = !checkbox.IsChecked;
                            reverting = false;
                        }
                    };
                    checkbox.Checked += save; checkbox.Unchecked += save; list.Children.Add(checkbox);
                }
            };
            refresh.Click += (sender, args) => reload();
            reload();
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
