using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using System.IO;
using Playnite.SDK.Plugins;
using SteamLibrary.Services;
using System.Collections.Generic;

namespace SteamLibrary
{
    public class SteamInstallController : InstallController
    {
        private CancellationTokenSource watcherToken;
        private readonly IPlayniteAPI playniteApi;
        private Dictionary<string, DateTime> initialManifests;
        private readonly CancellationTokenSource requestLifetime = new CancellationTokenSource();

        public SteamInstallController(Game game, IPlayniteAPI playniteApi) : base(game)
        {
            Name = "Install using Steam";
            this.playniteApi = playniteApi;
        }

        public override void Dispose()
        {
            watcherToken?.Cancel();
            requestLifetime.Cancel();
        }

        public override async void Install(InstallActionArgs args)
        {
            try
            {
                Playnite.HoleInOne.SteamLinkContext.RequireLocalAccount();
                if (!Steam.IsInstalled)
                {
                    throw new Exception("Steam installation not found.");
                }

                var gameId = Game.ToSteamGameID();
                if (gameId.IsMod)
                {
                    throw new NotSupportedException("Installing mods is not supported.");
                }
                else
                {
                    await Task.Run(() =>
                    {
                        initialManifests = SteamInstallService.CaptureManifests(gameId.AppID, SteamLocalService.GetLibraryFolders());
                    });
                    using (var bridge = new Playnite.HoleInOne.SteamLinkBridge())
                        await bridge.RequestDownloadAsync(gameId.AppID, false, requestLifetime.Token);
                    requestLifetime.Token.ThrowIfCancellationRequested();
                    StartInstallWatcher();
                }
            }
            catch (OperationCanceledException)
            {
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
            }
            catch (Exception error)
            {
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                playniteApi.Dialogs.ShowErrorMessage(error.Message, "HoleInOne · 설치");
            }
        }

