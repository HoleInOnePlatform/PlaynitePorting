using HoleInOne.SteamLink;
using System.Net;

var count = 0;
async Task Check(string name, Func<Task> action)
{
    var originalSink = SteamDiagnostics.Sink;
    var originalFlag = Environment.GetEnvironmentVariable("HOLEINONE_STEAM_DIAGNOSTICS");
    SteamDiagnostics.Sink = _ => { };
    Environment.SetEnvironmentVariable("HOLEINONE_STEAM_DIAGNOSTICS", "1");
    try { await action(); count++; Console.WriteLine("PASS " + name); }
    finally
    {
        SteamDiagnostics.Sink = originalSink;
        Environment.SetEnvironmentVariable("HOLEINONE_STEAM_DIAGNOSTICS", originalFlag);
    }
}
async Task Run(Wire wire, bool uninstall = false, Action? local = null, uint appId = 42)
{
    using var http = new HttpClient(wire);
    await new RemoteDownloadApi(http, "fixture-secret").RequestAsync(appId, uninstall, "THIS-PC",
        new HashSet<string> { "192.168.1.2" }, local ?? (() => { }), default);
}
async Task Reject(Wire wire, string code, Action? local = null, uint appId = 42)
{
    try { await Run(wire, local: local, appId: appId); throw new Exception("Unexpected acceptance"); }
    catch (ProbeFailure error) when (error.Code == code) { }
}
await Check("install reaches matching PC with app ID only and no path override", async () =>
{
    var wire = new Wire(); await Run(wire);
    if (wire.Mutations != 1 || wire.LastMethod != "InstallClientApp") throw new Exception("Wrong mutation");
});
await Check("uninstall uses server mutation", async () =>
{
    var wire = new Wire(); await Run(wire, true);
    if (wire.Mutations != 1 || wire.LastMethod != "UninstallClientApp") throw new Exception("Wrong mutation");
});
await Check("other PCs are never selected", async () =>
{
    var wire = new Wire { Machine = "OTHER-PC" }; await Reject(wire, "REMOTE_CLIENT_NOT_FOUND");
    if (wire.Mutations != 0) throw new Exception("Mutation sent");
});
await Check("same name with different private IP is rejected", async () =>
{
    var wire = new Wire { Ip = "10.0.0.1" }; await Reject(wire, "REMOTE_CLIENT_NOT_FOUND");
    if (wire.Mutations != 0) throw new Exception("Mutation sent");
});
await Check("ambiguous matching sessions are rejected", async () =>
{
    var wire = new Wire { Duplicate = true }; await Reject(wire, "REMOTE_CLIENT_AMBIGUOUS");
    if (wire.Mutations != 0) throw new Exception("Mutation sent");
});
await Check("closed local Steam sends no HTTP request", async () =>
{
    var wire = new Wire(); await Reject(wire, "REMOTE_LOCAL_CLIENT_REQUIRED", () => throw new ProbeFailure("REMOTE_LOCAL_CLIENT_REQUIRED"));
    if (wire.Requests != 0) throw new Exception("Request sent");
});
await Check("account change before mutation cancels dispatch", async () =>
{
    var wire = new Wire(); var checks = 0;
    await Reject(wire, "REMOTE_LOCAL_CLIENT_REQUIRED", () => { if (++checks == 2) throw new ProbeFailure("REMOTE_LOCAL_CLIENT_REQUIRED"); });
    if (wire.Mutations != 0) throw new Exception("Mutation sent");
});
await Check("invalid app ID sends nothing", async () =>
{
    var wire = new Wire(); await Reject(wire, "REMOTE_INVALID_APP", appId: 0);
    if (wire.Requests != 0) throw new Exception("Request sent");
});
await Check("Steam rejection is not reported as accepted or retried", async () =>
{
    var wire = new Wire { Result = "15" }; await Reject(wire, "REMOTE_REQUEST_FAILED");
    if (wire.Mutations != 1) throw new Exception("Mutation retried");
});
await Check("missing acknowledgement is not success", async () =>
{
    var wire = new Wire { Result = null }; await Reject(wire, "REMOTE_UNCONFIRMED");
    if (wire.Mutations != 1) throw new Exception("Mutation retried");
});
await Check("network failures are sanitized without retry or fallback", async () =>
{
    var wire = new Wire { NetworkFailure = true }; await Reject(wire, "REMOTE_REQUEST_FAILED");
    if (wire.Mutations != 1) throw new Exception("Mutation retried");
});
await Check("malformed success body is rejected", async () =>
{
    var wire = new Wire { Body = "not-json" }; await Reject(wire, "REMOTE_UNCONFIRMED");
});
await Check("diagnostics retain Steam rejection without credentials or request URLs", async () =>
{
    var diagnostics = new List<string>();
    SteamDiagnostics.Sink = diagnostics.Add;
    await Reject(new Wire { Result = "15" }, "REMOTE_REQUEST_FAILED");
    await Reject(new Wire { NetworkFailure = true }, "REMOTE_REQUEST_FAILED");
    if (!diagnostics.Any(line => line.Contains("stage=InstallClientApp outcome=response result=15 http=200")))
        throw new Exception("Missing Steam result");
    if (diagnostics.Any(line => line.Contains("fixture-secret") || line.Contains("access_token") ||
        line.Contains("THIS-PC") || line.Contains("192.168.1.2") || line.Contains("https://")))
        throw new Exception("Sensitive diagnostic data");
});
await Check("diagnostic write failure does not block installation", async () =>
{
    SteamDiagnostics.Sink = _ => throw new IOException("fixture failure");
    await Run(new Wire());
});
await Check("diagnostics require explicit opt-in", async () =>
{
    var diagnostics = new List<string>();
    SteamDiagnostics.Sink = diagnostics.Add;
    foreach (var flag in new string?[] { null, "0", "true" })
    {
        Environment.SetEnvironmentVariable("HOLEINONE_STEAM_DIAGNOSTICS", flag);
        await Run(new Wire());
        if (diagnostics.Count != 0) throw new Exception("Diagnostics were not disabled");
    }
    Environment.SetEnvironmentVariable("HOLEINONE_STEAM_DIAGNOSTICS", "1");
    await Run(new Wire());
    if (diagnostics.Count == 0) throw new Exception("Opt-in did not enable diagnostics");
});
await Check("cleanup failures preserve the original error and stop callbacks", async () =>
{
    var steps = new List<string>();
    var stopped = new TaskCompletionSource();
    async Task Request()
    {
        try { throw new ProbeFailure("REMOTE_DISCONNECTED"); }
        finally
        {
            await RemoteSessionCleanup.RunAsync(
                () => { steps.Add("logoff"); throw new InvalidOperationException(); },
                () => { steps.Add("disconnect"); throw new InvalidOperationException(); },
                () => { steps.Add("stop"); stopped.SetResult(); },
                stopped.Task);
        }
    }
    try { await Request(); throw new Exception("Original failure lost"); }
    catch (ProbeFailure error) when (error.Code == "REMOTE_DISCONNECTED") { }
    if (!steps.SequenceEqual(new[] { "logoff", "disconnect", "stop" })) throw new Exception("Cleanup skipped");
    await RemoteSessionCleanup.RunAsync(() => { }, () => { }, () => { },
        Task.FromException(new InvalidOperationException("fixture pump failure")));
});
Console.WriteLine($"{count} remote Steam checks passed.");

