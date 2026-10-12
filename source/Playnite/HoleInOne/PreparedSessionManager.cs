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
        private string requestId = Guid.NewGuid().ToString("N");
        private PreparedCloudSession prepared;
        private bool unsupported;
        private bool closed;
        private bool claimed;
        public PreparedSessionManager(IPreparedSessionBackend backend, Func<DateTimeOffset> now = null)
        { this.backend = backend; this.now = now ?? (() => DateTimeOffset.UtcNow); }

        private async Task PrepareCoreAsync(CancellationToken ct)
        {
            if (closed) throw new OperationCanceledException();
            if (prepared != null || unsupported) return;
            try { prepared = await backend.PrepareSessionAsync(requestId, ct).ConfigureAwait(false); }
            catch (PrewarmingUnavailableException) { unsupported = true; }
        }
        public async Task PrepareAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await PrepareCoreAsync(ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        public async Task<CloudSession> PlayAsync(string gameId, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
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
                            var legacy = await backend.CreateSessionAsync(ct, gameId).ConfigureAwait(false);
                            claimed = true;
                            return legacy;
                        }
                        if (prepared.GameId != gameId)
                        {
                            await ReleaseCoreAsync(ct).ConfigureAwait(false);
                            var legacy = await backend.CreateSessionAsync(ct, gameId).ConfigureAwait(false);
                            claimed = true;
                            return legacy;
                        }
                        if (prepared.ExpiresAt <= now()) throw new PreparedSessionExpiredException();
                        var session = await backend.PlayPreparedSessionAsync(prepared.SessionId, gameId, ct).ConfigureAwait(false);
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
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                closed = true;
                await ReleaseCoreAsync(ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }
}
