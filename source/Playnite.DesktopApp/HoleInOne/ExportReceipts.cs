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
        public Task Completion { get; private set; } = Task.CompletedTask;
        public ExportReceipts(LauncherSettings settings)
        {
            address = settings.BackendUrl;
            token = LauncherLogin.TokenFor(settings);
            if ((string.IsNullOrEmpty(token) || string.IsNullOrEmpty(LauncherLogin.AccountId)))
                throw new InvalidOperationException("Receipt cleanup requires the original account.");
            outbox = new ExportReceiptOutbox(Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "PendingReceipts"),
                address, "account-id:" + LauncherLogin.AccountId);
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
            Completion = Task.WhenAll(Completion, Task.Run(async () =>
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
            }));
        }
    }
}
