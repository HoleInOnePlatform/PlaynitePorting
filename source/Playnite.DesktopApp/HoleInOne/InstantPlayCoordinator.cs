using Microsoft.Win32;
using Playnite.HoleInOne;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class InstantPlayCoordinator
    {
        public static int ActiveStreamCount { get; private set; }
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

        public async void Start(Game game) { await StartAsync(game); }
        public async Task StartAsync(Game game)
        {
            if (Focus(game.Id)) return;
            var timing = new PerformanceTrace("cloud_entry", line => LogManager.GetLogger().Info(line));
            var entryTiming = timing.Begin("entry_until_launch_allowed");
            var visibleTiming = timing.Begin("entry_until_game_running");
            PerformanceTrace handoffTiming = null;
            PerformanceTrace.Step handoffTotal = null;
            var play = new ActivePlay();
            active.Add(game.Id, play);
            ActiveStreamCount++;
            EventHandler closed = (sender, args) => play.Closed.Cancel();
            BackendConnection backend = null;
            CloudSession session = null;
            PreparedSessionManager preparation = null;
            DownloadHandoffPrompt prompt = null;
            LocalDownloadNotice downloadNotice = null;
            Game localGame = null;
            bool cloudLaunchAllowed = false;
            try
            {
                var key = BuiltinStoreLibrary.GameKey(game);
                var settings = LauncherSettings.Load();
                if (!timing.Run("ensure_login", () => LauncherLogin.EnsureSignedIn(settings))) return;
                if ((!SteamLinkContext.IsConfigured || !(await SteamLinkContext.RefreshAsync(play.Closed.Token)).IsLinked)) throw new SteamLinkException("steam_link_required");
                backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings));
                preparation = CloudPreparation.Get(settings);
                LogManager.GetLogger().Info("[HIO-PERF] link trace=" + timing.Id + " preparation=" + preparation.TimingId);
                try { session = await timing.RunAsync("prepare_and_claim_session", () => preparation.PlayAsync(key, play.Closed.Token)); }
                catch (LoginRequiredException)
                {
                    // A backend restart invalidates in-memory tokens. Reauthenticate before retrying creation.
                    await CloudPreparation.ResetAsync();
                    LauncherLogin.Forget();
                    if (!timing.Run("ensure_login", () => LauncherLogin.EnsureSignedIn(settings))) return;
                    backend.Dispose();
                    backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings));
                    preparation = CloudPreparation.Get(settings);
                    LogManager.GetLogger().Info("[HIO-PERF] link trace=" + timing.Id + " preparation=" + preparation.TimingId);
                    session = await timing.RunAsync("prepare_and_claim_session", () => preparation.PlayAsync(key, play.Closed.Token));
                }
                var receipts = new ExportReceipts(settings);
                receipts.Start();
                LogManager.GetLogger().Info("[HIO-PERF] link trace=" + timing.Id + " session=" + session.SessionId);
                using (var webviewTiming = timing.Begin("webview_create_and_open"))
                {
                    play.View = new Playnite.WebView.WebView(new WebViewSettings { WindowWidth = 1280, WindowHeight = 800 });
                    play.View.EnableGameMode();
                    play.View.EnablePerformanceLogging(session.SessionId, line =>
                    {
                        LogManager.GetLogger().Info(line);
                        if (line.Contains("stage=page_to_game_running event=end") && line.Contains("outcome=ok"))
                            visibleTiming.Complete();
                    });
                    play.View.WindowHost.Closed += closed;
                    play.View.Navigate(session.StreamUrl);
                    play.View.Open();
                    webviewTiming.Complete();
                }
                play.Closed.Token.ThrowIfCancellationRequested();
                cloudLaunchAllowed = true;
                entryTiming.Complete();
                Game ReadLocalGame() => editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                downloadNotice = new LocalDownloadNotice(play.View.OverlayHost);
                downloadNotice.OpenSteamRequested += (_, __) =>
                {
                    try { SteamLinkContext.OpenLocalSteam(); }
                    catch (Exception error) { downloadNotice.Show(error.Message, false); }
                };
                await LocalInstallationObserver.WaitAsync(ReadLocalGame, key, () => editor.InstallGame(ReadLocalGame()),
                    (message, waitingForAccount) => {
                        if (message == null) downloadNotice.Hide(); else downloadNotice.Show(message, waitingForAccount);
                    }, play.Closed.Token);
                downloadNotice.Dispose();
                downloadNotice = null;
                prompt = new DownloadHandoffPrompt(play.View.WindowHost, play.View.OverlayHost);
                LogManager.GetLogger().Info("Local installation complete; handoff prompt shown for " + key);
                var clicked = new TaskCompletionSource<bool>();
                prompt.TransferRequested += (sender, args) => clicked.TrySetResult(true);
                try { await timing.RunAsync("notify_download_complete", () => backend.DownloadCompleteAsync(session.SessionId, play.Closed.Token)); }
                catch (Exception error) when (!play.Closed.IsCancellationRequested)
                {
                    LogManager.GetLogger().Warn("Download completion notification failed (" + error.GetType().Name + ").");
                    prompt.SetBusy(false, "서버에 다운로드 완료를 알리지 못했습니다. 웹뷰는 60분 후 종료됩니다.");
                }

                while (true)
                {
                    using (play.Closed.Token.Register(() => clicked.TrySetCanceled())) await clicked.Task;
                    handoffTiming = new PerformanceTrace("local_handoff", line => LogManager.GetLogger().Info(line));
                    handoffTotal = handoffTiming.Begin("handoff_until_local_launch_requested");
                    LogManager.GetLogger().Info("[HIO-PERF] link trace=" + handoffTiming.Id + " session=" + session.SessionId);
                    try
                    {
                        prompt.ThrowIfExpired();
                        game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                        if (!game.IsInstalled || game.IsInstalling || game.IsRunning) throw new InvalidOperationException("설치가 완료되고 로컬 게임이 종료된 상태에서 전환해야 함.");
                        await SaveHandoffWorkflow.ApplyAsync(game, key, session, backend, receipts,
                            id => editor.Database.Games.Get(id), message => prompt.SetBusy(true, message),
                            () => prompt.ThrowIfExpired(), play.Closed.Token, handoffTiming);
                        play.Closed.Token.ThrowIfCancellationRequested();
                        prompt.ThrowIfExpired();
                        game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                        if (!game.IsInstalled || game.IsInstalling || game.IsRunning || game.IsLaunching)
                            throw new InvalidOperationException("로컬 게임 상태가 변경되어 전환을 중단함.");
                        localGame = game;
                        break;
                    }
                    catch (OperationCanceledException error) when (play.Closed.IsCancellationRequested) { handoffTotal.Fail(error); throw; }
                    catch (Exception error)
                    {
                        LogManager.GetLogger().Warn("Save handoff failed for session " + session.SessionId + " (" + error.GetType().Name + ").");
                        handoffTotal.Fail(error);
                        clicked = new TaskCompletionSource<bool>();
                        prompt.SetBusy(false, "전환 실패: " + error.Message + " 로컬 게임은 실행하지 않았습니다.");
                    }
                }
            }
            catch (OperationCanceledException error) { entryTiming.Fail(error); visibleTiming.Fail(error); handoffTotal?.Fail(error); }
            catch (Exception error)
            {
                entryTiming.Fail(error); visibleTiming.Fail(error);
                handoffTotal?.Fail(error);
                if (error is LoginRequiredException) LauncherLogin.Forget();
                MessageBox.Show(error.Message, "HoleInOne", MessageBoxButton.OK,
                    error is CloudPlayUnavailableException ? MessageBoxImage.Warning : MessageBoxImage.Error);
                if (play.View != null && cloudLaunchAllowed && !play.Closed.IsCancellationRequested)
                {
                    try { await Task.Delay(Timeout.Infinite, play.Closed.Token); } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                entryTiming.Dispose();
                visibleTiming.Dispose();
                using (var cleanupTiming = (handoffTiming ?? timing).Begin("close_view_and_cleanup"))
                {
                    play.Closed.Cancel();
                    prompt?.Dispose();
                    downloadNotice?.Dispose();
                    if (play.View != null) { play.View.WindowHost.Closed -= closed; play.View.Dispose(); }
                    active.Remove(game.Id);
                    // A manual close or timeout also requests cloud termination. Server deadline is the fallback.
                    if (backend != null && session != null && localGame == null)
                    {
                        try { await backend.StopCloudAsync(session.SessionId, CancellationToken.None); }
                        catch (Exception error) { LogManager.GetLogger().Warn("Cloud session termination request failed (" + error.GetType().Name + ")."); }
                    }
                    try { if (session != null && preparation != null) await preparation.PlayFinishedAsync(); }
                    catch (Exception error) { LogManager.GetLogger().Warn("Prepared session cleanup deferred (" + error.GetType().Name + ")."); }
                    backend?.Dispose();
                    play.Closed.Dispose();
                    ActiveStreamCount--;
                    cleanupTiming.Complete();
                }
                if (localGame == null) handoffTotal?.Dispose();
            }
            if (localGame != null)
            {
                try { handoffTiming.Run("request_local_launch", () => launch(localGame)); handoffTotal.Complete(); }
                catch (Exception error) { handoffTotal.Fail(error); MessageBox.Show("로컬 게임 실행에 실패함: " + error.Message, "HoleInOne", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

    }
}
