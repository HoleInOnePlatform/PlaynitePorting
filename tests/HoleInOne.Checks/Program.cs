using System.Net;
using System.Text;
using Newtonsoft.Json;
using Playnite.HoleInOne;

var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    count++;
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
var token = CancellationToken.None;
LocalDownloadChecks.Run(Check);
await HandoffRecoveryChecks.RunAsync(Check);
await PerformanceChecks.RunAsync(Check);
await ReceiptChecks.RunAsync(Check);
using var wire = new FakeBackend();
using var client = new BackendConnection("http://localhost:5010", "test-token", wire);
var session = await client.CreateSessionAsync(token);
Check(session.SessionId == "session1", "create session and deserialize stream URL");
Check(wire.Calls[0] == "POST /v1/holeinone/sessions", "session HTTP route");
Check(wire.Authorized, "bearer token sent");
await client.CheckDevelopmentHealthAsync(token);
Check(wire.Calls.Last() == "GET /health" && wire.Authorized, "development health check uses authenticated health route");
foreach (var invalidHealth in new[] { "{}", "{\"status\":\"ok\",\"service\":\"other\",\"contractVersion\":1,\"authentication\":\"development\"}", "{\"status\":\"ok\",\"service\":\"holeinone\",\"contractVersion\":2,\"authentication\":\"development\"}", "{\"status\":\"ok\",\"service\":\"holeinone\",\"contractVersion\":1,\"authentication\":\"accounts\"}" })
{
    using var healthWire = new FakeBackend { HealthBody = invalidHealth };
    using var healthClient = new BackendConnection("http://localhost:5010", handler: healthWire);
    await Reject(() => healthClient.CheckDevelopmentHealthAsync(token), "incompatible server cannot pass connection check");
}
using (var healthWire = new FakeBackend { HealthStatus = HttpStatusCode.Unauthorized })
using (var healthClient = new BackendConnection("http://localhost:5010", handler: healthWire))
    await Reject(() => healthClient.CheckDevelopmentHealthAsync(token), "unauthenticated server check fails");
await client.CreateSessionAsync(token, "epic-other-game");
Check(wire.LastGame == "epic-other-game", "selected game ID reaches session creation");
using var wrongGameWire = new FakeBackend { ResponseGame = "steam-wrong-game" };
using var wrongGameClient = new BackendConnection("http://localhost:5010", handler: wrongGameWire);
await Reject(() => wrongGameClient.CreateSessionAsync(token, "epic-other-game"), "wrong game session is rejected before opening stream");

foreach (var errorCase in new[]
{
    (Body: "{\"errorCode\":\"game_not_configured\",\"error\":\"private-server-detail\"}", Unconfigured: true),
    (Body: "{\"error\":\"This game has no configured cloud application.\"}", Unconfigured: true),
    (Body: "{\"errorCode\":\"unknown\",\"error\":\"private-server-detail\"}", Unconfigured: false),
    (Body: "<html>private-server-detail</html>", Unconfigured: false),
    (Body: "[]", Unconfigured: false),
    (Body: "null", Unconfigured: false),
    (Body: "", Unconfigured: false)
})
{
    using var errorWire = new FakeBackend { SessionStatus = HttpStatusCode.NotFound, SessionErrorBody = errorCase.Body };
    using var errorClient = new BackendConnection("http://localhost:5010", handler: errorWire);
    Exception failure = null;
    try { await errorClient.CreateSessionAsync(token, "steam-377160"); }
    catch (Exception error) { failure = error; }
    Check(failure != null && failure.Message.Contains(errorCase.Unconfigured ? "현재 클라우드 플레이를 지원하지 않음" : "서버 주소와 Backend 버전"),
        "session 404 distinguishes unconfigured games from missing routes, including legacy responses");
    Check(!failure.Message.Contains("private-server-detail") && errorWire.Calls.Count == 1,
        "session error hides response details and never retries a different game");
}

foreach (var errorCase in new[]
{
    (Code: "game_not_supported", Status: HttpStatusCode.NotFound),
    (Code: "game_not_ready", Status: HttpStatusCode.Conflict),
    (Code: "game_catalog_unavailable", Status: HttpStatusCode.ServiceUnavailable)
})
{
    using var errorWire = new FakeBackend { SessionStatus = errorCase.Status,
        SessionErrorBody = JsonConvert.SerializeObject(new { errorCode = errorCase.Code }) };
    using var errorClient = new BackendConnection("http://localhost:5010", handler: errorWire);
    CloudPlayUnavailableException failure = null;
    try { await errorClient.CreateSessionAsync(token, "steam-377160"); }
    catch (CloudPlayUnavailableException error) { failure = error; }
    Check(failure != null && errorWire.Calls.Count == 1, "unsupported or unready cloud game produces one warning without retry or preparation");
}

