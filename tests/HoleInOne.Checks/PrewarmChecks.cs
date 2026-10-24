using Playnite.HoleInOne;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

static class PrewarmChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Task NoDelay(TimeSpan wait, CancellationToken ct) => Task.CompletedTask;
        var cloud = new FakeWarmBackend();
        var sessions = new PreparedSessionManager(cloud, delay: NoDelay);
        check(cloud.PrepareIds.Count == 0, "manager construction does not prewarm");
        await sessions.PlayAsync("steam-2868840", default);
        check(cloud.Games.Single() == "steam-2868840" && cloud.Plays == 1, "explicit Play prepares selected game then claims it");
        await Reject(() => sessions.PlayAsync("steam-2", default));
        check(cloud.PrepareIds.Count == 1 && cloud.Cancels.Count == 0, "concurrent Play cannot replace or cancel active game");
        await sessions.PlayFinishedAsync();
        await sessions.PlayAsync("steam-2", default);
        check(cloud.Games.Last() == "steam-2" && cloud.PrepareIds.Distinct().Count() == 2, "next game has its own game ID and request ID");
        await sessions.StopAsync(default);
        check(cloud.Cancels.Single() == cloud.PrepareIds.Last(), "shutdown cancels the owned request");
        await Reject(() => sessions.PlayAsync("steam-2", default));

        cloud = new FakeWarmBackend { FailFirstPrepare = true };
        sessions = new PreparedSessionManager(cloud, delay: NoDelay);
        await sessions.PlayAsync("steam-2", default);
        check(cloud.PrepareIds.Count == 2 && cloud.PrepareIds.Distinct().Count() == 1 && cloud.Games.All(g => g == "steam-2"), "lost response retries exactly the same game and request");

        cloud = new FakeWarmBackend { ExpireFirstPlay = true };
        sessions = new PreparedSessionManager(cloud, delay: NoDelay);
        await sessions.PlayAsync("steam-2", default);
        check(cloud.Cancels.Count == 1 && cloud.PrepareIds.Distinct().Count() == 2 && cloud.Plays == 2, "expired request is cancelled and replaced once");

        cloud = new FakeWarmBackend { Pending = new TaskCompletionSource<bool>() };
        sessions = new PreparedSessionManager(cloud, delay: NoDelay);
        using (var cancel = new CancellationTokenSource())
        {
            var pending = sessions.PlayAsync("steam-2", cancel.Token);
            cancel.Cancel();
            await Reject(() => pending);
            check(cloud.Cancels.Single() == cloud.PrepareIds.Single() && cloud.Plays == 0,
                "cancellation before prepare response stops by request ID using an independent token");
        }
        cloud.Pending = null;
        await sessions.PlayAsync("steam-2868840", default);
        check(cloud.PrepareIds.Distinct().Count() == 2, "cancelled play can be followed by another selected game");

        cloud = new FakeWarmBackend { Pending = new TaskCompletionSource<bool>(), FailCancel = true };
        sessions = new PreparedSessionManager(cloud, delay: NoDelay);
        using (var cancel = new CancellationTokenSource())
        {
            var pending = sessions.PlayAsync("steam-2", cancel.Token); cancel.Cancel(); await Reject(() => pending);
        }
        await Reject(() => sessions.PlayAsync("steam-2868840", default));
        check(cloud.PrepareIds.Count == 1, "failed cleanup blocks allocating another game");
        cloud.FailCancel = false; cloud.Pending = null;
        await sessions.PlayAsync("steam-2868840", default);
        check(cloud.Cancels.Distinct().Count() == 1 && cloud.Games.Last() == "steam-2868840", "cleanup retries retain identity before switching game");

        cloud = new FakeWarmBackend { Pending = new TaskCompletionSource<bool>() };
        sessions = new PreparedSessionManager(cloud, delay: NoDelay);
        var inflight = sessions.PlayAsync("steam-2", default);
        await sessions.StopAsync(default); await Reject(() => inflight);
        check(cloud.Cancels.Count == 1, "shutdown cancels in-flight Play and cleans exactly once");

        var retryWire = new WarmWire { Status = HttpStatusCode.ServiceUnavailable };
        using (var retryBackend = new BackendConnection("http://localhost:5010/", handler: retryWire))
        {
            var waits = new List<TimeSpan>();
            var manager = new PreparedSessionManager(retryBackend, delay: (wait, ct) => { waits.Add(wait); return Task.CompletedTask; });
            await Reject(() => manager.PlayAsync("steam-2868840", default));
            check(retryWire.PrepareIds.Count == 6 && waits.Count == 5 && waits.All(w => w == TimeSpan.FromSeconds(2)), "explicit Play has bounded transient retries");
            check(retryWire.PrepareIds.Distinct().Count() == 1 && retryWire.Calls.Last().EndsWith("/cancel"), "retry exhaustion cancels unknown allocation by original request");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.Conflict })
        {
            retryWire = new WarmWire { Status = status };
            using var retryBackend = new BackendConnection("http://localhost:5010/", handler: retryWire);
            await Reject(() => new PreparedSessionManager(retryBackend, delay: NoDelay).PlayAsync("steam-2868840", default));
            check(retryWire.PrepareIds.Count == 1, "permanent failures do not retry allocation");
        }
        var diagnostics = new List<string>();
        using (var failureBackend = new BackendConnection("http://localhost:5010/", "secret-token", new WarmWire { Status = HttpStatusCode.ServiceUnavailable }))
        {
            await Reject(() => new PreparedSessionManager(failureBackend, log: diagnostics.Add, delay: NoDelay).PlayAsync("steam-2868840", default));
            check(diagnostics.Any(l => l.Contains("[HIO-PREPARE-ERROR]") && l.Contains("http_status=503")) && !string.Join("", diagnostics).Contains("secret-token"), "allocation diagnostics retain HTTP status without credentials");
        }
        using (var unsupported = new BackendConnection("http://localhost:5010/", handler: new WarmWire { Status = HttpStatusCode.NotFound, ErrorCode = "game_not_configured" }))
        {
            try { await unsupported.PrepareSessionAsync("unsupported", "steam-other", default); throw new Exception("unknown game accepted"); }
            catch (CloudPlayUnavailableException) { check(true, "unknown game has a game availability error instead of missing API error"); }
        }
        var wire = new WarmWire();
        using var backend = new BackendConnection("http://localhost:5010/", "test-auth", wire);
        var prepared = await backend.PrepareSessionAsync("play-id", "steam-2868840", default);
        await backend.PlayPreparedSessionAsync(prepared.SessionId, prepared.GameId, default);
        check(wire.Calls.SequenceEqual(new[] { "/v1/holeinone/sessions/prepare", "/v1/holeinone/sessions/warm-1/play" }), "HTTP preparation and claim routes match the backend contract");
        wire.Url = "http://attacker.invalid/stream/v5/index.html#warm-1/" + new string('a',64);
        await Reject(() => backend.PlayPreparedSessionAsync("warm-1", "steam-2868840", default));
        check(true, "prepared webview URL must belong to the configured backend");
        wire.Url = "https://public-tunnel.example/stream/v5/index.html#warm-1/" + new string('a',64);
        var local = await backend.PlayPreparedSessionAsync("warm-1", "steam-2868840", default);
        check(local.StreamUrl == "http://localhost:5010/stream/v5/index.html#warm-1/" + new string('a',64),
            "loopback backend public tunnel URL is rebased without navigating the external origin");
        using (var remote = new BackendConnection("https://configured.example/", "test-auth", new WarmWire { Url = wire.Url }))
            await Reject(() => remote.PlayPreparedSessionAsync("warm-1", "steam-2868840", default));
        check(true, "remote backend still rejects a different advertised origin");
        foreach (var invalid in new[] {
            "https://public-tunnel.example/other#warm-1/" + new string('a',64),
            "https://public-tunnel.example/stream/v5/index.html?redirect=1#warm-1/" + new string('a',64),
            "https://public-tunnel.example/stream/v5/index.html#wrong/" + new string('a',64),
            "https://user@public-tunnel.example/stream/v5/index.html#warm-1/" + new string('a',64) })
        {
            wire.Url = invalid;
            await Reject(() => backend.PlayPreparedSessionAsync("warm-1", "steam-2868840", default));
            check(true, "loopback rebasing rejects unexpected paths, query, session and user info");
        }
        wire.Status = HttpStatusCode.Gone;
        try { await backend.PlayPreparedSessionAsync("warm-1", "steam-2868840", default); throw new Exception("expiry accepted"); }
        catch (PreparedSessionExpiredException) { check(true, "HTTP 410 is recoverable expiry"); }
    }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch { return; } throw new Exception("Expected rejection"); }

    private sealed class FakeWarmBackend : IPreparedSessionBackend
    {
        public List<string> PrepareIds = new(), Games = new(), Cancels = new();
        public TaskCompletionSource<bool> Pending;
        public bool FailFirstPrepare, ExpireFirstPlay, FailCancel;
        public int Plays;
        public async Task<PreparedCloudSession> PrepareSessionAsync(string requestId, string gameId, CancellationToken ct)
        {
            PrepareIds.Add(requestId); Games.Add(gameId);
            if (Pending != null) await Pending.Task.WaitAsync(ct);
            if (FailFirstPrepare && PrepareIds.Count == 1) throw new HttpRequestException("lost response");
            return new() { SessionId = "warm-" + PrepareIds.Distinct().Count(), GameId = gameId, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(9) };
        }
        public Task CancelPreparationAsync(string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Cancels.Add(id);
            if (FailCancel) throw new HttpRequestException("lost cleanup");
            return Task.CompletedTask;
        }
        public Task<CloudSession> PlayPreparedSessionAsync(string id, string game, CancellationToken ct)
        {
            Plays++;
            if (ExpireFirstPlay && Plays == 1) throw new PreparedSessionExpiredException();
            return Task.FromResult(new CloudSession { SessionId = id, GameId = game });
        }
        public Task StopCloudAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class WarmWire : HttpMessageHandler
    {
        public List<string> PrepareIds = new();
        public List<string> Calls = new();
        public string Url = "http://localhost:5010/stream/v5/index.html#warm-1/" + new string('a',64);
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string ErrorCode;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/prepare"))
                PrepareIds.Add(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            object body = request.RequestUri.AbsolutePath.EndsWith("/prepare")
                ? new { sessionId = "warm-1", gameId = "steam-2868840", expiresAt = DateTimeOffset.UtcNow.AddMinutes(9) }
                : new { sessionId = "warm-1", gameId = "steam-2868840", streamUrl = Url };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(
                JsonConvert.SerializeObject(ErrorCode == null ? body : new { errorCode = ErrorCode, error = "private-detail" }), Encoding.UTF8, "application/json") });
        }
    }
}
