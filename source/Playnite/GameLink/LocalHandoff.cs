using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    public interface ILocalHandoff
    {
        Task<bool> SwitchAsync(Guid gameId, JObject attempt, CancellationToken token);
    }
    public interface ILocalGameAdapter
    {
        Task WaitForInstallationAsync(CancellationToken token);
        SaveRestoreService CreateRestoreService(JObject manifestIdentity);
        void ValidateCompatibility();
        Task LaunchAsync(string contextPath, CancellationToken token);
    }

    // Explicit development attestation until native Steam/Godot path + synchronization policy can be verified.
    // Production defaults never guess an AppData account/profile path or assume Steam Cloud is disabled.
    internal sealed class VerifiedLocalGameAdapter : ILocalGameAdapter
    {
        private readonly Func<Game> game;
        private readonly Func<Game, bool> installLoader;
        private readonly string configPath;
        public VerifiedLocalGameAdapter(Func<Game> game, Func<Game, bool> installLoader)
        {
            this.game = game; this.installLoader = installLoader;
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoleInOne", "local-save-adapter.json");
        }
        public async Task WaitForInstallationAsync(CancellationToken token)
        {
            while (game()?.IsInstalled != true) await Task.Delay(500, token).ConfigureAwait(false);
            ValidateCompatibility();
        }
        private bool Safe()
        {
            var processes = Process.GetProcessesByName("SlayTheSpire2");
            try { return game()?.IsRunning != true && game()?.IsLaunching != true && processes.Length == 0 &&
                Environment.GetEnvironmentVariable("HIO_ALLOW_ATTESTED_LOCAL_ADAPTER") == "1" &&
                File.Exists(configPath) && (bool?)JObject.Parse(File.ReadAllText(configPath))["steamCloudDisabled"] == true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        public void ValidateCompatibility()
        {
            var g = game();
            if (g?.IsInstalled != true || g.GameId != "2868840" || string.IsNullOrWhiteSpace(g.InstallDirectory) || !Safe())
                throw new InvalidOperationException("Local installation/process/verified Steam Cloud adapter is unavailable.");
            var config = JObject.Parse(File.ReadAllText(configPath));
            if ((string)config["adapter"] != "development-attested" ||
                !string.Equals(Path.GetFullPath((string)config["installDirectory"] ?? ""), Path.GetFullPath(g.InstallDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Local installation attestation mismatch.");
            var exe = Path.Combine(g.InstallDirectory, "SlayTheSpire2.exe");
            var dll = Path.Combine(g.InstallDirectory, "data_sts2_windows_x86_64", "sts2.dll");
            if (!File.Exists(exe) || !File.Exists(dll)) throw new FileNotFoundException("Supported game binary is unavailable.");
            // Reflection-only metadata inspection never executes game code or loads native game dependencies.
            if (Assembly.ReflectionOnlyLoad(File.ReadAllBytes(dll)).ManifestModule.ModuleVersionId.ToString() != SaveRestoreService.SupportedBuild)
                throw new InvalidOperationException("Installed game build is unsupported.");
            var mods = Path.Combine(g.InstallDirectory, "mods");
            if (Directory.Exists(mods))
                foreach (var directory in Directory.GetDirectories(mods))
                    if (!string.Equals(Path.GetFileName(directory), "GameLoader", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Gameplay mod compatibility has not been verified.");
            if (!installLoader(g)) throw new InvalidOperationException("GameLoader installation failed.");
        }
        public SaveRestoreService CreateRestoreService(JObject identity)
        {
            ValidateCompatibility(); var config = JObject.Parse(File.ReadAllText(configPath));
            if ((int?)config["profileId"] != (int?)identity["profileId"] || string.IsNullOrWhiteSpace((string)config["steamAccountId"]))
                throw new InvalidOperationException("The verified local account/profile differs from the artifact.");
            var target = (string)config["resolvedRunSavePath"];
            if ((bool?)config["isRunningModded"] != true) throw new InvalidOperationException("GameLoader uses the modded save scope; unmodded restoration is rejected.");
            var suffix = Path.Combine("steam", (string)config["steamAccountId"], "modded", "profile" + (int)identity["profileId"], "saves", "current_run.save");
            if (string.IsNullOrEmpty(target) || !target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The game-resolved modded save path attestation is invalid.");
            return new SaveRestoreService(target, Safe);
        }
        public Task LaunchAsync(string contextPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ValidateCompatibility(); var g = game();
            var start = new ProcessStartInfo(Path.Combine(g.InstallDirectory, "SlayTheSpire2.exe"))
            { WorkingDirectory = g.InstallDirectory, UseShellExecute = false };
            start.EnvironmentVariables["HIO_HANDOFF_CONTEXT"] = contextPath;
            // Direct child receives immutable context; a pre-existing Steam process cannot inherit it.
            var process = Process.Start(start); if (process == null) throw new InvalidOperationException("Game launch failed.");
            process.Dispose(); return Task.CompletedTask;
        }
    }

    public sealed class GameLoaderPipe : IDisposable
    {
        private readonly JObject context;
        private NamedPipeServerStream pipe;
        public GameLoaderPipe(JObject context) { this.context = context; }
        public static bool ValidateResult(JObject c, JObject result)
        {
            return (int?)result["version"] == 2 && Guid.TryParse((string)result["resultId"], out _) &&
                (string)result["sessionId"] == (string)c["sessionId"] && (string)result["attemptId"] == (string)c["attemptId"] &&
                (string)result["artifactId"] == (string)c["artifactId"] && (string)result["generation"] == (string)c["generation"] &&
                (string)result["nonce"] == (string)c["nonce"] && (long?)c["expires"] > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
                result["loaded"]?.Type == JTokenType.Boolean && ((bool)result["loaded"] == false || SaveRestoreService.SameIdentity(c["identity"], result["identity"]));
        }
        public async Task<JObject> ReceiveAsync(CancellationToken token, JObject expectedReplay = null)
        {
            var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            for (var rejected = 0; rejected < 4; rejected++)
            {
                pipe = new NamedPipeServerStream((string)context["pipeName"], PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security);
                using (token.Register(() => pipe?.Dispose()))
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var bytes = new MemoryStream(); var one = new byte[1];
                    while (bytes.Length <= 16384)
                    {
                        var n = await pipe.ReadAsync(one, 0, 1, token).ConfigureAwait(false);
                        if (n == 0 || one[0] == 10) break; bytes.WriteByte(one[0]);
                    }
                    JObject result = null;
                    try { if (bytes.Length <= 16384) result = JObject.Parse(Encoding.UTF8.GetString(bytes.ToArray())); } catch (JsonException) { }
                    var accepted = result != null && ValidateResult(context, result) &&
                        (expectedReplay == null || JToken.DeepEquals(expectedReplay, result));
                    var ack = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { version = 2, resultId = (string)result?["resultId"], accepted }) + "\n");
                    await pipe.WriteAsync(ack, 0, ack.Length, token).ConfigureAwait(false); await pipe.FlushAsync(token).ConfigureAwait(false);
                    if (accepted) return result;
                }
            }
            throw new InvalidDataException("GameLoader IPC identity verification failed.");
        }
        public async Task AcknowledgeDuplicatesAsync(JObject original)
        {
            // Retain the attempt receiver for the sender's three two-second ACK attempts.
            using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
            {
                try
                {
                    for (var i = 0; i < 2; i++)
                    {
                        await ReceiveAsync(grace.Token, original).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
            }
        }
        public void Dispose() => pipe?.Dispose();
    }

    internal sealed class BackendLocalHandoff : ILocalHandoff
    {
        private readonly HoleInOneBackendClient api; private readonly string session; private readonly ILocalGameAdapter local;
        public BackendLocalHandoff(HoleInOneBackendClient api, string session, ILocalGameAdapter local)
        { this.api = api; this.session = session; this.local = local; }
        private string Route(string operation) => "v2/sessions/" + session + "/" + operation;
        private static string NewNonce()
        { var bytes = new byte[16]; using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private async Task<JObject> CurrentAsync(JObject attempt, string expected, CancellationToken token)
        {
            var state = await api.GetAsync(Route(""), token).ConfigureAwait(false); var current = state["attempt"] as JObject;
            if (current == null || (string)current["id"] != (string)attempt["id"] ||
                (string)current["artifactId"] != (string)attempt["artifactId"] || (string)current["generation"] != (string)attempt["generation"] ||
                !SaveRestoreService.SameIdentity(current["identity"], attempt["identity"]) || (string)state["state"] != expected ||
                expected != "ArtifactReady" && (long?)current["lease"] <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new InvalidOperationException("The attempt changed, expired, or was cancelled.");
            return current;
        }
        public async Task<bool> SwitchAsync(Guid gameId, JObject attempt, CancellationToken token)
        {
            SaveRestoreService.RestoreReceipt receipt = null; string contextPath = null; bool commitConfirmed = false; bool commitRequested = false; bool serverStateKnown = false;
            var id = (string)attempt["id"];
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var expires = (long)attempt["expires"];
                bounded.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, expires - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
                token = bounded.Token;
                try
                {
                    await local.WaitForInstallationAsync(token).ConfigureAwait(false);
                    await api.PostAsync(Route("installation"), new { ready = true }, token).ConfigureAwait(false);
                    await CurrentAsync(attempt, "ArtifactReady", token).ConfigureAwait(false);
                    await api.PostAsync(Route("prepare"), new { attemptId = id }, token, "prepare-" + id).ConfigureAwait(false);
                    JObject frozen;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var state = await api.GetAsync(Route(""), token).ConfigureAwait(false);
                        if ((string)state["state"] == "Frozen") { frozen = await CurrentAsync(attempt, "Frozen", token).ConfigureAwait(false); break; }
                        if ((string)state["state"] != "Preparing" || (string)state["attempt"]?["id"] != id)
                            throw new InvalidOperationException("Cloud freeze failed or was cancelled.");
                        if ((long?)state["attempt"]?["lease"] <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new TimeoutException("Freeze lease expired.");
                        await Task.Delay(400, token).ConfigureAwait(false);
                    }
                    bounded.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (long)frozen["lease"] - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
                    local.ValidateCompatibility();
                    var restore = local.CreateRestoreService((JObject)frozen["identity"]);
                    receipt = await restore.RestoreAsync(api, session, frozen, token).ConfigureAwait(false);
                    await CurrentAsync(frozen, "Frozen", token).ConfigureAwait(false);
                    var sha = (string)receipt.Manifest["files"][0]["sha256"];
                    await api.PostAsync(Route("restored"), new { attemptId = id, artifactId = (string)frozen["artifactId"], sha256 = sha }, token, "restore-" + id).ConfigureAwait(false);
                    var context = new JObject { ["version"] = 2, ["sessionId"] = session, ["attemptId"] = id,
                        ["artifactId"] = frozen["artifactId"], ["gameId"] = "2868840", ["build"] = SaveRestoreService.SupportedBuild,
                        ["identity"] = frozen["identity"].DeepClone(), ["generation"] = frozen["generation"], ["sha256"] = sha,
                        ["expires"] = Math.Min((long)frozen["expires"], (long)frozen["lease"]), ["nonce"] = NewNonce(),
                        ["pipeName"] = "HoleInOne.GameLoader." + id };
                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoleInOne", "handoff");
                    Directory.CreateDirectory(folder); if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Context folder is a link.");
                    var directorySecurity = new DirectorySecurity(); directorySecurity.SetAccessRuleProtection(true, false);
                    directorySecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    Directory.SetAccessControl(folder, directorySecurity);
                    contextPath = Path.Combine(folder, id + ".json");
                    var contextTemp = contextPath + ".tmp"; File.WriteAllText(contextTemp, context.ToString(Formatting.None)); File.Move(contextTemp, contextPath);
                    using (var ipc = new GameLoaderPipe(context))
                    {
                        var resultTask = ipc.ReceiveAsync(token);
                        await CurrentAsync(frozen, "Restored", token).ConfigureAwait(false);
                        await local.LaunchAsync(contextPath, token).ConfigureAwait(false);
                        var result = await resultTask.ConfigureAwait(false);
                        var acknowledgements = ipc.AcknowledgeDuplicatesAsync(result);
                        if ((bool)result["loaded"] != true) throw new InvalidOperationException("GameLoader did not verify the restored run.");
                        await CurrentAsync(frozen, "Restored", token).ConfigureAwait(false);
                        receipt.MarkCommitPending();
                        commitRequested = true;
                        var committed = await api.PostAsync(Route("commit"), new { attemptId = id, artifactId = (string)frozen["artifactId"],
                            generation = (string)frozen["generation"], loaded = true, observed = result["identity"] }, token, "commit-" + id).ConfigureAwait(false);
                        commitConfirmed = (string)committed["state"] == "Committed";
                        if (!commitConfirmed) throw new IOException("Backend commit was not confirmed.");
                        receipt.Commit(); await acknowledgements.ConfigureAwait(false); return true;
                    }
                }
                catch
                {
                    // Resolve a lost commit response before rollback/fail. The server owns cloud termination.
                    try
                    {
                        using (var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        {
                            var state = await api.GetAsync(Route(""), recovery.Token).ConfigureAwait(false);
                            commitConfirmed = (string)state["state"] == "Committed" && (string)state["attempt"]?["id"] == id;
                            if (commitConfirmed) { receipt?.Commit(); return true; }
                            await api.PostAsync(Route("fail"), new { attemptId = id, reason = "local_handoff_failed" }, recovery.Token, "fail-" + id).ConfigureAwait(false);
                            serverStateKnown = true; // A successful fail transition excludes a committed server outcome.
                        }
                    }
                    catch { }
                    if (!commitRequested || serverStateKnown)
                        try { receipt?.Rollback(); } catch { /* Journal and verified backup remain for recovery; never overwrite an active/changed run. */ }
                    return false;
                }
                finally { receipt?.Dispose(); if (contextPath != null && File.Exists(contextPath)) File.Delete(contextPath); }
            }
        }
    }
}