var transfer = new HandoffSession(client, session.SessionId);
Check(await transfer.PollAsync(true, token) == null && wire.SaveRequests == 0, "installation first waits for campfire");
wire.HasCampfire = true;
Check(await transfer.PollAsync(false, token) == null && wire.SaveRequests == 0, "campfire alone does not request save");
Check(await transfer.PollAsync(true, token) == null && wire.SaveRequests == 1, "both conditions request once and wait for ready");
Check(wire.LastRun == "run1", "save request uses campfire run ID");
await transfer.PollAsync(true, token);
Check(wire.SaveRequests == 1 && wire.Calls.Any(x => x.EndsWith("campfires?after=1")), "cursor and repeated polling do not duplicate save request");
wire.Ready = true;
var ticket = await transfer.PollAsync(true, token);
Check(ticket?.RequestId == "request1", "ready produces GameLoader ticket");
Check(await transfer.PollAsync(true, token) == null, "ticket delivered exactly once");

using var invalidWire = new FakeBackend { StreamUrl = null };
using var invalidClient = new BackendConnection("http://localhost:5010", handler: invalidWire);
await Reject(() => invalidClient.CreateSessionAsync(token), "disabled streaming cannot launch cloud play");
await Reject(() => client.GetSaveAsync("../session", "request1", token), "reject unsafe session ID");
using var failedWire = new FakeBackend { HasCampfire = true, Status = "failed" };
using var failedClient = new BackendConnection("http://localhost:5010", handler: failedWire);
await Reject(() => new HandoffSession(failedClient, "session1").PollAsync(true, token), "failed upload cannot hand off");
using var mismatchWire = new FakeBackend { HasCampfire = true, Ready = true, TicketSession = "other-session" };
using var mismatchClient = new BackendConnection("http://localhost:5010", handler: mismatchWire);
await Reject(() => new HandoffSession(mismatchClient, "session1").PollAsync(true, token), "reject ticket from another session");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
await Reject(() => client.GetCampfiresAsync("session1", 0, cancelled.Token), "cancellation stops HTTP work");

var root = Path.Combine(Path.GetTempPath(), "HoleInOne-Checks-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(root);
    var servicePath = Path.Combine(root, "holeinone.service.json");
    Check(ServiceConfiguration.Load(servicePath, false) == null, "developer build can omit service configuration");
    await Reject(() => Task.FromResult(ServiceConfiguration.Load(servicePath, true)), "distribution cannot fall back to localhost when configuration is missing");
    foreach (var value in new[] { "null", "{}", "{broken", "{\"BackendUrl\":\"http://service.example.com/\"}", "{\"BackendUrl\":\"https://localhost/\"}", "{\"BackendUrl\":\"https://127.0.0.1/\"}", "{\"BackendUrl\":\"https://service.example.com/api\"}", "{\"BackendUrl\":\"https://service.example.com/\",\"BearerToken\":\"shared-secret\"}" })
    {
        File.WriteAllText(servicePath, value);
        await Reject(() => Task.FromResult(ServiceConfiguration.Load(servicePath, true)), "invalid or credential-bearing service configuration rejected");
    }
    File.WriteAllText(servicePath, "{\"BackendUrl\":\"https://service.example.com\"}");
    Check(ServiceConfiguration.Load(servicePath, true).BackendUrl == "https://service.example.com/", "valid public service address is normalized");
    var developmentPath = Path.Combine(root, "holeinone.development.json");
    Check(DevelopmentServiceConfiguration.Load(developmentPath, false) == null, "normal developer build can omit team server settings");
    File.WriteAllText(developmentPath, JsonConvert.SerializeObject(new { BackendUrl = "http://203.0.113.10:5010", BearerToken = new string('t', 64) }));
    var development = DevelopmentServiceConfiguration.Load(developmentPath, false);
    Check(development.BackendUrl == "http://203.0.113.10:5010/" && development.BearerToken.Length == 64, "team package uses explicit IP and development authentication");
    await Reject(() => Task.FromResult(DevelopmentServiceConfiguration.Load(developmentPath, true)), "distribution rejects a development token file");
    foreach (var value in new[] { "null", "{}", "{\"BackendUrl\":\"http://203.0.113.10:5010/\",\"BearerToken\":\"short\"}", JsonConvert.SerializeObject(new { BackendUrl = "file:///tmp/", BearerToken = new string('t', 64) }), JsonConvert.SerializeObject(new { BackendUrl = "http://203.0.113.10:5010/", BearerToken = new string('t', 64) + "\r\n" }) })
    {
        File.WriteAllText(developmentPath, value);
        await Reject(() => Task.FromResult(DevelopmentServiceConfiguration.Load(developmentPath, false)), "unsafe development configuration rejected");
    }
    await Reject(() => Task.FromResult(HandoffSession.WriteTicket(root, ticket)), "missing GameLoader does not create ticket");
    var mod = Path.Combine(root, "mods", "GameLoader");
    Directory.CreateDirectory(mod);
    File.WriteAllText(Path.Combine(mod, "GameLoader.dll"), "test fixture");
    File.WriteAllText(Path.Combine(mod, "GameLoader.json"), "{}");
    var pending = HandoffSession.WriteTicket(root, ticket);
    var json = File.ReadAllText(pending);
    Check(json.Contains("\"backendUrl\"") && json.Contains("\"requestId\""), "ticket field casing matches GameLoader");
    Check(JsonConvert.DeserializeObject<TransferTicket>(json).RunId == "run1", "complete ticket file written");
    await Reject(() => Task.FromResult(HandoffSession.WriteTicket(root, ticket)), "unconsumed ticket is not overwritten");
    Check(File.ReadAllText(pending) == json && !Directory.GetFiles(mod, "*.tmp").Any(), "existing ticket preserved and temporary file cleaned");
}
finally { Directory.Delete(root, true); }
await PrewarmChecks.RunAsync(Check);
await SteamLinkChecks.RunAsync(Check);
StoreChecks.Run(Check);
LauncherSettingsChecks.Run(Check);
StorageChecks.Run(Check);
var completedAt = DateTimeOffset.Parse("2026-10-03T12:00:00+09:00");
var deadline = new HandoffDeadline(completedAt);
Check(deadline.RemainingSeconds(completedAt) == 3600, "download completion starts a 60 minute countdown");
Check(!deadline.HasExpired(completedAt.AddMinutes(59)), "handoff remains available before deadline");
Check(deadline.RemainingSeconds(completedAt.AddMinutes(60).AddMilliseconds(-1)) == 1, "countdown rounds remaining fraction up");
Check(deadline.HasExpired(completedAt.AddMinutes(60)), "deadline expires at exactly 60 minutes");
Check(deadline.RemainingSeconds(completedAt.AddHours(2)) == 0, "late timer tick after suspend does not extend deadline");
Console.WriteLine($"{count} checks passed.");

