using Playnite.HoleInOne;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class InstantPlayCoordinator
    {
        private sealed class ActivePlay { public Playnite.WebView.WebView View; public readonly CancellationTokenSource Closed = new CancellationTokenSource(); }
        private readonly Dictionary<Guid, ActivePlay> active = new Dictionary<Guid, ActivePlay>();
        private readonly DesktopGamesEditor editor;
        private readonly Action<Game> launch;
        public InstantPlayCoordinator(DesktopGamesEditor editor, Action<Game> launch) { this.editor = editor; this.launch = launch; }
        public bool Focus(Guid id)
        {
            if (!active.TryGetValue(id, out var play)) return false;
            play.View?.WindowHost.Activate();
            return true;
        }
        public async void Start(Game game)
        {
            if (Focus(game.Id)) return;
            var play = new ActivePlay();
            active.Add(game.Id, play);
            EventHandler closed = (sender, args) => play.Closed.Cancel();
            try
            {
                var key = BuiltinStoreLibrary.GameKey(game);
                var settings = LauncherSettings.Load();
                using (var backend = new BackendConnection(settings.BackendUrl, settings.BearerToken))
                {
                    var session = await backend.CreateSessionAsync(play.Closed.Token, key);
                    play.View = new Playnite.WebView.WebView(new WebViewSettings { WindowWidth = 1280, WindowHeight = 800 });
                    play.View.EnableGameInput();
                    play.View.WindowHost.Closed += closed;
                    play.View.Navigate(session.StreamUrl);
                    play.View.Open();
                    if (!game.IsInstalling && !game.IsInstalled) editor.InstallGame(game);
                    var handoff = key == "steam-2868840" ? new HandoffSession(backend, session.SessionId) : null;
                    while (true)
                    {
                        await Task.Delay(1000, play.Closed.Token);
                        game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                        if (!game.IsInstalled && !game.IsInstalling) throw new InvalidOperationException("로컬 다운로드가 시작되지 않았거나 중단됨. 자동 전환을 중단함.");
                        if (handoff == null) continue;
                        var ticket = await handoff.PollAsync(game.IsInstalled, play.Closed.Token);
                        if (ticket == null) continue;
                        play.Closed.Token.ThrowIfCancellationRequested();
                        InstallLoader(game.InstallDirectory);
                        Sts2ModSetup.EnableForCurrentSteamUser();
                        HandoffSession.WriteTicket(game.InstallDirectory, ticket);
                        play.View.WindowHost.Closed -= closed;
                        play.View.Close();
                        launch(game);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* Store installation belongs to the editor, not this window. */ }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "HoleInOne", MessageBoxButton.OK, MessageBoxImage.Error);
                // Keep an already opened stream usable after a handoff failure.
                if (play.View != null && !play.Closed.IsCancellationRequested)
                {
                    try { await Task.Delay(Timeout.Infinite, play.Closed.Token); } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                if (play.View != null) { play.View.WindowHost.Closed -= closed; play.View.Dispose(); }
                active.Remove(game.Id);
                play.Closed.Dispose();
            }
        }

        private static void InstallLoader(string directory)
        {
            if (!Path.IsPathRooted(directory)) throw new InvalidDataException("설치 경로가 없음.");
            var source = Path.Combine(PlaynitePaths.ProgramPath, "Mods", "GameLoader");
            var destination = Path.Combine(directory, "mods", "GameLoader");
            foreach (var name in new[] { "GameLoader.dll", "GameLoader.json" })
                if (!File.Exists(Path.Combine(source, name))) throw new FileNotFoundException("GameLoader 배포 파일이 없음. 빌드 도구 준비가 필요함.");
            Directory.CreateDirectory(destination);
            foreach (var name in new[] { "GameLoader.dll", "GameLoader.json" }) File.Copy(Path.Combine(source, name), Path.Combine(destination, name), true);
        }
    }
}
