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
await SaveWorkflowChecks.RunAsync(Check);
await PerformanceChecks.RunAsync(Check);
await ReceiptChecks.RunAsync(Check);
using var wire = new FakeBackend();
using var client = new BackendConnection("http://localhost:5010", handler: wire);
await client.CheckDevelopmentHealthAsync(token);
Check(wire.Calls.Last() == "GET /health" && wire.NoCredentialsSent, "health contract check sends no account credentials");
foreach (var invalidHealth in new[] { "{}", "{\"status\":\"ok\",\"service\":\"other\",\"contractVersion\":1,\"authentication\":\"development\"}", "{\"status\":\"ok\",\"service\":\"holeinone\",\"contractVersion\":2,\"authentication\":\"development\"}", "{\"status\":\"ok\",\"service\":\"holeinone\",\"contractVersion\":1,\"authentication\":\"accounts\"}" })
{
    using var healthWire = new FakeBackend { HealthBody = invalidHealth };
    using var healthClient = new BackendConnection("http://localhost:5010", handler: healthWire);
    await Reject(() => healthClient.CheckDevelopmentHealthAsync(token), "incompatible server cannot pass connection check");
}
using (var healthWire = new FakeBackend { HealthStatus = HttpStatusCode.Unauthorized })
using (var healthClient = new BackendConnection("http://localhost:5010", handler: healthWire))
    await Reject(() => healthClient.CheckDevelopmentHealthAsync(token), "unauthenticated server check fails");
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
    public string HealthBody = "{\"status\":\"ok\",\"service\":\"holeinone\",\"contractVersion\":2,\"authentication\":\"accounts\"}";
    public HttpStatusCode HealthStatus = HttpStatusCode.OK;
    public bool NoCredentialsSent = true;
    public readonly List<string> Calls = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(request.Method + " " + request.RequestUri.PathAndQuery);
        NoCredentialsSent &= request.Headers.Authorization == null;
        if (request.RequestUri.AbsolutePath != "/health") throw new Exception("Unexpected API request");
        return Task.FromResult(new HttpResponseMessage(HealthStatus) { Content = new StringContent(HealthBody, Encoding.UTF8, "application/json") });
    }
}
