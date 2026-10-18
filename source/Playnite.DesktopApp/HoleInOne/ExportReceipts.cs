using Playnite.HoleInOne;
using Playnite.SDK;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.HoleInOne
{
    internal sealed class ExportReceipts
    {
        private readonly ExportReceiptOutbox outbox;
        private readonly string address;
        private readonly string token;
        public ExportReceipts(LauncherSettings settings)
        {
            address = settings.BackendUrl;
            token = LauncherLogin.TokenFor(settings);
            if (settings.RequireLogin && (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(LauncherLogin.Username)))
                throw new InvalidOperationException("Receipt cleanup requires the original account.");
            outbox = new ExportReceiptOutbox(Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "PendingReceipts"),
                address, settings.RequireLogin ? "account:" + LauncherLogin.Username : "development");
        }
        public void Enqueue(string sessionId, string requestId, string hash)
        {
            try { outbox.Enqueue(sessionId, requestId, hash); }
            catch (Exception error)
            {
                // Cleanup failure must never turn an already applied save into a failed handoff.
                LogManager.GetLogger().Warn("Could not persist export receipt (" + error.GetType().Name + ").");
                return;
            }
            Start();
        }
        public void Start()
        {
            // Own the connection: the coordinator disposes its connection before local launch.
            _ = Task.Run(async () =>
            {
                try
                {
                    using (var backend = new BackendConnection(address, token))
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                        await outbox.DrainAsync((receipt, ct) => backend.ReceiveExportAsync(
                            receipt.SessionId, receipt.RequestId, receipt.Sha256, ct), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                { LogManager.GetLogger().Warn("Export receipt cleanup deferred (" + error.GetType().Name + ")."); }
            });
        }
    }
}
