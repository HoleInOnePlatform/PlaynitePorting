using Playnite.HoleInOne;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.HoleInOne
{
    internal sealed class SaveRecoveryWindow : Playnite.Controls.WindowBase
    {
        private readonly StackPanel rows = new StackPanel();
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly CancellationTokenSource closed = new CancellationTokenSource();
        private bool busy;
        private bool loading;
        private readonly System.Collections.Generic.HashSet<string> completed = new System.Collections.Generic.HashSet<string>();
        public SaveRecoveryWindow()
        {
            Title = "미완료 세이브 복구"; Width = 580; Height = 420;
            SetResourceReference(StyleProperty, "StandardWindowStyle");
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "현재 계정의 기존 전환입니다. 게임 설치를 완료하고 로컬 게임을 종료한 뒤 적용하세요.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(status);
            var retry = new Button { Content = "목록 다시 조회", Margin = new Thickness(0, 8, 0, 0) };
            retry.Click += async (s, e) => { if (!busy) await LoadAsync(); };
            panel.Children.Add(retry); panel.Children.Add(rows);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Closed += (s, e) => closed.Cancel();
            Loaded += async (s, e) => await LoadAsync();
        }
        private async Task LoadAsync()
        {
            if (loading) return;
            loading = true;
            rows.IsEnabled = false;
            var generation = LauncherLogin.Generation;
            try
            {
                var settings = LauncherSettings.Load();
                if (!LauncherLogin.EnsureSignedIn(settings)) return;
                using (var backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings)))
                {
                    generation = LauncherLogin.Generation;
                    var pending = await backend.GetRecoveriesAsync(closed.Token);
                    if (closed.IsCancellationRequested || generation != LauncherLogin.Generation) return;
                    rows.Children.Clear();
                    status.Text = pending.Length == 0 ? "미완료 전환이 없습니다." : "기존 요청의 상태를 확인했습니다.";
                    foreach (var item in pending)
                    {
                        if (completed.Contains(generation + ":" + item.SessionId + ":" + item.Transfer.GameId)) continue;
                        var game = DesktopApplication.Current.Database.Games.FirstOrDefault(g =>
                            (g.PluginId == BuiltinStoreLibrary.SteamId || g.PluginId == BuiltinStoreLibrary.EpicId) && BuiltinStoreLibrary.GameKey(g) == item.Transfer.GameId);
                        var state = item.Transfer.Status;
                        var restartRecovery = state == "failed" && item.Transfer.ErrorCode == "recovery_required";
                        var label = restartRecovery ? "서버 재시작 후 복구 재개" : state == "ready" ? "복원 준비 완료" : state == "no_save" ? "세이브 없음" :
                            state == "failed" ? "전환 실패" : state == "preparing" || state == "exporting" ? "전환 진행 중" : "확인이 필요한 상태";
                        var button = new Button { Content = (game?.Name ?? item.Transfer.GameId) + " · " + label,
                            IsEnabled = restartRecovery || state == "ready" || state == "preparing" || state == "exporting", Margin = new Thickness(0, 8, 0, 0) };
                        button.Click += async (s, e) => await RestoreAsync(item, settings, generation);
                        rows.Children.Add(button);
                    }
                }
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
            catch (OperationCanceledException) { status.Text = generation != LauncherLogin.Generation ? "계정이 변경되었습니다. 목록을 다시 조회해 주세요." : "목록 조회 시간이 초과되었습니다. 목록 다시 조회를 눌러 주세요."; }
            catch (Exception) { status.Text = "복구 목록을 확인하지 못했습니다. 다시 로그인한 뒤 열어 주세요."; }
            finally { loading = false; rows.IsEnabled = !busy; }
        }
        private async Task RestoreAsync(PendingSaveRecovery item, LauncherSettings settings, long generation)
        {
            if (busy || completed.Contains(generation + ":" + item.SessionId + ":" + item.Transfer.GameId)) return;
            busy = true; rows.IsEnabled = false;
            var steamGeneration = SteamLinkContext.Generation;
            try
            {
                Action requireLive = () => {
                    closed.Token.ThrowIfCancellationRequested();
                    if (generation != LauncherLogin.Generation) throw new InvalidOperationException("계정이 변경되었습니다.");
                    SteamLinkContext.RequireGeneration(steamGeneration);
                };
                requireLive();
                var game = DesktopApplication.Current.Database.Games.FirstOrDefault(g =>
                    (g.PluginId == BuiltinStoreLibrary.SteamId || g.PluginId == BuiltinStoreLibrary.EpicId) && BuiltinStoreLibrary.GameKey(g) == item.Transfer.GameId);
                if (game == null || !game.IsInstalled || game.IsInstalling || game.IsRunning || game.IsLaunching)
                    throw new InvalidOperationException("해당 게임을 설치하고 실행 중인 로컬 게임을 종료해 주세요.");
                await SteamLinkContext.RefreshAsync(closed.Token);
                bool restored;
                using (var backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings)))
                    restored = await SaveHandoffWorkflow.ApplyAsync(game, item.Transfer.GameId,
                        new CloudSession { SessionId = item.SessionId, GameId = item.Transfer.GameId }, backend, new ExportReceipts(settings),
                        id => DesktopApplication.Current.Database.Games.Get(id), message => status.Text = message, requireLive, closed.Token,
                        new PerformanceTrace("save_recovery", line => Playnite.SDK.LogManager.GetLogger().Info(line)), recoveryOnly: true);
                completed.Add(generation + ":" + item.SessionId + ":" + item.Transfer.GameId);
                rows.Children.Clear();
                await LoadAsync();
                status.Text = restored
                    ? "세이브를 복원했습니다. 라이브러리에서 로컬 게임을 실행할 수 있습니다."
                    : "복원할 클라우드 세이브가 없습니다. 로컬 세이브는 변경하지 않았습니다.";
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
            catch (OperationCanceledException) { status.Text = generation != LauncherLogin.Generation || steamGeneration != SteamLinkContext.Generation
                ? "계정 연결이 변경되어 복원을 중단했습니다. 목록을 다시 조회해 주세요."
                : "복원 요청 시간이 초과되었습니다. 기존 파일과 복구 기록을 유지합니다. 다시 시도해 주세요."; }
            catch (Exception error) { status.Text = error is InvalidOperationException || error is System.Net.Http.HttpRequestException ? error.Message : "복원하지 못했습니다. 기존 파일과 복구 기록을 유지합니다."; }
            finally { busy = false; rows.IsEnabled = true; }
        }
    }
}
