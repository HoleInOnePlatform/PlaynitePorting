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

        // Accessed on the WPF dispatcher. Each context owns its credentials and cleanup connection.
        public static PreparedSessionManager Get(LauncherSettings settings)
        {
            var token = LauncherLogin.TokenFor(settings);
            if (current != null && (current.Address != settings.BackendUrl || current.Token != token))
            {
                var previous = current;
                current = null;
                _ = StopAsync(previous);
            }
            if (current == null)
            {
                var backend = new BackendConnection(settings.BackendUrl, token);
                current = new Context { Address = settings.BackendUrl, Token = token, Backend = backend,
                    Sessions = new PreparedSessionManager(backend) };
            }
            return current.Sessions;
        }
        public static async void Begin()
        {
            try
            {
                var settings = LauncherSettings.Load();
                // Startup already completed the login dialog. Do not show another dialog here.
                if (settings.RequireLogin && string.IsNullOrEmpty(LauncherLogin.TokenFor(settings))) return;
                await Get(settings).PrepareAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                // Preparation must not block library use. Play retries the same preparation ID.
                LogManager.GetLogger().Warn("Cloud preparation deferred (" + error.GetType().Name + ").");
            }
        }
        public static Task ResetAsync()
        {
            var previous = current;
            current = null;
            return previous == null ? Task.CompletedTask : StopAsync(previous);
        }
        private static async Task StopAsync(Context context)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await context.Sessions.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            { LogManager.GetLogger().Warn("Prepared session cleanup deferred to server expiry (" + error.GetType().Name + ")."); }
            finally { context.Backend.Dispose(); }
        }
        public static void Shutdown()
        {
            // WPF Exit cannot await. All network waits below avoid its synchronization context.
            try { ResetAsync().Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
        }
    }
}
