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

        public async void Start(Game game)
        {
            if (Focus(game.Id)) return;
            var play = new ActivePlay();
            active.Add(game.Id, play);
            ActiveStreamCount++;
            EventHandler closed = (sender, args) => play.Closed.Cancel();
            BackendConnection backend = null;
            CloudSession session = null;
            PreparedSessionManager preparation = null;
            DownloadHandoffPrompt prompt = null;
            LocalDownloadPrompt downloadPrompt = null;
            Game localGame = null;
            SteamCloudBridge steamCloud = null;
            try
            {
                var key = BuiltinStoreLibrary.GameKey(game);
                var settings = LauncherSettings.Load();
                if (!LauncherLogin.EnsureSignedIn(settings)) return;
                if (settings.RequireLogin && (!SteamLinkContext.IsConfigured || !(await SteamLinkContext.RefreshAsync(play.Closed.Token)).IsLinked)) throw new SteamLinkException("steam_link_required");
                if (settings.SteamCloudUploadEnabled && key.StartsWith("steam-", StringComparison.Ordinal) && uint.TryParse(key.Substring(6), out var steamAppId))
                {
                    // Capture the remote baseline before cloud gameplay, not after the export arrives.
                    steamCloud = new SteamCloudBridge();
                    await steamCloud.PrepareAsync(steamAppId, play.Closed.Token);
                }
                backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings));
                preparation = CloudPreparation.Get(settings);
                try { session = await preparation.PlayAsync(key, play.Closed.Token); }
                catch (LoginRequiredException) when (settings.RequireLogin)
                {
                    // A backend restart invalidates in-memory tokens. Reauthenticate before retrying creation.
                    await CloudPreparation.ResetAsync();
                    LauncherLogin.Forget();
                    if (!LauncherLogin.EnsureSignedIn(settings)) return;
                    backend.Dispose();
                    backend = new BackendConnection(settings.BackendUrl, LauncherLogin.TokenFor(settings));
                    preparation = CloudPreparation.Get(settings);
                    session = await preparation.PlayAsync(key, play.Closed.Token);
                }
                play.View = new Playnite.WebView.WebView(new WebViewSettings { WindowWidth = 1280, WindowHeight = 800 });
                play.View.EnableGameMode();
                play.View.WindowHost.Closed += closed;
                play.View.Navigate(session.StreamUrl);
                play.View.Open();
                Game ReadLocalGame() => editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                var download = new LocalDownloadSession(() => ReadLocalGame().IsInstalled, () => ReadLocalGame().IsInstalling,
                    () => { if (key.StartsWith("steam-", StringComparison.Ordinal)) SteamLinkContext.RequireLocalAccount(); },
                    () => editor.InstallGame(ReadLocalGame()));
                downloadPrompt = new LocalDownloadPrompt(play.View.OverlayHost, key.StartsWith("steam-", StringComparison.Ordinal));
                var retryRequested = true;
                string openSteamError = null;
                downloadPrompt.RetryRequested += (_, __) => { retryRequested = true; openSteamError = null; };
                downloadPrompt.OpenSteamRequested += (_, __) =>
                {
                    try { SteamLinkContext.OpenLocalSteam(); openSteamError = null; }
                    catch (Exception error) { openSteamError = error.Message; downloadPrompt.Update(openSteamError, download.CanRetry); }
                };
                while (true)
                {
                    play.Closed.Token.ThrowIfCancellationRequested();
                    game = ReadLocalGame();
                    if (retryRequested)
                    {
                        retryRequested = false;
                        download.Request();
                        if (download.CanRetry)
                            LogManager.GetLogger().Info("PC download deferred; user can retry without restarting the cloud session.");
                    }
                    download.Refresh();
                    if (download.Complete) break;
                    downloadPrompt.Update(openSteamError ?? download.Message, download.CanRetry);
                    // Store refreshes can briefly clear installation flags. Keep observing while
                    // this webview is open so an early completion or a resumed download is not lost.
                    await Task.Delay(1000, play.Closed.Token);
                }

                downloadPrompt.Dispose();
                downloadPrompt = null;
                prompt = new DownloadHandoffPrompt(play.View.WindowHost, play.View.OverlayHost);
                LogManager.GetLogger().Info("Local installation complete; handoff prompt shown for " + key);
                var clicked = new TaskCompletionSource<bool>();
                prompt.TransferRequested += (sender, args) => clicked.TrySetResult(true);
                try { await backend.DownloadCompleteAsync(session.SessionId, play.Closed.Token); }
                catch (Exception error) when (!play.Closed.IsCancellationRequested)
                {
                    LogManager.GetLogger().Warn(error, "Download completion notification failed.");
                    prompt.SetBusy(false, "서버에 다운로드 완료를 알리지 못했습니다. 웹뷰는 60분 후 종료됩니다.");
                }

                while (true)
                {
                    using (play.Closed.Token.Register(() => clicked.TrySetCanceled())) await clicked.Task;
                    try
                    {
                        prompt.ThrowIfExpired();
                        game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                        if (!game.IsInstalled || game.IsInstalling || game.IsRunning) throw new InvalidOperationException("설치가 완료되고 로컬 게임이 종료된 상태에서 전환해야 함.");
                        var status = await backend.StartExportAsync(session.SessionId, play.Closed.Token);
                        if (!System.Text.RegularExpressions.Regex.IsMatch(status.RequestId ?? "", "^[a-zA-Z0-9_-]{1,128}$"))
                            throw new InvalidDataException("잘못된 세이브 전환 식별자임.");
                        var requestId = status.RequestId;
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
                        if (status.Status == "ready")
                        {
                            // Save destinations and Steam identity are irrelevant when there is no save.
                            var roots = LocalRoots(game.InstallDirectory);
                            SteamLinkContext.RequireLocalAccount();
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
                            await backend.DownloadExportAsync(session.SessionId, status, archive, play.Closed.Token);
                            prompt.ThrowIfExpired();
                            game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                            if (game.IsRunning || !game.IsInstalled || game.IsInstalling ||
                                !string.Equals(game.InstallDirectory, roots["install"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("로컬 게임 상태 또는 설치 위치가 변경됨. 전환 중단.");
                            // Keep the archive/backups when Steam upload fails; never launch through a known conflict.
                            if (steamCloud != null)
                            {
                                prompt.SetBusy(true, "저장된 진행 상황을 준비하는 중입니다.");
                                await steamCloud.SyncAsync(archive, status.Sha256, play.Closed.Token);
                            }
                            prompt.ThrowIfExpired();
                            game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                            if (game.IsRunning || game.IsLaunching || !game.IsInstalled || game.IsInstalling ||
                                !string.Equals(game.InstallDirectory, roots["install"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("로컬 게임 상태 또는 설치 위치가 변경됨. 전환 중단.");
                            if (steamId != null && SteamCloudBridge.ActiveSteamId() != steamId)
                                throw new InvalidOperationException("Steam 계정이 변경되어 전환을 중단함.");
                            prompt.SetBusy(true, "기존 세이브를 백업하고 클라우드 세이브를 적용하는 중입니다.");
                            await Task.Run(() => ExportSaveInstaller.Apply(archive, key, roots, steamId, accountId, Path.Combine(work, "restore"), play.Closed.Token), play.Closed.Token);
                            // The receipt is cleanup only: failure must not invalidate successfully applied saves.
                            try { await backend.ReceiveExportAsync(session.SessionId, requestId, status.Sha256, play.Closed.Token); }
                            catch (Exception error) when (!play.Closed.IsCancellationRequested)
                            { LogManager.GetLogger().Warn(error, "Applied save export receipt failed."); }
                        }
                        play.Closed.Token.ThrowIfCancellationRequested();
                        prompt.ThrowIfExpired();
                        game = editor.Database.Games.Get(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                        if (!game.IsInstalled || game.IsInstalling || game.IsRunning || game.IsLaunching)
                            throw new InvalidOperationException("로컬 게임 상태가 변경되어 전환을 중단함.");
                        localGame = game;
                        break;
                    }
                    catch (OperationCanceledException) when (play.Closed.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        clicked = new TaskCompletionSource<bool>();
                        prompt.SetBusy(false, "전환 실패: " + error.Message + " 로컬 게임은 실행하지 않았습니다.");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (error is LoginRequiredException) LauncherLogin.Forget();
                MessageBox.Show(error.Message, "HoleInOne", MessageBoxButton.OK,
                    error is CloudPlayUnavailableException ? MessageBoxImage.Warning : MessageBoxImage.Error);
                if (play.View != null && !play.Closed.IsCancellationRequested)
                {
                    try { await Task.Delay(Timeout.Infinite, play.Closed.Token); } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                prompt?.Dispose();
                downloadPrompt?.Dispose();
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
                steamCloud?.Dispose();
                play.Closed.Dispose();
                ActiveStreamCount--;
            }
            if (localGame != null)
            {
                try { launch(localGame); }
                catch (Exception error) { MessageBox.Show("로컬 게임 실행에 실패함: " + error.Message, "HoleInOne", MessageBoxButton.OK, MessageBoxImage.Error); }
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
