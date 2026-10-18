using Playnite.HoleInOne;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

static class PrewarmChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var cloud = new FakeWarmBackend { Pending = new TaskCompletionSource<bool>() };
        var sessions = new PreparedSessionManager(cloud);
        var startup = sessions.PrepareAsync(default);
        var play = sessions.PlayAsync("steam-2868840", default);
        check(cloud.PrepareIds.Count == 1 && !play.IsCompleted, "early Play shares the in-flight startup preparation");
        cloud.Pending.SetResult(true);
        await startup;
        var result = await play;
        check(result.SessionId == "warm-1" && cloud.Legacy == 0 && cloud.Plays == 1, "Play claims the prepared V5 session without creating a legacy session");
        await Reject(() => sessions.PlayAsync("steam-2868840", default));
        check(cloud.Plays == 1, "concurrent/repeated Play cannot claim the same allocation twice");
        await sessions.PlayFinishedAsync();
        await sessions.PlayAsync("steam-2868840", default);
        check(cloud.PrepareIds.Distinct().Count() == 2, "a later game after close gets a fresh preparation");
        await sessions.StopAsync(default);
        check(cloud.Stops == 1, "shutdown stops the owned session");
        await Reject(() => sessions.PrepareAsync(default));

        cloud = new FakeWarmBackend { FailFirstPrepare = true };
        sessions = new PreparedSessionManager(cloud);
        await Reject(() => sessions.PrepareAsync(default));
        await sessions.PlayAsync("steam-2868840", default);
        check(cloud.PrepareIds.Count == 2 && cloud.PrepareIds.Distinct().Count() == 1, "lost preparation response retries the same idempotency ID");

        cloud = new FakeWarmBackend { ExpireFirstPlay = true };
        sessions = new PreparedSessionManager(cloud);
        await sessions.PrepareAsync(default);
        await sessions.PlayAsync("steam-2868840", default);
        check(cloud.Stops == 1 && cloud.PrepareIds.Distinct().Count() == 2 && cloud.Plays == 2, "expired warm session is stopped and replaced once on Play");

        cloud = new FakeWarmBackend(); sessions = new PreparedSessionManager(cloud);
        await sessions.PrepareAsync(default);
        await sessions.PlayAsync("epic-other", default);
        check(cloud.Plays == 0 && cloud.Stops == 1 && cloud.Legacy == 1, "another game releases the V5 slot and follows its existing game route");

        cloud = new FakeWarmBackend { Disabled = true }; sessions = new PreparedSessionManager(cloud);
        await sessions.PrepareAsync(default); await sessions.PlayAsync("steam-2868840", default);
        check(cloud.Legacy == 1 && cloud.PrepareIds.Count == 1, "older/disabled backend retains existing streaming behavior");

        cloud = new FakeWarmBackend { Pending = new TaskCompletionSource<bool>() }; sessions = new PreparedSessionManager(cloud);
        startup = sessions.PrepareAsync(default); var stop = sessions.StopAsync(default);
        cloud.Pending.SetResult(true); await startup; await stop;
        check(cloud.Stops == 1, "shutdown during preparation cleans up its eventual result");

        var wire = new WarmWire();
        using var backend = new BackendConnection("http://localhost:5010/", "test-auth", wire);
        var prepared = await backend.PrepareSessionAsync("startup-id", default);
        await backend.PlayPreparedSessionAsync(prepared.SessionId, prepared.GameId, default);
        check(wire.Calls.SequenceEqual(new[] { "/v1/holeinone/sessions/prepare", "/v1/holeinone/sessions/warm-1/play" }), "HTTP preparation and claim routes match the backend contract");
        await Reject(() => backend.ConnectPreparedSessionAsync(prepared.SessionId, prepared.GameId, default));
        check(true, "missing deferred support confirmation is rejected before navigating");
        wire.Deferred = true;
        check((await backend.ConnectPreparedSessionAsync(prepared.SessionId, prepared.GameId, default)).LaunchDeferred, "connect-only route requires explicit launch gate confirmation");
        await backend.AllowPreparedLaunchAsync(prepared.SessionId, default);
        check(wire.Calls.Last().EndsWith("/allow-launch"), "baseline confirmation uses authenticated account endpoint");
        wire.Url = "http://attacker.invalid/stream/v5/index.html#warm-1/" + new string('a',64);
        await Reject(() => backend.PlayPreparedSessionAsync("warm-1", "steam-2868840", default));
        check(true, "prepared webview URL must belong to the configured backend");
        wire.Status = HttpStatusCode.Gone;
        try { await backend.PlayPreparedSessionAsync("warm-1", "steam-2868840", default); throw new Exception("expiry accepted"); }
        catch (PreparedSessionExpiredException) { check(true, "HTTP 410 is recoverable expiry"); }
    }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch { return; } throw new Exception("Expected rejection"); }

    private sealed class FakeWarmBackend : IPreparedSessionBackend
    {
        public List<string> PrepareIds = new();
        public TaskCompletionSource<bool> Pending;
        public bool FailFirstPrepare, ExpireFirstPlay, Disabled;
        public int Plays, Stops, Legacy;
        public async Task<PreparedCloudSession> PrepareSessionAsync(string requestId, CancellationToken ct)
        {
            PrepareIds.Add(requestId);
            if (Pending != null) await Pending.Task;
            if (Disabled) throw new PrewarmingUnavailableException();
            if (FailFirstPrepare && PrepareIds.Count == 1) throw new HttpRequestException("lost response");
            return new() { SessionId = "warm-" + PrepareIds.Distinct().Count(), GameId = "steam-2868840", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(9) };
        }
        public Task<CloudSession> PlayPreparedSessionAsync(string id, string game, CancellationToken ct)
        {
            Plays++;
            if (ExpireFirstPlay && Plays == 1) throw new PreparedSessionExpiredException();
            return Task.FromResult(new CloudSession { SessionId = id, GameId = game });
        }
        public Task<CloudSession> CreateSessionAsync(CancellationToken ct, string gameId = null)
        { Legacy++; return Task.FromResult(new CloudSession { SessionId = "legacy", GameId = gameId }); }
        public Task StopCloudAsync(string id, CancellationToken ct) { Stops++; return Task.CompletedTask; }
    }
    private sealed class WarmWire : HttpMessageHandler
    {
        public List<string> Calls = new();
        public string Url = "http://localhost:5010/stream/v5/index.html#warm-1/" + new string('a',64);
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Deferred;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri.AbsolutePath);
            object body = request.RequestUri.AbsolutePath.EndsWith("/prepare")
                ? new { sessionId = "warm-1", gameId = "steam-2868840", expiresAt = DateTimeOffset.UtcNow.AddMinutes(9) }
                : new { sessionId = "warm-1", gameId = "steam-2868840", streamUrl = Url, launchDeferred = Deferred };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") });
        }
    }
}