sealed class FakeBackend : HttpMessageHandler
{
    public string HealthBody = "{\"status\":\"ok\",\"service\":\"holeinone\",\"contractVersion\":1,\"authentication\":\"development\"}";
    public HttpStatusCode HealthStatus = HttpStatusCode.OK;
    public HttpStatusCode SessionStatus = HttpStatusCode.OK;
    public string SessionErrorBody;
    public string StreamUrl = "https://example.com/stream";
    public bool HasCampfire;
    public bool Ready;
    public string Status = "requested";
    public string TicketSession = "session1";
    public int SaveRequests;
    public string LastRun;
    public string LastGame;
    public string ResponseGame;
    public bool Authorized = true;
    public readonly List<string> Calls = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri.PathAndQuery;
        Calls.Add(request.Method + " " + path);
        Authorized &= request.Headers.Authorization?.Parameter == "test-token";
        if (path == "/health")
            return new HttpResponseMessage(HealthStatus) { Content = new StringContent(HealthBody, Encoding.UTF8, "application/json") };
        object body;
        if (path == "/v1/holeinone/sessions")
        {
            if (SessionStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(SessionStatus) { Content = new StringContent(SessionErrorBody ?? "") };
            LastGame = request.Content == null ? null : (string)Newtonsoft.Json.Linq.JObject.Parse(await request.Content.ReadAsStringAsync())["gameId"];
            body = new { sessionId = "session1", gameId = ResponseGame ?? LastGame, streamUrl = StreamUrl };
        }
        else if (path.Contains("/campfires?"))
        {
            body = HasCampfire && path.EndsWith("after=0")
                ? new[] { new { sequence = 1, notice = new { runId = "run1" } } }
                : Array.Empty<object>();
        }
        else
        {
            if (request.Method == HttpMethod.Post)
            {
                SaveRequests++;
                LastRun = (string)Newtonsoft.Json.Linq.JObject.Parse(await request.Content.ReadAsStringAsync())["runId"];
            }
            body = new
            {
                requestId = "request1", runId = "run1", status = Ready ? "ready" : Status,
                ticket = Ready ? new TransferTicket
                {
                    BackendUrl = "http://localhost:5010/", SessionId = TicketSession,
                    RequestId = "request1", RunId = "run1"
                } : null
            };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
        };
    }
}
