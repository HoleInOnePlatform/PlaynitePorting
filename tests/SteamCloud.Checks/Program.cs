using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoleInOne.SteamCloud;

const uint app = 2868840;
const ulong user = 76561197960265728UL + 123;
var checks = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
async Task Reject(Func<Task> operation, string code, string name)
{
    try { await operation(); throw new Exception("Expected rejection: " + name); }
    catch (CloudFailure e) { Check(e.Code == code, name); }
}
static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
static CloudSnapshot Snapshot(params (string name, byte[] bytes)[] values) => new(1,
    values.ToDictionary(v => v.name, v => new RemoteFile(v.name, Hash(v.bytes), v.bytes.Length), StringComparer.OrdinalIgnoreCase));
static SaveFile Save(string content) => new("steamUserDataRoot", "{steamAccountId}/2868840/remote/profile1/saves/current_run.save", Bytes(content));
const string name = "profile1/saves/current_run.save";
var empty = Snapshot();
var before = Snapshot((name, Bytes("old")));
var desired = Save("new");
var initialPlan = CloudPlanner.Create([desired], before, before, [], app, user);
Check(initialPlan.Files.Single().Name == name && initialPlan.LocalOnly == 0, "Steam userdata token mapping");
var secondGame = new SaveFile("steamUserDataRoot", "{steamAccountId}/377160/remote/progress.save", Bytes("save"));
Check(CloudPlanner.Create([secondGame], empty, empty, [], 377160, user).Files.Single().Name == "progress.save", "same planner supports another app");
await Reject(() => Task.Run(() => CloudPlanner.Create([desired], before, Snapshot((name, Bytes("other device"))), [], app, user)), "remote_changed", "concurrent remote edit is protected");
await Reject(() => Task.Run(() => CloudPlanner.Create([desired], before, empty, [], app, user)), "remote_changed", "concurrent remote deletion is protected");
Check(CloudPlanner.Create([desired], before, Snapshot((name, desired.Bytes)), [], app, user).Files.Count == 0, "retry after successful upload is idempotent");
await Reject(() => Task.Run(() => CloudPlanner.Create([desired with { Path = "999/2868840/remote/save" }], empty, empty, [], app, user)), "account_or_app_mismatch", "foreign account blocked");
await Reject(() => Task.Run(() => CloudPlanner.Create([secondGame], empty, empty, [], app, user)), "account_or_app_mismatch", "foreign game blocked");
await Reject(() => Task.Run(() => CloudPlanner.Create([desired with { Path = "{steamAccountId}/2868840/remote/../outside" }], empty, empty, [], app, user)), "unsafe_path", "traversal rejected");
await Reject(() => Task.Run(() => CloudPlanner.Create([desired with { Path = "{steamAccountId}/2868840/remote/remotecache.vdf" }], empty, empty, [], app, user)), "steam_metadata_in_bundle", "Steam metadata never uploaded");
await Reject(() => Task.Run(() => CloudPlanner.Create([desired, new("steamUserData", "2868840/remote/" + name, Bytes("different"))], empty, empty, [], app, user)), "ambiguous_cloud_path", "conflicting duplicate destinations rejected");
var rules = new CloudRule[] { new("WinAppDataRoaming", "Example/{64BitSteamID}", "*.sav", false) };
var auto = new SaveFile("roaming", "Example/{steamId64}/slot.sav", Bytes("slot"));
var autoPlan = CloudPlanner.Create([auto, auto with { Path = "Example/{steamId64}/preferences.json" }], empty, empty, rules, app, user);
Check(autoPlan.Files.Single().Name == "%WinAppDataRoaming%Example/" + user + "/slot.sav" && autoPlan.LocalOnly == 1, "AutoCloud rules map new saves but exclude local preferences");
var nested = auto with { Path = "Example/{steamId64}/nested/slot.sav" };
await Reject(() => Task.Run(() => CloudPlanner.Create([nested], empty, empty, rules, app, user)), "no_cloud_mapping", "nonrecursive rule does not upload nested files");
Check(CloudPlanner.Create([nested], empty, empty, [rules[0] with { Recursive = true }], app, user).Files.Count == 1, "recursive AutoCloud rule supported");
var rooted = "%WinAppDataRoaming%/Example/" + user + "/slot.sav";
Check(CloudPlanner.Create([auto], Snapshot((rooted, Bytes("old"))), Snapshot((rooted, Bytes("old"))), [], app, user).Files.Single().Name == rooted, "existing Steam filename is preserved exactly");
var fake = new FakeCloud(before);
await new CloudSyncEngine(fake).SyncAsync([desired], before, [], app, user, true, default);
Check(fake.Calls.SequenceEqual(["snapshot"]), "preview never opens batch or sends launch signals");
fake = new(before);
await new CloudSyncEngine(fake).SyncAsync([desired], before, [], app, user, false, default);
Check(fake.Calls.SequenceEqual(["snapshot", "begin", "upload", "complete:True", "snapshot", "ready"]), "successful upload verifies before launch check");
fake = new(before) { FailUpload = true };
await Reject(() => new CloudSyncEngine(fake).SyncAsync([desired], before, [], app, user, false, default), "test_upload_failed", "upload failure propagated");
Check(fake.Calls.Contains("complete:False") && !fake.Calls.Contains("ready"), "failed batch closed without enabling launch");
fake = new(before) { CancelUpload = true };
try { await new CloudSyncEngine(fake).SyncAsync([desired], before, [], app, user, false, default); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { Check(fake.Calls.Contains("complete:False"), "cancelled upload closes batch with fresh cleanup token"); }
fake = new(before) { CorruptAfterUpload = true };
await Reject(() => new CloudSyncEngine(fake).SyncAsync([desired], before, [], app, user, false, default), "upload_verification_failed", "server hash mismatch blocks launch");
Check(!fake.Calls.Contains("ready"), "launch check not reached after failed verification");
fake = new(before) { Pending = true };
await Reject(() => new CloudSyncEngine(fake).SyncAsync([desired], before, [], app, user, false, default), "remote_operation_pending", "pending foreign session blocks success");
Check(fake.Files[name].Sha1 == desired.Sha1, "uploaded copy retained for safe retry after pending session");
var preparationConnection = new FakeCloud(before);
var disconnectedConnection = new FakeCloud(before) { Disconnected = true };
var recoveredConnection = new FakeCloud(before);
var editedConnection = new FakeCloud(Snapshot((name, Bytes("other device"))));
var connections = new Queue<FakeCloud>([preparationConnection, disconnectedConnection, recoveredConnection, editedConnection]);
var failLogin = false;
Task<ICloudSessionTransport> Connect(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    if (failLogin) throw new CloudFailure("steam_disconnected");
    return Task.FromResult<ICloudSessionTransport>(connections.Dequeue());
}
var prepared = await PreparedCloudUpload.PrepareAsync(Connect, app, user, default);
Check(preparationConnection.Disposed && prepared.BaselineFiles == 1,
    "preparation releases Steam connection before gameplay while retaining baseline");
await Reject(() => prepared.SyncAsync([desired], false, default), "steam_disconnected",
    "disconnect during a transfer remains a retryable operation failure");
Check(disconnectedConnection.Disposed, "failed transfer releases disconnected connection");
failLogin = true;
await Reject(() => prepared.SyncAsync([desired], false, default), "steam_disconnected",
    "failed reconnect leaves prepared upload available for another retry");
failLogin = false;
await prepared.SyncAsync([desired], false, default);
Check(recoveredConnection.Disposed && recoveredConnection.Files[name].Sha1 == desired.Sha1,
    "retry uses a fresh connection and completes upload");
await Reject(() => prepared.SyncAsync([desired], false, default), "remote_changed",
    "reconnection preserves original baseline and rejects another device's changes");
Check(editedConnection.Disposed && !editedConnection.Calls.Contains("begin") && connections.Count == 0,
    "conflict closes fresh connection without uploading");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await prepared.SyncAsync([desired], false, cancelled.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(connections.Count == 0, "cancelled retry does not open another connection"); }
}
Check(!SteamCloudTransport.IsStorageHost(new Uri("https://127.0.0.1/upload")) && !SteamCloudTransport.IsStorageHost(new Uri("https://example.com")) &&
    !SteamCloudTransport.IsStorageHost(new Uri("http://steamcloud.blob.core.windows.net/file")), "untrusted upload endpoints rejected");
