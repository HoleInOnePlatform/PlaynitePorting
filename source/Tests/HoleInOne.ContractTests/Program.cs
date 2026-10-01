using Newtonsoft.Json.Linq;
using Playnite.GameLink;
using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static int count;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static async Task ExpectFailure(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected rejection."); }
    private static async Task Test(string name, Func<Task> test) { await test(); count++; Console.WriteLine("PASS " + name); }
    private static JObject Identity(string run = "17123:fixture-seed") => new JObject { ["profileId"] = 1, ["runId"] = run, ["checkpoint"] = "0:1,2;3,4" };
    private static JObject Attempt(string artifact = null) => new JObject { ["id"] = Guid.NewGuid().ToString(), ["artifactId"] = artifact ?? Guid.NewGuid().ToString(),
        ["identity"] = Identity(), ["generation"] = "fixture-generation", ["expires"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        ["lease"] = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() };
    private static JObject Manifest(JObject a, byte[] data) => new JObject { ["version"] = 2, ["gameId"] = "2868840", ["build"] = SaveRestoreService.SupportedBuild,
        ["identity"] = a["identity"].DeepClone(), ["generation"] = a["generation"], ["artifactId"] = a["artifactId"], ["expires"] = a["expires"],
        ["mods"] = new JArray("GameLinkPoint@v0.4.0"), ["files"] = new JArray(new JObject { ["path"] = "current_run.save", ["size"] = data.Length, ["sha256"] = SaveRestoreService.Hash(data) }) };
    private sealed class PendingHandoff : ILocalHandoff
    {
        public int Calls; public CancellationToken Token; public readonly TaskCompletionSource<bool> Complete = new TaskCompletionSource<bool>();
        public Task<bool> SwitchAsync(Guid gameId, JObject attempt, CancellationToken token) { Calls++; Token = token; return Complete.Task; }
    }
    private static string Event(string session, long seq, string type, JObject a) => new JObject { ["version"] = 2, ["sessionId"] = session,
        ["gameId"] = "2868840", ["sequence"] = seq, ["type"] = type, ["eventId"] = Guid.NewGuid().ToString(),
        ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["payload"] = a }.ToString();
    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public string Session = Guid.NewGuid().ToString(); public JObject Attempt = Program.Attempt(); public byte[] Bytes = Encoding.UTF8.GetBytes("fixture-cloud-run");
        public string State = "Frozen"; public bool Corrupt; public int Commits; public JObject Document;
        public HoleInOneBackendClient Api { get; }
        public FixtureServer()
        {
            Document = Manifest(Attempt, Bytes);
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var url = "http://127.0.0.1:" + port + "/"; listener.Prefixes.Add(url); listener.Start(); Api = new HoleInOneBackendClient(new Uri(url), () => "fixture-user");
            _ = Serve();
        }
        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { return; }
                var p = c.Request.Url.AbsolutePath; byte[] response;
                try
                {
                    if (c.Request.Headers["Authorization"] != "Bearer fixture-user") { c.Response.StatusCode = 403; response = Encoding.UTF8.GetBytes("{}"); }
                    else if (p.EndsWith("/file")) response = Corrupt ? Encoding.UTF8.GetBytes("broken") : Bytes;
                    else if (p.Contains("/artifacts/")) response = Encoding.UTF8.GetBytes(Document.ToString());
                    else
                    {
                        JObject body = null;
                        if (c.Request.HttpMethod == "POST") using (var reader = new StreamReader(c.Request.InputStream)) body = JObject.Parse(reader.ReadToEnd());
                        if (p.EndsWith("/prepare")) State = "Frozen";
                        if (p.EndsWith("/restored")) State = "Restored";
                        if (p.EndsWith("/commit"))
                        { Check(State == "Restored" && (bool?)body["loaded"] == true && SaveRestoreService.SameIdentity(body["observed"], Attempt["identity"]), "Commit proof mismatch"); State = "Committed"; Commits++; }
                        if (p.EndsWith("/fail")) State = "Failed";
                        response = Encoding.UTF8.GetBytes(new JObject { ["id"] = Session, ["gameId"] = "2868840", ["state"] = State, ["attempt"] = Attempt, ["stream"] = "ACTIVE" }.ToString());
                    }
                    c.Response.ContentLength64 = response.Length; await c.Response.OutputStream.WriteAsync(response, 0, response.Length);
                }
                catch { c.Response.StatusCode = 500; }
                finally { c.Response.Close(); }
            }
        }
        public void Dispose() { listener.Close(); Api.Dispose(); }
    }
    private sealed class FakeLocalGame : ILocalGameAdapter
    {
        public string Target; public bool WrongIdentity; public bool LaunchFailure; public bool Running;
        public Task WaitForInstallationAsync(CancellationToken token) => Task.CompletedTask;
        public void ValidateCompatibility() { }
        public SaveRestoreService CreateRestoreService(JObject identity) => new SaveRestoreService(Target, () => !Running);
        public Task LaunchAsync(string contextPath, CancellationToken token)
        {
            if (LaunchFailure) throw new IOException("Injected launch failure");
            var context = JObject.Parse(File.ReadAllText(contextPath));
            _ = Task.Run(async () =>
            {
                using (var pipe = new NamedPipeClientStream(".", (string)context["pipeName"], PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await pipe.ConnectAsync(2000);
                    var result = new JObject { ["version"] = 2, ["resultId"] = Guid.NewGuid().ToString("N"), ["sessionId"] = context["sessionId"],
                        ["attemptId"] = context["attemptId"], ["artifactId"] = context["artifactId"], ["generation"] = context["generation"], ["nonce"] = context["nonce"],
                        ["loaded"] = true, ["identity"] = Identity(WrongIdentity ? "wrong:run" : "17123:fixture-seed"), ["reason"] = "fixture" };
                    var bytes = Encoding.UTF8.GetBytes(result.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                    await pipe.WriteAsync(bytes, 0, bytes.Length); await pipe.FlushAsync();
                    using (var reader = new StreamReader(pipe)) await reader.ReadLineAsync();
                }
            });
            return Task.CompletedTask;
        }
    }
    private static string Target(string root, string name) { var dir = Path.Combine(root, name); Directory.CreateDirectory(dir); var p = Path.Combine(dir, "current_run.save"); File.WriteAllText(p, "original-local"); return p; }
    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "HoleInOne-contract-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var playbackNow = DateTimeOffset.UtcNow; var playbackBackend = new Uri("http://127.0.0.1:43117/");
        var playbackTicket = Guid.NewGuid().ToString();
        await Test("startup errors explain safe codes without echoing server diagnostics", () => {
            Check(BackendStartupFailure.Describe("aws_credentials_missing").Contains("aws login --profile holeinone"), "AWS setup guidance missing");
            Check(BackendStartupFailure.Describe("aws_access_denied").Contains("권한"), "AWS permission guidance missing");
            var secret = "https://attacker.example/?token=secret-diagnostic";
            Check(!BackendStartupFailure.Describe(secret).Contains(secret), "Raw server diagnostic leaked"); return Task.CompletedTask;
        });
        JObject Playback(string mode, string url) => new JObject { ["playbackMode"] = mode, ["playUrl"] = url,
            ["ticket"] = playbackTicket, ["expires"] = playbackNow.AddMinutes(1).ToUnixTimeMilliseconds() };
        await Test("backend playback accepts AWS hosted HTTPS link and SDK origin address", () => {
            var hosted = "https://gameliftstreams.aws.com/su-1AB2C3De4/stream?token=fixture-token";
            Check(BackendPlaybackAddress.Validate(playbackBackend, Playback("aws-hosted-url", hosted), playbackNow).AbsoluteUri == hosted, "Hosted link rejected");
            foreach (var url in new[] { "/#ticket=" + playbackTicket, playbackBackend.AbsoluteUri + "#ticket=" + playbackTicket })
                Check(BackendPlaybackAddress.Validate(playbackBackend, Playback("sdk", url), playbackNow).AbsoluteUri == playbackBackend.AbsoluteUri + "#ticket=" + playbackTicket, "SDK link changed");
            return Task.CompletedTask;
        });
        await Test("backend playback rejects incomplete contract, expired link and unknown mode", async () => {
            var good = Playback("aws-hosted-url", "https://gameliftstreams.aws.com/su-Ab123/stream?token=fixture");
            foreach (var field in new[] { "playbackMode", "playUrl", "expires" }) {
                var missing = (JObject)good.DeepClone(); missing.Remove(field);
                await ExpectFailure(() => { BackendPlaybackAddress.Validate(playbackBackend, missing, playbackNow); return Task.CompletedTask; }); }
            var expired = (JObject)good.DeepClone(); expired["expires"] = playbackNow.ToUnixTimeMilliseconds();
            var unsupported = (JObject)good.DeepClone(); unsupported["playbackMode"] = "other";
            var wrongType = (JObject)good.DeepClone(); wrongType["expires"] = "future";
            foreach (var response in new[] { expired, unsupported, wrongType })
                await ExpectFailure(() => { BackendPlaybackAddress.Validate(playbackBackend, response, playbackNow); return Task.CompletedTask; });
        });
        await Test("AWS hosted playback rejects foreign origins, credentials and malformed capability", async () => {
            foreach (var url in new[] { "http://gameliftstreams.aws.com/su-Ab123/stream?token=fixture", "https://gameliftstreams.aws.com.attacker.example/su-Ab123/stream?token=fixture",
                "https://attacker.example/su-Ab123/stream?token=fixture", "https://user:secret@gameliftstreams.aws.com/su-Ab123/stream?token=fixture",
                "https://gameliftstreams.aws.com:444/su-Ab123/stream?token=fixture", "https://gameliftstreams.aws.com/su-Ab123/stream?token=fixture#fragment",
                "https://gameliftstreams.aws.com/su-Ab123/other?token=fixture", "https://gameliftstreams.aws.com/su-Ab123/stream?token=",
                "https://gameliftstreams.aws.com/su-Ab123/stream?token=%20", "https://gameliftstreams.aws.com/su-Ab123/stream?token=fixture&other=secret",
                "https://gameliftstreams.aws.com/su-Ab123/stream?token=fixture;other=secret", "//gameliftstreams.aws.com/su-Ab123/stream?token=fixture" })
                await ExpectFailure(() => { BackendPlaybackAddress.Validate(playbackBackend, Playback("aws-hosted-url", url), playbackNow); return Task.CompletedTask; });
        });
        await Test("SDK playback rejects foreign origins, token mismatch and extra parameters", async () => {
            foreach (var url in new[] { "https://attacker.example/#ticket=" + playbackTicket, "http://localhost:43117/#ticket=" + playbackTicket,
                "http://127.0.0.1:43118/#ticket=" + playbackTicket, "http://user:secret@127.0.0.1:43117/#ticket=" + playbackTicket,
                "http://127.0.0.1:43117/?token=secret#ticket=" + playbackTicket, "/#ticket=wrong", "/#ticket=" + playbackTicket + "&other=secret",
                "#ticket=" + playbackTicket, "//127.0.0.1:43117/#ticket=" + playbackTicket, "/base/#ticket=" + playbackTicket })
                await ExpectFailure(() => { BackendPlaybackAddress.Validate(playbackBackend, Playback("sdk", url), playbackNow); return Task.CompletedTask; });
            var missing = Playback("sdk", "/#ticket=" + playbackTicket); missing.Remove("ticket");
            await ExpectFailure(() => { BackendPlaybackAddress.Validate(playbackBackend, missing, playbackNow); return Task.CompletedTask; });
        });
        await Test("native connection stores current-user encrypted token and reopens", () => {
            var path = Path.Combine(root, "connection", "backend-connection.bin");
            var settings = new NativeBackendConfiguration("http://127.0.0.1:43117/", "fixture-credential-" + Guid.NewGuid().ToString("N"));
            settings.Save(path); var loaded = NativeBackendConfiguration.Load(path, false);
            Check(loaded.Address == settings.Address && loaded.Token == settings.Token, "Encrypted record failed to reopen");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(settings.Token), "Token written in plaintext"); return Task.CompletedTask;
        });
        await Test("native connection rejects corrupt record without modifying it", async () => {
            var path = Path.Combine(root, "connection", "corrupt.bin"); File.WriteAllText(path, "broken");
            await ExpectFailure(() => { NativeBackendConfiguration.Load(path, false); return Task.CompletedTask; });
            Check(File.ReadAllText(path) == "broken", "Corrupt record was destroyed");
        });
        await Test("native connection rejects remote HTTP and secret-bearing URL", async () => {
            foreach (var address in new[] { "http://example.com/", "https://example.com/base/", "https://user:secret@example.com/", "https://example.com/?token=secret", "https://example.com/#secret" })
                await ExpectFailure(() => { new NativeBackendConfiguration(address, "fixture-token"); return Task.CompletedTask; });
        });
        await Test("native connection never mixes partial environment and saved credentials", async () => {
            var url = Environment.GetEnvironmentVariable("HIO_BACKEND_URL"); var token = Environment.GetEnvironmentVariable("HIO_USER_TOKEN");
            try { Environment.SetEnvironmentVariable("HIO_BACKEND_URL", "https://other.example/"); Environment.SetEnvironmentVariable("HIO_USER_TOKEN", null);
                await ExpectFailure(() => { NativeBackendConfiguration.Load(Path.Combine(root, "missing.bin")); return Task.CompletedTask; }); }
            finally { Environment.SetEnvironmentVariable("HIO_BACKEND_URL", url); Environment.SetEnvironmentVariable("HIO_USER_TOKEN", token); }
        });
        await Test("native connection changes cannot send new token to old origin", async () => {
            var url = Environment.GetEnvironmentVariable("HIO_BACKEND_URL"); var token = Environment.GetEnvironmentVariable("HIO_USER_TOKEN");
            try { Environment.SetEnvironmentVariable("HIO_BACKEND_URL", "http://127.0.0.1:43117/"); Environment.SetEnvironmentVariable("HIO_USER_TOKEN", "fixture-old");
                using (var api = HoleInOneBackendClient.FromEnvironment()) { Environment.SetEnvironmentVariable("HIO_BACKEND_URL", "https://other.example/"); Environment.SetEnvironmentVariable("HIO_USER_TOKEN", "fixture-new");
                    try { await api.GetAsync("v2/capabilities", CancellationToken.None); throw new Exception("Changed origin accepted"); }
                    catch (NativeBackendConfigurationException) { } } }
            finally { Environment.SetEnvironmentVariable("HIO_BACKEND_URL", url); Environment.SetEnvironmentVariable("HIO_USER_TOKEN", token); }
        });
        await Test("ready/session isolation and duplicate delivery", async () => {
            var sessionId = Guid.NewGuid().ToString(); var local = new PendingHandoff(); var a = Attempt();
            using (var session = new HandoffSession(Guid.NewGuid(), "2868840", sessionId, local)) {
                Check(!session.Receive(Event(Guid.NewGuid().ToString(), 1, "handoff.ready", a)), "Wrong user session accepted");
                Check(session.Receive(Event(sessionId, 1, "handoff.point.reached", a)) && local.Calls == 0, "Candidate caused launch");
                Check(session.Receive(Event(sessionId, 3, "handoff.ready", a)), "Ready rejected");
                Check(session.Receive(Event(sessionId, 7, "handoff.ready", a)) && local.Calls == 1, "Duplicate launch");
                local.Complete.SetResult(true); await Task.Delay(10); Check(session.State == HandoffState.Local, "Verified transition missing"); }
        });
        await Test("cancel interrupts Switching and late success cannot commit", async () => {
            var id = Guid.NewGuid().ToString(); var local = new PendingHandoff(); var a = Attempt();
            using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, local)) {
                s.Receive(Event(id, 1, "handoff.ready", a)); s.Receive(Event(id, 2, "handoff.cancelled", a));
                Check(local.Token.IsCancellationRequested, "Attempt not cancelled"); local.Complete.SetResult(true); await Task.Delay(10); Check(s.State == HandoffState.Cancelled, "Late completion applied"); }
        });
        await Test("reconnected snapshot starts one ready attempt", async () => {
            var id = Guid.NewGuid().ToString(); var local = new PendingHandoff(); var a = Attempt();
            using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, local)) {
                var snapshot = new JObject { ["id"] = id, ["gameId"] = "2868840", ["state"] = "ArtifactReady", ["attempt"] = a };
                s.RestoreSnapshot(snapshot); s.RestoreSnapshot(snapshot); Check(local.Calls == 1, "Snapshot duplicate launch"); local.Complete.SetResult(false); await Task.Delay(10); }
        });
        await Test("snapshot cursor ignores historical superseded ready", async () => {
            var id = Guid.NewGuid().ToString(); var local = new PendingHandoff(); var latest = Attempt();
            using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, local)) {
                s.RestoreSnapshot(new JObject { ["id"] = id, ["gameId"] = "2868840", ["state"] = "ArtifactReady", ["attempt"] = latest, ["cursor"] = 20 });
                Check(s.Receive(Event(id, 10, "handoff.ready", Attempt())) && local.Calls == 1 && !local.Token.IsCancellationRequested, "Historical ready superseded snapshot");
                local.Complete.SetResult(false); await Task.Delay(10); }
        });
        await Test("expired ready replay is acknowledged without launching", () => {
            var id = Guid.NewGuid().ToString(); var local = new PendingHandoff(); var expired = Attempt(); expired["expires"] = 1;
            using (var s = new HandoffSession(Guid.NewGuid(), "2868840", id, local)) {
                Check(s.Receive(Event(id, 1, "handoff.ready", expired)) && local.Calls == 0, "Expired ready started/stalled");
                Check(s.Receive(Event(id, 2, "handoff.failed", expired)), "Later event blocked"); } return Task.CompletedTask;
        });
        await Test("target rejects drive-relative UNC and alternate data streams", async () => {
            foreach (var invalid in new[] { "C:folder\\current_run.save", "\\\\server\\share\\current_run.save", "C:\\folder:ads\\current_run.save" })
                await ExpectFailure(() => { new SaveRestoreService(invalid, () => true); return Task.CompletedTask; });
        });
        await Test("manifest rejects path traversal", () => {
            var a = Attempt(); var m = Manifest(a, new byte[] { 1 }); m["files"][0]["path"] = "../current_run.save";
            return ExpectFailure(() => { SaveRestoreService.ValidateManifest(m, a); return Task.CompletedTask; });
        });
        await Test("manifest rejects profile mismatch and expired artifact", async () => {
            var a = Attempt(); var m = Manifest(a, new byte[] { 1 }); m["identity"]["profileId"] = 2;
            await ExpectFailure(() => { SaveRestoreService.ValidateManifest(m, a); return Task.CompletedTask; });
            m = Manifest(a, new byte[] { 1 }); m["expires"] = 1;
            await ExpectFailure(() => { SaveRestoreService.ValidateManifest(m, a); return Task.CompletedTask; });
        });
        await Test("corrupt transfer preserves original save", async () => {
            using (var fixture = new FixtureServer()) { fixture.Corrupt = true; var p = Target(root, "corrupt");
                await ExpectFailure(() => new SaveRestoreService(p, () => true).RestoreAsync(fixture.Api, fixture.Session, fixture.Attempt, CancellationToken.None));
                Check(File.ReadAllText(p) == "original-local", "Corruption changed original"); }
        });
        await Test("apply verifies hash and rollback preserves backup", async () => {
            using (var fixture = new FixtureServer()) { var p = Target(root, "normal");
                using (var receipt = await new SaveRestoreService(p, () => true).RestoreAsync(fixture.Api, fixture.Session, fixture.Attempt, CancellationToken.None)) {
                    Check(File.ReadAllText(p) == "fixture-cloud-run", "Restore wrong data"); receipt.Rollback(); Check(File.ReadAllText(p) == "original-local", "Rollback lost original"); } }
        });
        await Test("active process guard rejects overwrite", async () => {
            using (var fixture = new FixtureServer()) { var p = Target(root, "active");
                await ExpectFailure(() => new SaveRestoreService(p, () => false).RestoreAsync(fixture.Api, fixture.Session, fixture.Attempt, CancellationToken.None)); Check(File.ReadAllText(p) == "original-local", "Active save changed"); }
        });
        await Test("exclusive receipt prevents competing restore", async () => {
            using (var f = new FixtureServer()) { var p = Target(root, "lock"); var service = new SaveRestoreService(p, () => true);
                using (var receipt = await service.RestoreAsync(f.Api, f.Session, f.Attempt, CancellationToken.None)) { await ExpectFailure(() => service.RestoreAsync(f.Api, f.Session, Attempt(), CancellationToken.None)); receipt.Rollback(); } }
        });
        await Test("changed local run prevents destructive rollback", async () => {
            using (var f = new FixtureServer()) { var p = Target(root, "changed");
                using (var receipt = await new SaveRestoreService(p, () => true).RestoreAsync(f.Api, f.Session, f.Attempt, CancellationToken.None)) {
                    File.WriteAllText(p, "new-local-run"); await ExpectFailure(() => { receipt.Rollback(); return Task.CompletedTask; }); Check(File.ReadAllText(p) == "new-local-run", "Changed save lost"); } }
        });
        await Test("commit crash journal reconciles confirmed server state", async () => {
            using (var f = new FixtureServer()) { var p = Target(root, "crash"); var service = new SaveRestoreService(p, () => true);
                using (var receipt = await service.RestoreAsync(f.Api, f.Session, f.Attempt, CancellationToken.None)) receipt.MarkCommitPending();
                f.State = "Committed"; await service.RecoverIncompleteAsync(f.Api, CancellationToken.None); Check(File.ReadAllText(p) == "fixture-cloud-run", "Committed save rolled back"); }
        });
        await Test("unresolved crash journal remains intact", async () => {
            using (var f = new FixtureServer()) { var p = Target(root, "pending"); var service = new SaveRestoreService(p, () => true);
                using (var receipt = await service.RestoreAsync(f.Api, f.Session, f.Attempt, CancellationToken.None)) receipt.MarkCommitPending();
                f.State = "Restored"; await ExpectFailure(() => service.RecoverIncompleteAsync(f.Api, CancellationToken.None)); Check(File.ReadAllText(p) == "fixture-cloud-run", "Unresolved save rolled back"); }
        });
        await Test("IPC rejects echoed token with another actual run", () => {
            var context = new JObject { ["sessionId"] = Guid.NewGuid().ToString(), ["attemptId"] = Guid.NewGuid().ToString(), ["artifactId"] = Guid.NewGuid().ToString(),
                ["generation"] = "gen", ["nonce"] = Guid.NewGuid().ToString("N"), ["identity"] = Identity(), ["expires"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() };
            var result = (JObject)context.DeepClone(); result["version"] = 2; result["resultId"] = Guid.NewGuid().ToString("N"); result["loaded"] = true; result["identity"] = Identity("other:actualrun");
            Check(!GameLoaderPipe.ValidateResult(context, result), "Echoed token replaced actual identity proof"); result["identity"] = Identity(); Check(GameLoaderPipe.ValidateResult(context, result), "Exact actual identity rejected");
            context["expires"] = 1; Check(!GameLoaderPipe.ValidateResult(context, result), "Expired signal accepted"); return Task.CompletedTask;
        });
        await Test("named pipe ACK accepts exact retries and rejects changed result", async () => {
            var attempt = Guid.NewGuid().ToString();
            var context = new JObject { ["sessionId"] = Guid.NewGuid().ToString(), ["attemptId"] = attempt, ["artifactId"] = Guid.NewGuid().ToString(),
                ["generation"] = "gen", ["nonce"] = Guid.NewGuid().ToString("N"), ["identity"] = Identity(), ["expires"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                ["pipeName"] = "HoleInOne.GameLoader." + attempt };
            var result = (JObject)context.DeepClone(); result.Remove("expires"); result.Remove("pipeName"); result["version"] = 2; result["resultId"] = Guid.NewGuid().ToString("N"); result["loaded"] = true;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var receiver = new GameLoaderPipe(context)) {
                var receiving = receiver.ReceiveAsync(cancellation.Token);
                Check(await SendPipe(context, result), "Initial ACK rejected"); var first = await receiving;
                var duplicates = receiver.AcknowledgeDuplicatesAsync(first);
                var changed = (JObject)result.DeepClone(); changed["resultId"] = Guid.NewGuid().ToString("N");
                Check(!await SendPipe(context, changed), "Changed result ACK accepted");
                Check(await SendPipe(context, result), "Exact retry rejected"); Check(await SendPipe(context, result), "Second retry rejected"); await duplicates;
            }
        });
        await Test("native request session and cursor survive journal reopen", () => {
            var token = Environment.GetEnvironmentVariable("HIO_USER_TOKEN"); var url = Environment.GetEnvironmentVariable("HIO_BACKEND_URL");
            Environment.SetEnvironmentVariable("HIO_BACKEND_URL", "http://127.0.0.1:4321/"); Environment.SetEnvironmentVariable("HIO_USER_TOKEN", "fixture-" + Guid.NewGuid());
            try {
                var game = Guid.NewGuid(); var origin = new Uri("http://127.0.0.1:4321/"); string request, session = Guid.NewGuid().ToString();
                using (var journal = new NativeSessionJournal(origin, game)) { request = journal.RequestId; journal.SessionId = session; journal.Cursor = 57; }
                using (var journal = new NativeSessionJournal(origin, game)) { Check(journal.RequestId == request && journal.SessionId == session && journal.Cursor == 57, "Journal did not persist"); journal.Closed = true; }
                using (var journal = new NativeSessionJournal(origin, game)) { Check(journal.RequestId != request && journal.SessionId == null && journal.Cursor == 0, "Closed journal reused session"); journal.Closed = true; }
            } finally { Environment.SetEnvironmentVariable("HIO_USER_TOKEN", token); Environment.SetEnvironmentVariable("HIO_BACKEND_URL", url); } return Task.CompletedTask;
        });
        await Test("fake HTTP restore plus named-pipe load proof commits once", async () => {
            using (var f = new FixtureServer()) { f.State = "ArtifactReady"; var local = new FakeLocalGame { Target = Target(root, "flow") };
                var handoff = new BackendLocalHandoff(f.Api, f.Session, local); Check(await handoff.SwitchAsync(Guid.NewGuid(), f.Attempt, CancellationToken.None), "Fake flow failed"); Check(f.Commits == 1, "Duplicate commit"); }
        });
        await Test("launch failure preserves local save and resumes cloud", async () => {
            using (var f = new FixtureServer()) { f.State = "ArtifactReady"; var local = new FakeLocalGame { Target = Target(root, "launchfail"), LaunchFailure = true };
                Check(!await new BackendLocalHandoff(f.Api, f.Session, local).SwitchAsync(Guid.NewGuid(), f.Attempt, CancellationToken.None), "Injected failure committed");
                Check(f.State == "Failed" && f.Commits == 0 && File.ReadAllText(local.Target) == "original-local", "Failure lost cloud/original"); }
        });
        await Test("different actual run preserves local save and keeps cloud", async () => {
            using (var f = new FixtureServer()) { f.State = "ArtifactReady"; var local = new FakeLocalGame { Target = Target(root, "wrong-actual"), WrongIdentity = true };
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    Check(!await new BackendLocalHandoff(f.Api, f.Session, local).SwitchAsync(Guid.NewGuid(), f.Attempt, timeout.Token), "Different actual run committed");
                Check(f.State == "Failed" && f.Commits == 0 && File.ReadAllText(local.Target) == "original-local", "Mismatch lost original/cloud"); }
        });
        await Test("running local adapter prevents handoff overwrite", async () => {
            using (var f = new FixtureServer()) { f.State = "ArtifactReady"; var local = new FakeLocalGame { Target = Target(root, "running-local"), Running = true };
                Check(!await new BackendLocalHandoff(f.Api, f.Session, local).SwitchAsync(Guid.NewGuid(), f.Attempt, CancellationToken.None), "Running game overwritten");
                Check(f.Commits == 0 && File.ReadAllText(local.Target) == "original-local", "Running game's save changed"); }
        });
        Console.WriteLine("PASS " + count + " contract/fault tests (fake HTTP/game adapter; no actual cloud/game E2E).");
        // Keep per-run journals for diagnosis. Only generated temp data is written by these tests.
    }
    private static async Task<bool> SendPipe(JObject context, JObject result)
    {
        using (var pipe = new NamedPipeClientStream(".", (string)context["pipeName"], PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(2500); var bytes = Encoding.UTF8.GetBytes(result.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            await pipe.WriteAsync(bytes, 0, bytes.Length); await pipe.FlushAsync();
            using (var reader = new StreamReader(pipe)) return (bool)JObject.Parse(await reader.ReadLineAsync())["accepted"];
        }
    }
    private static async Task ProbeConfiguredBackend()
    {
        using (var api = HoleInOneBackendClient.FromEnvironment())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var caps = await api.GetAsync("v2/capabilities", timeout.Token);
            Check((int?)caps["contract"] == 2 && (string)caps["provider"] == "fake" && (bool?)caps["video"] == false,
                "This diagnostic may only mutate a local fake backend");
            var session = await api.PostAsync("v2/sessions", new { gameId = "2868840" }, timeout.Token);
            var id = (string)session["id"]; Check(Guid.TryParse(id, out _), "Session not created");
            var loaded = await api.GetAsync("v2/sessions/" + id, timeout.Token);
            Check((string)loaded["id"] == id && (string)loaded["stream"] == "ACTIVE", "Owned session unavailable");
            var closed = await api.PostAsync("v2/sessions/" + id + "/close", new { }, timeout.Token);
            Check((string)closed["stream"] == "TERMINATED" || (string)closed["stream"] == "TERMINATING", "Fake close request not accepted");
            while ((string)closed["stream"] != "TERMINATED")
            { await Task.Delay(100, timeout.Token); closed = await api.GetAsync("v2/sessions/" + id, timeout.Token); }
            Console.WriteLine("PASS installed encrypted native configuration -> real backend HTTP auth/create/read/close (fake only; no video/AWS).");
        }
    }
    private static int Main(string[] args) { try { (args.Length == 1 && args[0] == "--probe-configured-backend" ? ProbeConfiguredBackend() : Run()).GetAwaiter().GetResult(); return 0; } catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; } }
}
