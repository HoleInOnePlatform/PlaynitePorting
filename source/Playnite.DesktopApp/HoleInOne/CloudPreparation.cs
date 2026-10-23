using Playnite.HoleInOne;
using Playnite.SDK;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.HoleInOne
{
    public static class CloudPreparation
    {
        private sealed class Context
        {
            public string Address;
            public string Token;
            public BackendConnection Backend;
            public PreparedSessionManager Sessions;
        }
        private static Context current;
        private static Task pendingPreparation = Task.CompletedTask;
        private static Task pendingCleanup = Task.CompletedTask;
        private static CancellationTokenSource preparationCancellation = new CancellationTokenSource();

        // Accessed on the WPF dispatcher. Each context owns its credentials and cleanup connection.
        public static PreparedSessionManager Get(LauncherSettings settings)
        {
            var token = LauncherLogin.TokenFor(settings);
            if (current != null && (current.Address != settings.BackendUrl || current.Token != token))
            {
                var previous = current;
                current = null;
                pendingCleanup = Task.WhenAll(pendingCleanup, StopAsync(previous));
            }
            if (current == null)
            {
                var backend = new BackendConnection(settings.BackendUrl, token);
                current = new Context { Address = settings.BackendUrl, Token = token, Backend = backend,
                    Sessions = new PreparedSessionManager(backend, log: line => LogManager.GetLogger().Info(line)) };
            }
            return current.Sessions;
        }
        public static void Begin() { pendingPreparation = Task.WhenAll(pendingPreparation, BeginAsync()); }
        private static async Task BeginAsync()
        {
            try
            {
                var settings = LauncherSettings.Load();
                var generation = LauncherLogin.Generation;
                var cancellation = preparationCancellation.Token;
                // Startup already completed the login dialog. Do not show another dialog here.
                if (string.IsNullOrEmpty(LauncherLogin.TokenFor(settings))) return;
                if (!SteamLinkContext.IsConfigured || !(await SteamLinkContext.RefreshAsync(cancellation)).IsLinked) return;
                cancellation.ThrowIfCancellationRequested();
                if (generation != LauncherLogin.Generation) return;
                new ExportReceipts(settings).Start();
                await Get(settings).PrepareWithRetriesAsync(cancellation);
            }
            catch (Exception error)
            {
                // Preparation must not block library use. Play retries the same preparation ID.
                LogManager.GetLogger().Warn("Cloud preparation deferred (" + BackendConnection.DescribeFailure(error, CancellationToken.None) + ").");
            }
        }
        public static Task ResetAsync()
        {
            var previousCancellation = preparationCancellation;
            previousCancellation.Cancel();
            preparationCancellation = new CancellationTokenSource();
            var previous = current;
            current = null;
            pendingCleanup = Task.WhenAll(pendingCleanup, previous == null ? Task.CompletedTask : StopAsync(previous),
                DisposeAfterPreparationAsync(previousCancellation, pendingPreparation));
            return pendingCleanup;
        }
        private static async Task DisposeAfterPreparationAsync(CancellationTokenSource cancellation, Task preparation)
        {
            try { await preparation.ConfigureAwait(false); }
            finally { cancellation.Dispose(); }
        }
        private static async Task StopAsync(Context context)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await context.Sessions.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            { LogManager.GetLogger().Warn("Prepared session cleanup deferred to server expiry (" + BackendConnection.DescribeFailure(error, CancellationToken.None) + ")."); }
            finally { context.Backend.Dispose(); }
        }
        public static void Shutdown()
        {
            // WPF Exit cannot await. All network waits below avoid its synchronization context.
            try { Task.WhenAll(ResetAsync(), pendingPreparation).Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
        }
    }
}