Check(SteamCloudTransport.IsStorageHost(new Uri("https://steamcloudseoul2.blob.core.windows.net/file")), "Steam storage endpoint accepted");
var testParent = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
var testRoot = Path.Combine(testParent, "hio-steam-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    async Task<(string path, string hash)> Bundle(bool badHash = false)
    {
        var path = Path.Combine(testRoot, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("files/000000").Open()) output.Write(desired.Bytes);
            using var metadata = zip.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(metadata, new { Version = 1, GameId = "steam-" + app,
                Files = new[] { new { Entry = "files/000000", desired.Root, desired.Path, Size = desired.Bytes.Length,
                    Sha256 = badHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(desired.Bytes)) } } });
        }
        return (path, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
    }
    var bundle = await Bundle();
    Check((await SaveBundle.ReadAsync(bundle.path, bundle.hash, app, default)).Single().Bytes.SequenceEqual(desired.Bytes), "bundle verified without modifying live files");
    await Reject(() => SaveBundle.ReadAsync(bundle.path, new string('0', 64), app, default), "bundle_hash_mismatch", "archive checksum enforced");
    await Reject(() => SaveBundle.ReadAsync(bundle.path, bundle.hash, 377160, default), "invalid_bundle", "bundle app ID enforced");
    var corrupt = await Bundle(true);
    await Reject(() => SaveBundle.ReadAsync(corrupt.path, corrupt.hash, app, default), "bundle_hash_mismatch", "individual checksum enforced");
    var token = Path.Combine(testRoot, "test-login.dat");
    var credential = new SavedLogin("synthetic-user", "synthetic-token-not-a-real-secret", user);
    LoginStore.Save(token, credential);
    Check(LoginStore.Load(token) == credential && !Encoding.UTF8.GetString(File.ReadAllBytes(token)).Contains(credential.RefreshToken), "login token encrypted with Windows DPAPI");
    LoginStore.Save(token, credential with { RefreshToken = "synthetic-refreshed-token" });
    Check(LoginStore.Load(token).RefreshToken == "synthetic-refreshed-token", "encrypted login can be replaced after reconnect");
}
finally
{
    var resolved = Path.GetFullPath(testRoot);
    if (Path.GetDirectoryName(resolved) != testParent || !Path.GetFileName(resolved).StartsWith("hio-steam-checks-", StringComparison.Ordinal))
        throw new Exception("Unexpected test cleanup path");
    Directory.Delete(resolved, true);
}
Console.WriteLine($"{checks} Steam Cloud checks passed. No live Steam requests were made.");
if (args.Length == 1)
{
    foreach (var input in new[] { "{\"operation\":\"invalid\"}", "{\"operation\":\"cancel\"}", "invalid-json" })
    {
        using var process = new System.Diagnostics.Process { StartInfo = new(args[0])
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        try
        {
            await process.StandardInput.WriteLineAsync(input);
            await process.StandardInput.FlushAsync();
            var response = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(response != null && JsonDocument.Parse(response).RootElement.GetProperty("type").GetString() == "error", "worker rejects invalid/cancelled request without login");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.ExitCode == 1, "worker exits without waiting for parent to close stdin");
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
    Console.WriteLine($"{checks} checks including packaged worker IPC passed.");
}

sealed class FakeCloud(CloudSnapshot initial) : ICloudSessionTransport
{
    public readonly Dictionary<string, RemoteFile> Files = new(initial.Files, StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = [];
    public bool FailUpload, CancelUpload, CorruptAfterUpload, Pending, Disconnected, Disposed;
    public Task<IReadOnlyList<CloudRule>> RulesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CloudRule>>([]);
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    public Task<CloudSnapshot> SnapshotAsync(CancellationToken ct)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeCloud));
        Calls.Add("snapshot");
        if (Disconnected) throw new CloudFailure("steam_disconnected");
        return Task.FromResult(new CloudSnapshot(1, new Dictionary<string, RemoteFile>(Files)));
    }
    public Task<ulong> BeginAsync(IReadOnlyList<UploadFile> files, ulong expectedChange, CancellationToken ct) { Calls.Add("begin"); return Task.FromResult(42UL); }
    public Task UploadAsync(ulong batch, UploadFile file, CancellationToken ct)
    {
        Calls.Add("upload");
        if (FailUpload) throw new CloudFailure("test_upload_failed");
        if (CancelUpload) throw new OperationCanceledException();
        if (!CorruptAfterUpload) Files[file.Name] = new(file.Name, file.Sha1, file.Bytes.Length);
        return Task.CompletedTask;
    }
    public Task CompleteAsync(ulong batch, bool success, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Calls.Add("complete:" + success); return Task.CompletedTask; }
    public Task EnsureLaunchReadyAsync(CancellationToken ct) { Calls.Add("ready"); if (Pending) throw new CloudFailure("remote_operation_pending"); return Task.CompletedTask; }
}
