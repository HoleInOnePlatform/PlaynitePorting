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

        private static string ActiveSteamId()
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            var account = value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
            if (account == 0) throw new InvalidOperationException("Steam에 로그인한 뒤 다시 시도해 주세요.");
            return (76561197960265728UL + account).ToString(CultureInfo.InvariantCulture);
        }

        public async void Start(Game game)
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
            Game localGame = null;
            bool cloudLaunchAllowed = false;
            try
            {
                var key = BuiltinStoreLibrary.GameKey(game);
                var settings = LauncherSettings.Load();
                if (!timing.Run("ensure_login", () => LauncherLogin.EnsureSignedIn(settings))) return;
                backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings));
                preparation = CloudPreparation.Get(settings);
                LogManager.GetLogger().Info("[HIO-PERF] link trace=" + timing.Id + " preparation=" + preparation.TimingId);
                try { session = await timing.RunAsync("prepare_and_claim_session", () => preparation.PlayAsync(key, play.Closed.Token)); }
                catch (LoginRequiredException) when (settings.RequireLogin)
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
                if (session.LaunchDeferred)
                {
                    await timing.RunAsync("allow_game_launch", () => backend.AllowPreparedLaunchAsync(session.SessionId, play.Closed.Token));
                }
                cloudLaunchAllowed = true;
                entryTiming.Complete();
                using (var installationTiming = timing.Begin("local_installation_wait"))
                {
                    if (!game.IsInstalling && !game.IsInstalled) editor.InstallGame(game);
                    while (true)
                    {
                        play.Closed.Token.ThrowIfCancellationRequested();
                        game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                        if (game.IsInstalled && !game.IsInstalling) break;
                        // Store refreshes can briefly clear installation flags. Keep observing while
                        // this webview is open so an early completion or a resumed download is not lost.
                        await Task.Delay(1000, play.Closed.Token);
                    }

                    installationTiming.Complete();
                }

                prompt = new DownloadHandoffPrompt(play.View.WindowHost, play.View.OverlayHost);
                LogManager.GetLogger().Info("Local installation complete; handoff prompt shown for " + key);
                var clicked = new TaskCompletionSource<bool>();
                prompt.TransferRequested += (sender, args) => clicked.TrySetResult(true);
                try { await timing.RunAsync("notify_download_complete", () => backend.DownloadCompleteAsync(session.SessionId, play.Closed.Token)); }
                catch (Exception error) when (!play.Closed.IsCancellationRequested)
                {
                    LogManager.GetLogger().Warn(error, "Download completion notification failed.");
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
                        var status = await handoffTiming.RunAsync("request_export", () => backend.StartExportAsync(session.SessionId, play.Closed.Token));
                        if (!System.Text.RegularExpressions.Regex.IsMatch(status.RequestId ?? "", "^[a-zA-Z0-9_-]{1,128}$"))
                            throw new InvalidDataException("잘못된 세이브 전환 식별자임.");
                        var requestId = status.RequestId;
                        LogManager.GetLogger().Info("[HIO-PERF] link trace=" + handoffTiming.Id + " export=" + requestId);
                        using (var exportWait = handoffTiming.Begin("server_export_wait"))
                        {
                            while (true)
                            {
                                play.Closed.Token.ThrowIfCancellationRequested();
                                prompt.ThrowIfExpired();
                                if (status.GameId != key || status.RequestId != requestId) throw new InvalidDataException("다른 게임 또는 전환 요청의 응답임.");
                                if (status.Status == "ready" || status.Status == "no_save") break;
                                if (status.Status == "failed") throw new InvalidOperationException(status.FailureMessage());
                                if (status.Status != "exporting" && status.Status != "preparing") throw new InvalidDataException("잘못된 세이브 전환 상태임.");
                                prompt.SetBusy(true, status.Status == "preparing" ? "받은 파일에서 세이브 데이터를 준비하는 중입니다." : "클라우드 게임을 종료하고 저장된 데이터를 가져오는 중입니다.");
                                await Task.Delay(1000, play.Closed.Token);
                                status = await backend.GetExportAsync(session.SessionId, play.Closed.Token);
                            }
                            exportWait.Complete();
                        }
                        if (status.Status == "ready")
                        {
                            // Save destinations and Steam identity are irrelevant when there is no save.
                            var roots = LocalRoots(game.InstallDirectory);
                            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
                            var account = value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
                            var accountId = account == 0 ? null : account.ToString(CultureInfo.InvariantCulture);
                            var steamId = account == 0 ? null : (76561197960265728UL + account).ToString(CultureInfo.InvariantCulture);
                            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                            if (accountId != null && !string.IsNullOrEmpty(steamPath))
                            {
                                roots["steamUserData"] = Path.Combine(steamPath, "userdata", accountId);
                                roots["steamUserDataRoot"] = Path.Combine(steamPath, "userdata");
                            }
                            var work = Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "SaveHandoffs", session.SessionId, Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(work);
                            var archive = Path.Combine(work, "saves.zip");
                            prompt.SetBusy(true, "세이브 데이터를 다운로드하는 중입니다.");
                            await handoffTiming.RunAsync("download_and_verify_archive", () => backend.DownloadExportAsync(session.SessionId, status, archive, play.Closed.Token));
                            prompt.ThrowIfExpired();
                            game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                            if (game.IsRunning || !game.IsInstalled || game.IsInstalling ||
                                !string.Equals(game.InstallDirectory, roots["install"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("로컬 게임 상태 또는 설치 위치가 변경됨. 전환 중단.");
                            prompt.SetBusy(true, "세이브를 검증하고 저장된 진행 상황을 준비하는 중입니다.");
                            var restore = await handoffTiming.RunAsync("validate_and_stage_saves", () => Task.Run(
                                () => ExportSaveInstaller.Prepare(archive, key, roots, steamId, accountId, Path.Combine(work, "restore"), play.Closed.Token),
                                play.Closed.Token));
                            prompt.ThrowIfExpired();
                            game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                            if (game.IsRunning || game.IsLaunching || !game.IsInstalled || game.IsInstalling ||
                                !string.Equals(game.InstallDirectory, roots["install"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("로컬 게임 상태 또는 설치 위치가 변경됨. 전환 중단.");
                            if (steamId != null && ActiveSteamId() != steamId)
                                throw new InvalidOperationException("Steam 계정이 변경되어 전환을 중단함.");
                            prompt.SetBusy(true, "기존 세이브를 백업하고 클라우드 세이브를 적용하는 중입니다.");
                            await handoffTiming.RunAsync("backup_and_commit_saves", () => Task.Run(() => restore.Commit(play.Closed.Token), play.Closed.Token));
                            // Persist cleanup, then send independently of local launch and webview cancellation.
                            handoffTiming.Run("persist_receipt", () => receipts.Enqueue(session.SessionId, requestId, status.Sha256));
                        }
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
                    if (play.View != null) { play.View.WindowHost.Closed -= closed; play.View.Dispose(); }
                    active.Remove(game.Id);
                    // A manual close or timeout also requests cloud termination. Server deadline is the fallback.
                    if (backend != null && session != null && localGame == null)
                    {
                        try { await backend.StopCloudAsync(session.SessionId, CancellationToken.None); }
                        catch (Exception error) { LogManager.GetLogger().Warn(error, "Cloud session termination request failed."); }
                    }
                    if (session != null && preparation != null) await preparation.PlayFinishedAsync();
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

        private static Dictionary<string, string> LocalRoots(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Path.IsPathRooted(installDirectory) || !Directory.Exists(installDirectory))
                throw new InvalidDataException("설치된 게임 경로를 확인하지 못함.");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["roaming"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["local"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["localLow"] = Path.Combine(profile, "AppData", "LocalLow"),
                ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ["savedGames"] = Path.Combine(profile, "Saved Games"),
                ["profile"] = profile, ["install"] = installDirectory
            };
        }
    }
}
