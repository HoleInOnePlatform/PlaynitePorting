using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    public sealed class PreparedCloudSession
    {
        public string SessionId { get; set; }
        public string GameId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
    public sealed class PrewarmingUnavailableException : Exception { }
    public sealed class PreparedSessionExpiredException : Exception { }
    public sealed class DeferredLaunchUnavailableException : Exception { }
    public interface IDeferredPreparedSessionBackend
    {
        Task<CloudSession> ConnectPreparedSessionAsync(string sessionId, string gameId, CancellationToken ct);
    }
    public interface IPreparedSessionBackend
    {
        Task<PreparedCloudSession> PrepareSessionAsync(string requestId, CancellationToken ct);
        Task<CloudSession> PlayPreparedSessionAsync(string sessionId, string gameId, CancellationToken ct);
        Task<CloudSession> CreateSessionAsync(CancellationToken ct, string gameId = null);
        Task StopCloudAsync(string sessionId, CancellationToken ct);
    }

    // Shared by startup and Play. Never create a second allocation just because preparation
    // is still running or its HTTP response was lost. Backend deduplicates requestId too.
    public sealed class PreparedSessionManager
    {
        private readonly IPreparedSessionBackend backend;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private readonly Func<DateTimeOffset> now;
        private readonly PerformanceTrace timing;
        private readonly Action<string> log;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        public string TimingId => timing.Id;
        private string requestId = Guid.NewGuid().ToString("N");
        private PreparedCloudSession prepared;
        private bool unsupported;
        private bool closed;
        private bool claimed;
        public PreparedSessionManager(IPreparedSessionBackend backend, Func<DateTimeOffset> now = null, Action<string> log = null)
        { timing = new PerformanceTrace("session_preparation", log); this.log = log; this.backend = backend; this.now = now ?? (() => DateTimeOffset.UtcNow); }

        private async Task PrepareCoreAsync(CancellationToken ct)
        {
            if (closed) throw new OperationCanceledException();
            if (prepared != null || unsupported) return;
            try { prepared = await timing.RunAsync("prepare_api", () => backend.PrepareSessionAsync(requestId, ct)).ConfigureAwait(false); }
            catch (Exception error)
            {
                try { log?.Invoke("[HIO-PREPARE-ERROR] trace=" + timing.Id + " stage=prepare_api " + BackendConnection.DescribeFailure(error, ct)); }
                catch { } // Logging must not change retry or fallback behavior.
                if (error is PrewarmingUnavailableException) unsupported = true;
                else throw;
            }
        }
        public async Task PrepareAsync(CancellationToken ct)
        {
            await timing.RunAsync("preparation_lock_wait", () => gate.WaitAsync(ct)).ConfigureAwait(false);
            try { await PrepareCoreAsync(ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        public async Task PrepareWithRetriesAsync(CancellationToken ct, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token))
            {
                var token = linked.Token;
                // Share the entire startup operation with Play, including the retry delay.
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (claimed) return;
                    for (var attempt = 1; ; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        try { await PrepareCoreAsync(token).ConfigureAwait(false); return; }
                        catch (Exception error) when (attempt <= 5 && !token.IsCancellationRequested && IsRetryable(error))
                        {
                            try { log?.Invoke("[HIO-PREPARE-RETRY] trace=" + timing.Id + " retry=" + attempt + " max_retries=5 delay_ms=2000"); }
                            catch { }
                            await (delay != null ? delay(TimeSpan.FromSeconds(2), token) : Task.Delay(TimeSpan.FromSeconds(2), token)).ConfigureAwait(false);
                        }
                    }
                }
                finally { gate.Release(); }
            }
        }
        private static bool IsRetryable(Exception error)
        {
            if (error is LoginRequiredException) return false;
            if (error is OperationCanceledException) return true; // Caller cancellation is checked separately.
            if (!(error is System.Net.Http.HttpRequestException)) return false;
            if (error.Data["HioHttpStatus"] is int status)
                return status == 408 || status == 429 || status >= 500;
            return true;
        }
        public async Task<CloudSession> PlayAsync(string gameId, CancellationToken ct)
        {
            await timing.RunAsync("preparation_lock_wait", () => gate.WaitAsync(ct)).ConfigureAwait(false);
            try
            {
                if (closed) throw new OperationCanceledException();
                // The currently claimed cloud game remains owned by InstantPlayCoordinator.
                if (claimed) throw new InvalidOperationException("이미 클라우드 게임을 실행하고 있습니다. 현재 게임 창을 닫아 주세요.");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await PrepareCoreAsync(ct).ConfigureAwait(false);
                        if (unsupported)
                        {
                            ct.ThrowIfCancellationRequested();
                            var legacy = await timing.RunAsync("legacy_create_api", () => backend.CreateSessionAsync(ct, gameId)).ConfigureAwait(false);
                            claimed = true;
                            return legacy;
                        }
                        if (prepared.GameId != gameId)
                        {
                            await ReleaseCoreAsync(ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            var legacy = await timing.RunAsync("legacy_create_api", () => backend.CreateSessionAsync(ct, gameId)).ConfigureAwait(false);
                            claimed = true;
                            return legacy;
                        }
                        if (prepared.ExpiresAt <= now()) throw new PreparedSessionExpiredException();
                        ct.ThrowIfCancellationRequested();
                        var session = await timing.RunAsync("play_api", () => backend.PlayPreparedSessionAsync(prepared.SessionId, gameId, ct)).ConfigureAwait(false);
                        claimed = true;
                        return session;
                    }
                    catch (PreparedSessionExpiredException) when (attempt == 0)
                    {
                        await ReleaseCoreAsync(ct).ConfigureAwait(false);
                    }
                }
                throw new PreparedSessionExpiredException();
            }
            finally { gate.Release(); }
        }
        private async Task ReleaseCoreAsync(CancellationToken ct)
        {
            if (prepared != null) await backend.StopCloudAsync(prepared.SessionId, ct).ConfigureAwait(false);
            prepared = null;
            requestId = Guid.NewGuid().ToString("N");
            claimed = false;
        }
        // Called only after the coordinator has finished the old play/stop or local handoff.
        public async Task PlayFinishedAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { prepared = null; claimed = false; requestId = Guid.NewGuid().ToString("N"); }
            finally { gate.Release(); }
        }
        public async Task StopAsync(CancellationToken ct)
        {
            shutdown.Cancel();
            await timing.RunAsync("preparation_lock_wait", () => gate.WaitAsync(ct)).ConfigureAwait(false);
            try
            {
                closed = true;
                await ReleaseCoreAsync(ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }
}
