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

namespace SteamLibrary
{
    public class SteamInstallController : InstallController
    {
        private CancellationTokenSource watcherToken;
        private readonly IPlayniteAPI playniteApi;
        private string requestedManifest;
        private DateTime initialManifestWriteTime;

        public SteamInstallController(Game game, IPlayniteAPI playniteApi) : base(game)
        {
            Name = "Install using Steam";
            this.playniteApi = playniteApi;
        }

        public override void Dispose()
        {
            watcherToken?.Cancel();
        }

        public override void Install(InstallActionArgs args)
        {
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
                var location = SteamInstallService.SelectLocation(playniteApi, Game.Name);
                if (location == null)
                {
                    InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                    return;
                }
                requestedManifest = Path.Combine(location.Path, "steamapps", $"appmanifest_{gameId.AppID}.acf");
                initialManifestWriteTime = File.GetLastWriteTimeUtc(requestedManifest);
                ProcessStarter.StartProcess(Steam.ClientExecPath, SteamInstallService.BuildArguments(gameId.AppID, location.Index));
                StartInstallWatcher();
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
                    if (requestedManifest != null && File.Exists(requestedManifest) &&
                        File.GetLastWriteTimeUtc(requestedManifest) != initialManifestWriteTime)
                        requestAccepted = true;
                    if (!requestAccepted && requestTimer.Elapsed > TimeSpan.FromMinutes(2))
                        throw new TimeoutException("Steam이 다운로드 요청을 처리하지 못함. Steam 로그인 상태·게임 소유권·저장 공간을 확인한 뒤 다시 시도해 주세요.");
                    Playnite.SDK.Models.GameMetadata installedGame;
                    try
                    {
                        installedGame = await Task.Run(() => SteamLocalService.GetInstalledGame(id.AppID), token);
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
        private CancellationTokenSource watcherToken;

        public override Task UninstallAsync(UninstallActionArgs args)
        {
            // Steam can block while forwarding the command to its running client.
            // Keep process startup off the UI thread; the caller handles launch errors.
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
                using (ProcessStarter.StartProcess(Steam.ClientExecPath, $"-silent +app_uninstall {gameId.AppID}")) { }
                LogManager.GetLogger().Info($"Steam uninstall request dispatched in {requestTimer.ElapsedMilliseconds} ms.");
                StartUninstallWatcher();
            }
        }

        public async void StartUninstallWatcher()
        {
            watcherToken = new CancellationTokenSource();
            var id = Game.ToSteamGameID();

            await Task.Run(async () =>
            {
                while (true)
                {
                    if (watcherToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var installed = SteamLocalService.GetInstalledGames(false);
                    if (!installed.ContainsKey(id))
                    {
                        InvokeOnUninstalled(new GameUninstalledEventArgs());
                        return;
                    }

                    await Task.Delay(5_000);
                }
            });
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
