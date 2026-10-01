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
using var wire = new FakeBackend();
using var client = new BackendConnection("http://localhost:5010", "test-token", wire);
var session = await client.CreateSessionAsync(token);
Check(session.SessionId == "session1", "create session and deserialize stream URL");
Check(wire.Calls[0] == "POST /v1/holeinone/sessions", "session HTTP route");
Check(wire.Authorized, "bearer token sent");

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
Console.WriteLine($"{count} checks passed.");

sealed class FakeBackend : HttpMessageHandler
{
    public string StreamUrl = "https://example.com/stream";
    public bool HasCampfire;
    public bool Ready;
    public string Status = "requested";
    public string TicketSession = "session1";
    public int SaveRequests;
    public string LastRun;
    public bool Authorized = true;
    public readonly List<string> Calls = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri.PathAndQuery;
        Calls.Add(request.Method + " " + path);
        Authorized &= request.Headers.Authorization?.Parameter == "test-token";
        object body;
        if (path == "/v1/holeinone/sessions")
        {
            body = new { sessionId = "session1", streamUrl = StreamUrl };
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
