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

        public SteamInstallController(Game game) : base(game)
        {
            Name = "Install using Steam";
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
                ProcessStarter.StartUrl($"steam://install/{gameId.AppID}");
                StartInstallWatcher();
            }
        }

        public async void StartInstallWatcher()
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
                    if (installed.TryGetValue(id, out var installedGame))
                    {
                        var installInfo = new GameInstallationData
                        {
                            InstallDirectory = installedGame.InstallDirectory
                        };

                        InvokeOnInstalled(new GameInstalledEventArgs(installInfo));
                        return;
                    }

                    await Task.Delay(10_000);
                }
            });
        }
    }

    public class SteamUninstallController : UninstallController
    {
        private CancellationTokenSource watcherToken;

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
                ProcessStarter.StartUrl($"steam://uninstall/{gameId.AppID}");
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

}