        public async void StartInstallWatcher()
        {
            watcherToken?.Cancel();
            watcherToken = new CancellationTokenSource();
            var token = watcherToken.Token;
            var id = Game.ToSteamGameID();
            var readFailed = false;
            var requestTimer = Stopwatch.StartNew();
            var requestAccepted = false;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!requestAccepted && requestTimer.Elapsed > TimeSpan.FromMinutes(2))
                        throw new TimeoutException("Steam이 다운로드 요청을 처리하지 못함. Steam 로그인 상태·게임 소유권·저장 공간을 확인한 뒤 다시 시도해 주세요.");
                    Playnite.SDK.Models.GameMetadata installedGame;
                    try
                    {
                        installedGame = await Task.Run(() =>
                        {
                            if (!requestAccepted)
                                requestAccepted = SteamInstallService.HasManifestChanged(initialManifests,
                                    SteamInstallService.CaptureManifests(id.AppID, SteamLocalService.GetLibraryFolders()));
                            return SteamLocalService.GetInstalledGame(id.AppID);
                        }, token);
                        readFailed = false;
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException)
                    {
                        // Steam rewrites manifests during installation. Keep watching after a temporary read failure.
                        if (!readFailed) LogManager.GetLogger().Warn(error, "Steam installation status read failed; retrying.");
                        readFailed = true;
                        await Task.Delay(1000, token);
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    if (installedGame != null)
                    {
                        InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = installedGame.InstallDirectory }));
                        return;
                    }
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                LogManager.GetLogger().Error(error, "Steam installation monitoring stopped.");
                if (!token.IsCancellationRequested)
                {
                    InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                    playniteApi.Dialogs.ShowErrorMessage(error.Message, "HoleInOne · 다운로드 시작 실패");
                }
            }
        }
    }

    public class SteamUninstallController : UninstallController
    {
        private readonly CancellationTokenSource watcherToken = new CancellationTokenSource();

        public override Task UninstallAsync(UninstallActionArgs args)
        {
            // The synchronous SDK entry point waits for a server acknowledgement.
            // Keep it off the UI thread; the caller handles request errors.
            return Task.Run(() => Uninstall(args));
        }

        public SteamUninstallController(Game game) : base(game)
        {
            Name = "Uninstall using Steam";
        }

        public override void Dispose()
        {
            watcherToken?.Cancel();
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            if (!Steam.IsInstalled)
            {
                throw new Exception("Steam installation not found.");
            }

            var gameId = Game.ToSteamGameID();
            if (gameId.IsMod)
            {
                throw new NotSupportedException("Uninstalling mods is not supported.");
            }
            else
            {
                var requestTimer = Stopwatch.StartNew();
                using (var bridge = new Playnite.HoleInOne.SteamLinkBridge())
                    bridge.RequestDownloadAsync(gameId.AppID, true, watcherToken.Token).GetAwaiter().GetResult();
                LogManager.GetLogger().Info($"Steam uninstall request dispatched in {requestTimer.ElapsedMilliseconds} ms.");
                WaitForUninstallAsync(gameId).GetAwaiter().GetResult();
            }
        }

        private async Task WaitForUninstallAsync(GameID id)
        {
            var token = watcherToken.Token;
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                // Presence in any registered library prevents a false completion.
                var present = SteamInstallService.CaptureManifests(id.AppID, SteamLocalService.GetLibraryFolders()).Count != 0;
                if (!present)
                {
                    InvokeOnUninstalled(new GameUninstalledEventArgs());
                    return;
                }
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            throw new TimeoutException("Steam 제거 완료를 확인하지 못했습니다. Steam 다운로드 상태를 확인해 주세요.");
        }
    }

    public class SteamPlayController : PlayController
    {
        private GameID gameId;
        private ProcessMonitor procMon;
        private Stopwatch stopWatch;
        private readonly SteamLibrarySettings settings;
        private readonly IPlayniteAPI playniteAPI;

        public SteamPlayController(Game game, SteamLibrarySettings settings, IPlayniteAPI playniteAPI) : base(game)
        {
            gameId = game.ToSteamGameID();
            Name = string.Format(ResourceProvider.GetString(LOC.SteamStartUsingClient), "Steam");
            this.settings = settings;
            this.playniteAPI = playniteAPI;
        }

        public override void Dispose()
        {
            procMon?.Dispose();
        }

        public override void Play(PlayActionArgs args)
        {
            Playnite.HoleInOne.SteamLinkContext.RequireLocalAccount();
            Dispose();

            var steamExe = Steam.ClientExecPath;
            if (steamExe.IsNullOrEmpty() || !File.Exists(steamExe))
            {
                throw new Exception("Can't start game, Steam install not found");
            }

            var installDirectory = Game.InstallDirectory;
            if (gameId.IsMod)
            {
                var allGames = SteamLocalService.GetInstalledGames(false);
                if (allGames.TryGetValue(gameId.AppID.ToString(), out GameMetadata realGame))
                {
                    installDirectory = realGame.InstallDirectory;
                }
            }

            if (!gameId.IsMod && !gameId.IsShortcut
                && (
                    (playniteAPI.ApplicationInfo.Mode == ApplicationMode.Fullscreen && settings.ShowSteamLaunchMenuInFullscreenMode)
                    || (playniteAPI.ApplicationInfo.Mode == ApplicationMode.Desktop && settings.ShowSteamLaunchMenuInDesktopMode)
                ))
            {
                ProcessStarter.StartProcess(steamExe, $"-silent \"steam://launch/{Game.GameId}/Dialog\"");
            }
            else
            {
                ProcessStarter.StartProcess(steamExe, $"-silent \"steam://rungameid/{Game.GameId}\"");
            }

            procMon = new ProcessMonitor();
            procMon.TreeStarted += ProcMon_TreeStarted;
            procMon.TreeDestroyed += Monitor_TreeDestroyed;
            if (Directory.Exists(installDirectory))
            {
                procMon.WatchDirectoryProcesses(installDirectory, false);
            }
            else
            {
                InvokeOnStopped(new GameStoppedEventArgs());
            }
        }

        private void ProcMon_TreeStarted(object sender, ProcessMonitor.TreeStartedEventArgs args)
        {
            stopWatch = Stopwatch.StartNew();
            InvokeOnStarted(new GameStartedEventArgs() { StartedProcessId = args.StartedId });
        }

        private void Monitor_TreeDestroyed(object sender, EventArgs args)
        {
            stopWatch?.Stop();
            InvokeOnStopped(new GameStoppedEventArgs(Convert.ToUInt64(stopWatch?.Elapsed.TotalSeconds ?? 0)));
        }
    }
}
