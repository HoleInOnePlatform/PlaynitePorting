using EpicLibrary.Services;
using Newtonsoft.Json.Linq;
using Playnite.HoleInOne;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EpicLibrary
{
    // Only automated downloads and installations owned by Legendary use this adapter.
    // Account login, entitlement import and Epic Launcher installations remain upstream.
    internal static class HoleInOneDownload
    {
        private static readonly SemaphoreSlim authentication = new SemaphoreSlim(1, 1);

        internal static Dictionary<string, GameMetadata> Installed()
        {
            var games = new Dictionary<string, GameMetadata>();
            if (!File.Exists(LegendaryTool.Executable)) return games;
            foreach (var item in JArray.Parse(LegendaryTool.RunAsync("list-installed", "--json").GetAwaiter().GetResult()))
            {
                var id = (string)item["app_name"];
                var directory = (string)item["install_path"];
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)
                    || (bool?)item["needs_verification"] == true || (bool?)item["is_dlc"] == true
                    || !File.Exists(StoreFiles.SafeChild(directory, (string)item["executable"]))) continue;
                games[id] = new GameMetadata { GameId = id, Name = (string)item["title"], InstallDirectory = directory,
                    IsInstalled = true, Source = new MetadataNameProperty("Epic"),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") } };
            }
            return games;
        }

        internal static async Task Authenticate(EpicLibrary plugin)
        {
            await authentication.WaitAsync().ConfigureAwait(false);
            try
            {
                await LegendaryTool.RunAsync("auth", "--delete").ConfigureAwait(false);
                var code = await Task.Run(() => new EpicAccountClient(plugin.PlayniteApi, plugin.TokensPath).GetDownloadExchangeCode()).ConfigureAwait(false);
                await LegendaryTool.RunAsync("auth", "--token", code).ConfigureAwait(false);
                // Legendary can exit successfully when authentication fails.
                await LegendaryTool.RunAsync("list", "--json").ConfigureAwait(false);
            }
            finally { authentication.Release(); }
        }
    }

    internal sealed class HoleInOneInstallController : InstallController
    {
        private readonly EpicLibrary plugin;
        public HoleInOneInstallController(Game game, EpicLibrary plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "HoleInOne 자동 다운로드";
        }
        public override async void Install(InstallActionArgs args)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Game.GameId) || Game.GameId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                    throw new InvalidOperationException("게임 ID 형식이 올바르지 않음.");
                await HoleInOneDownload.Authenticate(plugin);
                await LegendaryTool.RunAsync("-y", "install", Game.GameId, "--base-path", LauncherSettings.Load().DownloadDirectory,
                    "--game-folder", Game.GameId, "--skip-dlcs");
                var installed = await Task.Run(() => HoleInOneDownload.Installed());
                if (!installed.TryGetValue(Game.GameId, out var game)) throw new InvalidOperationException("Epic 설치 파일 확인에 실패함.");
                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = game.InstallDirectory }));
            }
            catch (Exception)
            {
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                plugin.PlayniteApi.Dialogs.ShowErrorMessage("Epic 다운로드를 완료하지 못함. Epic 연동 설정의 인증과 저장 공간을 확인해야 함.", "HoleInOne");
            }
        }
        // Closing the cloud window must not cancel the download.
        public override void Dispose() { }
    }

    internal sealed class HoleInOneUninstallController : UninstallController
    {
        private readonly EpicLibrary plugin;
        public HoleInOneUninstallController(Game game, EpicLibrary plugin) : base(game) { this.plugin = plugin; Name = "Uninstall"; }
        public override async void Uninstall(UninstallActionArgs args)
        {
            try
            {
                await LegendaryTool.RunAsync("-y", "uninstall", Game.GameId);
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            catch (Exception)
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage("Epic 게임 제거를 완료하지 못함.", "HoleInOne");
            }
        }
        public override void Dispose() { }
    }
}
