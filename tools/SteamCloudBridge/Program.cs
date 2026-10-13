using Playnite.HoleInOne;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using HoleInOne.SteamCloud;

// Private newline-delimited JSON pipe. No listener, token/URL logging, or command-line credentials.
var outputGate = new object();
void Emit(object value) { lock (outputGate) Console.WriteLine(JsonSerializer.Serialize(value)); }
var timing = new PerformanceTrace("steam_worker", Console.Error.WriteLine);
using var lifetime = new CancellationTokenSource();
var queue = Channel.CreateUnbounded<BridgeCommand>(new() { SingleReader = true, SingleWriter = true });
var reader = Task.Run(async () =>
{
    try
    {
        while (!lifetime.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(lifetime.Token);
            if (line == null) break;
            if (line.Length > 32768) throw new CloudFailure("invalid_request");
            var command = JsonSerializer.Deserialize<BridgeCommand>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new CloudFailure("invalid_request");
            if (command.Operation is "cancel" or "quit") { lifetime.Cancel(); break; }
            await queue.Writer.WriteAsync(command, lifetime.Token);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception) { Emit(new { type = "error", code = "invalid_request" }); }
    finally { queue.Writer.TryComplete(); lifetime.Cancel(); }
});
try
{
    var start = await queue.Reader.ReadAsync(lifetime.Token);
    if (start.Operation is not ("auth" or "session") || !ulong.TryParse(start.SteamId, out var steamId) ||
        steamId <= 76561197960265728UL || steamId > 76561197960265728UL + uint.MaxValue ||
        string.IsNullOrWhiteSpace(start.TokenPath) || !Path.IsPathFullyQualified(start.TokenPath)) throw new CloudFailure("invalid_request");
    Directory.CreateDirectory(Path.GetDirectoryName(start.TokenPath)!);
    using var accountLock = new FileStream(start.TokenPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await using var connection = new SteamConnection();
    using (var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
    {
        loginTimeout.CancelAfter(TimeSpan.FromSeconds(start.Operation == "auth" ? 180 : 45));
        await timing.RunAsync("steam_login", () => connection.LoginAsync(start.TokenPath, steamId, start.Operation == "auth", Emit, loginTimeout.Token));
    }
    if (start.Operation == "auth") Emit(new { type = "authenticated", steamId = steamId.ToString() });
    else
    {
        if (start.AppId == 0) throw new CloudFailure("invalid_request");
        using var transport = new SteamCloudTransport(connection, start.AppId);
        IReadOnlyList<CloudRule> rules;
        CloudSnapshot baseline;
        using (var prepare = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            prepare.CancelAfter(TimeSpan.FromSeconds(45));
            (rules, baseline) = await HoleInOne.SteamCloud.CloudPreparation.LoadAsync(
                ct => timing.RunAsync("cloud_rules", () => transport.RulesAsync(ct)),
                ct => timing.RunAsync("baseline_snapshot", () => transport.SnapshotAsync(ct)), prepare.Token);
        }
        Emit(new { type = "ready", steamId = steamId.ToString(), appId = start.AppId, baselineFiles = baseline.Files.Count });
        await foreach (var command in queue.Reader.ReadAllAsync(lifetime.Token))
        {
            try
            {
                if (command.Operation is not ("preview" or "sync") || string.IsNullOrWhiteSpace(command.ArchivePath) ||
                    !Path.IsPathFullyQualified(command.ArchivePath) || command.Sha256 == null)
                    throw new CloudFailure("invalid_request");
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                operation.CancelAfter(TimeSpan.FromSeconds(120));
                var watch = Stopwatch.StartNew();
                var files = await timing.RunAsync("read_save_bundle", () => SaveBundle.ReadAsync(command.ArchivePath, command.Sha256, start.AppId, operation.Token));
                var plan = await new CloudSyncEngine(transport, timing).SyncAsync(files, baseline, rules, start.AppId, steamId, command.Operation == "preview", operation.Token);
                Emit(new { type = "complete", preview = command.Operation == "preview", uploaded = plan.Files.Count, unchanged = plan.Unchanged,
                    localOnly = plan.LocalOnly, bytes = plan.Files.Sum(f => (long)f.Bytes.Length), elapsedMs = watch.ElapsedMilliseconds });
            }
            catch (Exception error) { Emit(new { type = "error", code = ErrorCode(error) }); }
        }
    }
}
catch (Exception error) { Emit(new { type = "error", code = ErrorCode(error) }); Environment.ExitCode = 1; }
// Console input on Windows may ignore cancellation while the parent keeps its pipe open.
// The reader is a background task: never hold the account lock/process open waiting for it.
finally { lifetime.Cancel(); }

static string ErrorCode(Exception error) => error switch
{
    CloudFailure failure => failure.Code,
    OperationCanceledException or TimeoutException => "operation_timeout",
    IOException => "local_io_failed",
    UnauthorizedAccessException => "local_access_failed",
    _ => "steam_sync_failed"
};
public sealed record BridgeCommand(string Operation, string? SteamId = null, uint AppId = 0, string? TokenPath = null,
    string? ArchivePath = null, string? Sha256 = null);