sealed class Wire : HttpMessageHandler
{
    public string Machine = "THIS-PC", Ip = "192.168.1.2", Body = "{\"response\":{}}";
    public string? Result = "1", LastMethod;
    public bool Duplicate, NetworkFailure;
    public int Mutations, Requests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        var method = request.RequestUri!.Segments[^2].Trim('/');
        string body;
        if (method == "GetAllClientLogonInfo")
        {
            if (request.Method != HttpMethod.Get) throw new Exception("Read must use GET");
            var session = "{\"machine_name\":\"" + Machine + "\",\"client_instanceid\":\"18446744073709551000\"}";
            body = "{\"response\":{\"sessions\":[" + session + (Duplicate ? "," + session : "") + "]}}";
        }
        else if (method == "GetClientInfo")
            body = "{\"response\":{\"client_info\":{\"machine_name\":\"" + Machine + "\",\"ip_private\":\"" + Ip + "\"}}}";
        else
        {
            Mutations++; LastMethod = method;
            if (request.Method != HttpMethod.Post || request.RequestUri.Query.Length != 0) throw new Exception("Mutation must POST");
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('='))
                .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            if (form.Count != 3 || form["appid"] != "42" || form["client_instanceid"] != "18446744073709551000" ||
                form["access_token"] != "fixture-secret") throw new Exception("Wrong fields or precision loss");
            if (NetworkFailure) throw new HttpRequestException("Secret URI fixture-secret");
            body = Body;
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (Result != null && Mutations > 0) response.Headers.Add("x-eresult", Result);
        return response;
    }
}

namespace HoleInOne.SteamLink
{
    // Worker failure contract only; the API under test is the production source linked above.
    public sealed class ProbeFailure(string code) : Exception(code) { public string Code => Message; }
}
