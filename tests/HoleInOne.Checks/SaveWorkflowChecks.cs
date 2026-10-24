using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using Playnite.HoleInOne;
using Playnite.DesktopApp.HoleInOne;

static class SaveWorkflowChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var changedId in new[] { false, true })
        {
            using var wire = new Wire("failed", "no_save") { RestartRecovery = true, ChangedId = changedId };
            using var backend = new BackendConnection("https://fixture.example/", handler: wire);
            try
            {
                var restored = await Run(backend, new ExportReceipts(), default);
                check(!changedId && !restored, "restart recovery preserves existing request and distinguishes no_save");
            }
            catch (InvalidDataException) { check(changedId, "restart recovery rejects replacement request ID"); }
            check(wire.Methods.SequenceEqual(new[] { HttpMethod.Get, HttpMethod.Post }), "only explicit restart recovery resumes after reading existing transfer");
        }
        foreach (var first in new[] { "no_save", "exporting", "preparing" })
        {
            using var wire = new Wire(first, "no_save");
            using var backend = new BackendConnection("https://fixture.example/", handler: wire);
            var receipts = new ExportReceipts();
            var restored = await Run(backend, receipts, default);
            check(!restored && receipts.Count == 0, first + " to no_save never reports restoration or queues receipt");
            check(wire.Methods.All(m => m == HttpMethod.Get), "recovery workflow only reads existing export");
        }
        using (var wire = new Wire("failed"))
        using (var backend = new BackendConnection("https://fixture.example/", handler: wire))
        {
            try { await Run(backend, new ExportReceipts(), default); throw new Exception("failed export accepted"); }
            catch (InvalidOperationException) { check(wire.Methods.Count == 1, "failed recovery is surfaced without new export"); }
        }
        using (var wire = new Wire("exporting"))
        using (var backend = new BackendConnection("https://fixture.example/", handler: wire))
        using (var stop = new CancellationTokenSource())
        {
            var pending = Run(backend, new ExportReceipts(), stop.Token);
            stop.Cancel();
            try { await pending; throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { check(wire.Methods.Count == 1, "closing during export polling prevents subsequent requests"); }
        }
        using (var wire = new Wire("ready"))
        using (var backend = new BackendConnection("https://fixture.example/", handler: wire))
        {
            try
            {
                await SaveHandoffWorkflow.ApplyAsync(null, "steam-1", new CloudSession { SessionId = "session" },
                    backend, new ExportReceipts(), _ => throw new Exception("read local game"), _ => { },
                    () => throw new OperationCanceledException("account changed"), default, new PerformanceTrace("test", _ => { }), true);
                throw new Exception("stale account accepted");
            }
            catch (OperationCanceledException) { check(wire.Methods.Count == 1, "account change blocks download and local save access"); }
        }
    }

    private static Task<bool> Run(BackendConnection backend, ExportReceipts receipts, CancellationToken ct) =>
        SaveHandoffWorkflow.ApplyAsync(null, "steam-1", new CloudSession { SessionId = "session" }, backend,
            receipts, _ => throw new Exception("unexpected local save access"), _ => { }, () => { }, ct,
            new PerformanceTrace("test", _ => { }), recoveryOnly: true);

    private sealed class Wire(params string[] states) : HttpMessageHandler
    {
        public readonly List<HttpMethod> Methods = new();
        public bool RestartRecovery, ChangedId;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Methods.Add(request.Method);
            if ((request.Method != HttpMethod.Get && !(RestartRecovery && Methods.Count == 2 && request.Method == HttpMethod.Post)) || !request.RequestUri.AbsolutePath.EndsWith("/export-handoff"))
                throw new Exception("unexpected mutation or download");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonConvert.SerializeObject(new { requestId = ChangedId && Methods.Count > 1 ? "other" : "one", gameId = "steam-1",
                    canRetry = RestartRecovery,
                    errorCode = RestartRecovery && Methods.Count == 1 ? "recovery_required" : null,
                    status = states[Math.Min(Methods.Count - 1, states.Length - 1)] })) });
        }
    }
}

// Only host dependencies are substitutes. Tests execute the production workflow and HTTP client.
// Any accidental entry into save application fails; these checks do not validate successful file restoration.
namespace Playnite.SDK.Models
{
    public sealed class Game
    {
        public Guid Id { get; set; }
        public string InstallDirectory { get; set; }
        public bool IsRunning { get; set; }
        public bool IsLaunching { get; set; }
        public bool IsInstalled { get; set; }
        public bool IsInstalling { get; set; }
    }
}
namespace Playnite.SDK
{
    public static class LogManager { public static Logger GetLogger() => new(); }
    public sealed class Logger { public void Info(string value) { } public void Warn(string value) { } }
}
namespace Playnite.HoleInOne
{
    public static class SteamLinkContext { public static void RequireLocalAccount() => throw new Exception("unexpected local account access"); }
    public static class LocalSteamAccount
    {
        public static uint ActiveAccountId() => throw new Exception("unexpected local account access");
        public static string SteamId(uint value) => throw new Exception("unexpected local account access");
        public static string RequireSteamId() => throw new Exception("unexpected local account access");
    }
}
namespace Playnite.DesktopApp.HoleInOne
{
    internal sealed class ExportReceipts
    {
        public int Count { get; private set; }
        public void Enqueue(string session, string request, string hash) { Count++; }
    }
}
