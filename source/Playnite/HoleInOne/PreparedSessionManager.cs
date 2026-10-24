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
    public sealed class SessionPreparationUnavailableException : Exception
    { public SessionPreparationUnavailableException() : base("게임 준비 API를 확인할 수 없습니다. 앱과 Backend 버전을 확인해 주세요.") { } }
    public sealed class PreparedSessionExpiredException : Exception { }
    public sealed class CloudSessionCleanupPendingException : InvalidOperationException
    {
        public CloudSessionCleanupPendingException(Exception cause)
            : base("이전 게임 정리 중입니다. 잠시 후 다시 시도해 주세요.", cause) { }
    }
    public interface IPreparedSessionBackend
    {
        Task<PreparedCloudSession> PrepareSessionAsync(string requestId, string gameId, CancellationToken ct);
        Task CancelPreparationAsync(string requestId, CancellationToken ct);
        Task<CloudSession> PlayPreparedSessionAsync(string sessionId, string gameId, CancellationToken ct);
        Task StopCloudAsync(string sessionId, CancellationToken ct);
    }

    // Only explicit Play allocates capacity. Retries retain both the game and idempotency key.
    public sealed class PreparedSessionManager
    {
        public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
        // A cancelled Play can spend CleanupTimeout cleaning up before Stop acquires the gate,
        // then Stop may retry that cleanup once. Leave margin for both operations.
        public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(25);
        public static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(30);
        private readonly IPreparedSessionBackend backend;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private readonly Func<DateTimeOffset> now;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly PerformanceTrace timing;
        private readonly Action<string> log;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        public string TimingId => timing.Id;
        private string requestId;
        private PreparedCloudSession prepared;
        private bool closed;
        private bool claimed;
        private bool allocationPossible;
        public PreparedSessionManager(IPreparedSessionBackend backend, Func<DateTimeOffset> now = null, Action<string> log = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            timing = new PerformanceTrace("session_preparation", log);
            this.log = log; this.backend = backend; this.now = now ?? (() => DateTimeOffset.UtcNow);
            this.delay = delay ?? Task.Delay;
        }
        private void Log(string message) { try { log?.Invoke(message); } catch { } }
        private async Task PrepareCoreAsync(string gameId, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    prepared = await timing.RunAsync("prepare_api", () => backend.PrepareSessionAsync(requestId, gameId, ct)).ConfigureAwait(false);
                    allocationPossible = true;
                    return;
                }
                catch (Exception error)
                {
                    // A later rejection cannot undo an earlier unknown allocation outcome.
                    if (!RejectedBeforeAllocation(error)) allocationPossible = true;
                    Log("[HIO-PREPARE-ERROR] trace=" + timing.Id + " stage=prepare_api " + BackendConnection.DescribeFailure(error, ct));
                    if (attempt >= 5 || ct.IsCancellationRequested || !IsRetryable(error)) throw;
                    Log("[HIO-PREPARE-RETRY] trace=" + timing.Id + " retry=" + (attempt + 1) + " max_retries=5 delay_ms=2000");
                    await delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }
        }
        private static bool RejectedBeforeAllocation(Exception error)
        {
            // Only classify prepare responses. A claim failure can follow a successful allocation.
            // Other errors (including malformed success bodies and Steam lease conflicts) remain ambiguous.
            return error.Data["HioHttpStatus"] is int status &&
                (status == 400 || status == 401 || status == 403 || status == 404);
        }
        private static bool IsRetryable(Exception error)
        {
            if (error is LoginRequiredException) return false;
            if (error is OperationCanceledException) return true;
            if (!(error is System.Net.Http.HttpRequestException)) return false;
            if (error.Data["HioHttpStatus"] is int status) return status == 408 || status == 429 || status >= 500;
            return true;
        }
        public async Task<CloudSession> PlayAsync(string gameId, CancellationToken ct)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token))
            {
                var token = linked.Token;
                await timing.RunAsync("preparation_lock_wait", () => gate.WaitAsync(token)).ConfigureAwait(false);
                try
                {
                    if (closed) throw new OperationCanceledException();
                    // A rejected concurrent Play must never cancel the already claimed game.
                    if (claimed) throw new InvalidOperationException("이미 클라우드 게임을 실행하고 있습니다. 현재 게임 창을 닫아 주세요.");
                    // A previous failed cleanup must be acknowledged before allocating another game.
                    if (requestId != null)
                    {
                        try { await ReleaseCoreAsync(token).ConfigureAwait(false); }
                        catch (Exception error) when (!(error is LoginRequiredException) && !token.IsCancellationRequested)
                        { throw new CloudSessionCleanupPendingException(error); }
                    }
                    try
                    {
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            requestId = Guid.NewGuid().ToString("N");
                            try
                            {
                                await PrepareCoreAsync(gameId, token).ConfigureAwait(false);
                                if (prepared.GameId != gameId) throw new CloudPlayUnavailableException("요청한 게임과 준비된 게임이 다릅니다.");
                                if (prepared.ExpiresAt <= now()) throw new PreparedSessionExpiredException();
                                token.ThrowIfCancellationRequested();
                                var session = await timing.RunAsync("play_api", () => backend.PlayPreparedSessionAsync(prepared.SessionId, gameId, token)).ConfigureAwait(false);
                                token.ThrowIfCancellationRequested();
                                claimed = true;
                                return session;
                            }
                            catch (PreparedSessionExpiredException) when (attempt == 0)
                            { await ReleaseCoreAsync(token).ConfigureAwait(false); }
                        }
                        throw new PreparedSessionExpiredException();
                    }
                    catch
                    {
                        // The HTTP response may be lost: cancel by owner-scoped request ID even
                        // when we never learned the allocated session ID. Do not reuse caller cancellation.
                        using (var cleanup = new CancellationTokenSource(CleanupTimeout))
                        {
                            try { await ReleaseCoreAsync(cleanup.Token).ConfigureAwait(false); }
                            catch (Exception error) { Log("Play cancellation cleanup deferred (" + BackendConnection.DescribeFailure(error, cleanup.Token) + ")."); }
                        }
                        throw;
                    }
                }
                finally { gate.Release(); }
            }
        }
        private async Task ReleaseCoreAsync(CancellationToken ct)
        {
            if (requestId != null && allocationPossible) await backend.CancelPreparationAsync(requestId, ct).ConfigureAwait(false);
            Clear();
        }
        private void Clear() { prepared = null; requestId = null; claimed = false; allocationPossible = false; }
        // The coordinator owns stop/export ordering after a successful Play.
        public async Task PlayFinishedAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { Clear(); }
            finally { gate.Release(); }
        }
        public async Task StopAsync(CancellationToken ct)
        {
            shutdown.Cancel();
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { closed = true; await ReleaseCoreAsync(ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }
}
