using Playnite.HoleInOne;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

static class PrewarmChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var retryWire = new WarmWire { Status = HttpStatusCode.ServiceUnavailable };
        using (var retryBackend = new BackendConnection("http://localhost:5010/", handler: retryWire))
        {
            var retries = new List<TimeSpan>();
            var retryManager = new PreparedSessionManager(retryBackend);
            await Reject(() => retryManager.PrepareWithRetriesAsync(default, (wait, ct) => { retries.Add(wait); return Task.CompletedTask; }));
            check(retryWire.Calls.Count == 6 && retries.Count == 5 && retries.All(wait => wait == TimeSpan.FromSeconds(2)), "startup stops after initial request plus five retries at two second intervals");
            check(retryWire.PrepareIds.Distinct().Count() == 1, "all warmup retries reuse the same request ID");
        }
        retryWire = new WarmWire { Status = HttpStatusCode.ServiceUnavailable };
        using (var retryBackend = new BackendConnection("http://localhost:5010/", handler: retryWire))
        {
            var retryManager = new PreparedSessionManager(retryBackend);
            var waiting = new TaskCompletionSource<bool>();
            var resume = new TaskCompletionSource<bool>();
            var warmup = retryManager.PrepareWithRetriesAsync(default, async (wait, ct) => { waiting.SetResult(true); await resume.Task; });
            await waiting.Task;
            var concurrentPlay = retryManager.PlayAsync("steam-2868840", default);
            check(!concurrentPlay.IsCompleted && retryWire.PrepareIds.Count == 1, "Play shares warmup retry rather than allocating another session");
            retryWire.Status = HttpStatusCode.OK;
            resume.SetResult(true);
            await warmup;
            await concurrentPlay;
            check(retryWire.PrepareIds.Count == 2, "successful retry stops further retries and allows Play");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.Conflict })
        {
            retryWire = new WarmWire { Status = status };
            using var retryBackend = new BackendConnection("http://localhost:5010/", handler: retryWire);
            try { await new PreparedSessionManager(retryBackend).PrepareWithRetriesAsync(default); } catch { }
            check(retryWire.Calls.Count == 1, "authentication, invalid requests and disabled prewarming are not retried");
        }
        retryWire = new WarmWire { Status = HttpStatusCode.ServiceUnavailable };
        using (var retryBackend = new BackendConnection("http://localhost:5010/", handler: retryWire))
        {
            var retryManager = new PreparedSessionManager(retryBackend);
            var waiting = new TaskCompletionSource<bool>();
            var warmup = retryManager.PrepareWithRetriesAsync(default, (wait, ct) => { waiting.SetResult(true); return Task.Delay(Timeout.Infinite, ct); });
            await waiting.Task;
            await retryManager.StopAsync(default);
            await Reject(() => warmup);
            check(retryWire.Calls.Count == 1, "shutdown cancels retry delay immediately without another request");
        }
        var diagnostics = new List<string>();
        var failureWire = new WarmWire { Status = HttpStatusCode.ServiceUnavailable };
        using (var failureBackend = new BackendConnection("http://localhost:5010/", "secret-token", failureWire))
        {
            var preparation = new PreparedSessionManager(failureBackend, log: diagnostics.Add);
            await Reject(() => preparation.PrepareAsync(default));
            check(diagnostics.Any(line => line.Contains("[HIO-PREPARE-ERROR]") && line.Contains("http_status=503")), "preparation failures record HTTP status with trace");
            failureWire.Failure = new HttpRequestException("secret-token https://private.example/", new System.Net.WebException("private", new System.Net.Sockets.SocketException(10061), WebExceptionStatus.ConnectFailure, null));
            await Reject(() => preparation.PrepareAsync(default));
            var failure = diagnostics.Last(line => line.Contains("[HIO-PREPARE-ERROR]"));
            check(failure.Contains("http_status=none") && failure.Contains("socket_error=ConnectionRefused") && failure.Contains("web_error=ConnectFailure"), "transport failure records nested connection cause without stale HTTP status");
            check(!string.Join("", diagnostics).Contains("secret-token") && !failure.Contains("private"), "failure diagnostics omit credentials, URLs and raw exception messages");
            failureWire.Failure = null;
            failureWire.Status = HttpStatusCode.OK;
            await preparation.PrepareAsync(default);
            check(true, "diagnostic logging preserves preparation retry success");
        }
        check(BackendConnection.DescribeFailure(new TaskCanceledException(), default).Contains("reason=timeout"), "request timeout is distinguished from caller cancellation");
        check(BackendConnection.DescribeFailure(new OperationCanceledException(), new CancellationToken(true)).Contains("reason=cancelled"), "caller cancellation is labeled separately");
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
        public List<string> PrepareIds = new();
        public Exception Failure;
        public List<string> Calls = new();
        public string Url = "http://localhost:5010/stream/v5/index.html#warm-1/" + new string('a',64);
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Deferred;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Failure != null) throw Failure;
            Calls.Add(request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/prepare"))
                PrepareIds.Add(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            object body = request.RequestUri.AbsolutePath.EndsWith("/prepare")
                ? new { sessionId = "warm-1", gameId = "steam-2868840", expiresAt = DateTimeOffset.UtcNow.AddMinutes(9) }
                : new { sessionId = "warm-1", gameId = "steam-2868840", streamUrl = Url, launchDeferred = Deferred };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") });
        }
    }
}
