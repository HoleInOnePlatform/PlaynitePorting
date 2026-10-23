using Playnite.HoleInOne;
using Playnite.SDK.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.HoleInOne
{
    internal static class LocalInstallationObserver
    {
        public static async Task WaitAsync(Func<Game> readGame, string key, Action install, Action<string, bool> notice, CancellationToken ct)
        {
            var download = new LocalDownloadSession(() => readGame().IsInstalled, () => readGame().IsInstalling,
                () => { if (key.StartsWith("steam-", StringComparison.Ordinal)) SteamLinkContext.RequireLocalAccount(); }, install);
            download.Request();
            string lastNotice = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                download.Refresh();
                if (download.Complete) return;
                if (download.Message != lastNotice)
                    notice(download.Message, download.WaitingForAccount && key.StartsWith("steam-", StringComparison.Ordinal));
                lastNotice = download.Message;
                // Cancelling observation leaves the independently owned store download running.
                await Task.Delay(1000, ct);
            }
        }
    }
}
