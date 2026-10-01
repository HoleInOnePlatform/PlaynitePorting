using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    // Only one game-resolved run file is supported. No server-provided destination or archive extraction.
    public sealed class SaveRestoreService
    {
        public const string SupportedBuild = "97f10687-c306-4798-ab75-8b9f23f34dfb";
        private readonly Func<bool> safeToWrite;
        public string Target { get; }
        private string Workspace => Path.Combine(Path.GetDirectoryName(Target), ".holeinone-restore");
        public SaveRestoreService(string target, Func<bool> safeToWrite)
        {
            if (string.IsNullOrWhiteSpace(target) || !System.Text.RegularExpressions.Regex.IsMatch(target, "^[A-Za-z]:[\\\\/]") ||
                target.Substring(2).Contains(":") || Path.GetFileName(target) != "current_run.save")
                throw new ArgumentException("A verified absolute game save target is required.");
            Target = Path.GetFullPath(target);
            this.safeToWrite = safeToWrite ?? throw new ArgumentNullException(nameof(safeToWrite));
        }
        public static bool SameIdentity(JToken a, JToken b) => a != null && b != null &&
            (int?)a["profileId"] == (int?)b["profileId"] && (string)a["runId"] == (string)b["runId"] &&
            (string)a["checkpoint"] == (string)b["checkpoint"];
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static void ValidateManifest(JObject m, JObject a)
        {
            var files = m["files"] as JArray; var f = files?.Count == 1 ? files[0] : null;
            var id = m["identity"]; var profile = (int?)id?["profileId"];
            if ((int?)m["version"] != 2 || (string)m["gameId"] != "2868840" || (string)m["build"] != SupportedBuild ||
                (string)m["artifactId"] != (string)a["artifactId"] || (string)m["generation"] != (string)a["generation"] ||
                !SameIdentity(id, a["identity"]) || profile < 1 || profile > 3 || profile == null ||
                string.IsNullOrEmpty((string)id?["runId"]) || string.IsNullOrEmpty((string)id?["checkpoint"]) ||
                (string)f?["path"] != "current_run.save" || f?["size"]?.Type != JTokenType.Integer || (long?)f?["size"] <= 0 || (long?)f?["size"] > 16 * 1024 * 1024 ||
                !System.Text.RegularExpressions.Regex.IsMatch((string)f?["sha256"] ?? "", "^[a-f0-9]{64}$") ||
                m["expires"]?.Type != JTokenType.Integer || (long?)m["expires"] <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new InvalidDataException("Artifact manifest is incompatible or expired.");
            if (!(m["mods"] is JArray mods) || mods.Count > 32)
                throw new InvalidDataException("Modded save compatibility has not been verified.");
            foreach (var mod in mods)
                if ((string)mod != "GameLinkPoint@v0.4.0" && (string)mod != "GameLoader@v0.5.0")
                    throw new InvalidDataException("Gameplay mod compatibility has not been verified.");
        }
        private void CheckSafe()
        {
            if (!safeToWrite()) throw new InvalidOperationException("Game/process/Steam Cloud safety check failed; save is unchanged.");
            var directory = new DirectoryInfo(Path.GetDirectoryName(Target));
            for (var current = directory; current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Save path contains a link.");
            if (File.Exists(Target) && (File.GetAttributes(Target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Save target is a link.");
            if (Directory.Exists(Workspace) && (File.GetAttributes(Workspace) & FileAttributes.ReparsePoint) != 0) throw new IOException("Restore workspace is a link.");
        }
        private static void DurableWrite(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        private void Journal(string directory, JObject value)
        {
            var path = Path.Combine(directory, "journal.json"); var temp = path + ".tmp";
            DurableWrite(temp, Encoding.UTF8.GetBytes(value.ToString(Formatting.None)));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public async Task<RestoreReceipt> RestoreAsync(HoleInOneBackendClient api, string session, JObject attempt, CancellationToken token)
        {
            CheckSafe(); Directory.CreateDirectory(Path.GetDirectoryName(Target)); Directory.CreateDirectory(Workspace);
            CheckSafe(); var lockPath = Path.Combine(Workspace, "restore.lock");
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Restore lock is a link.");
            var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { var receipt = await RestoreCoreAsync(api, session, attempt, token).ConfigureAwait(false); receipt.Ownership = ownership; return receipt; }
            catch { ownership.Dispose(); throw; }
        }
        private async Task<RestoreReceipt> RestoreCoreAsync(HoleInOneBackendClient api, string session, JObject attempt, CancellationToken token)
        {
            CheckSafe(); await RecoverIncompleteAsync(api, token).ConfigureAwait(false);
            var artifact = (string)attempt["artifactId"];
            if (!Guid.TryParse(artifact, out _) || !Guid.TryParse((string)attempt["id"], out _)) throw new InvalidDataException("Invalid attempt identity.");
            var path = "v2/sessions/" + session + "/artifacts/" + artifact;
            var m = await api.GetAsync(path, token).ConfigureAwait(false); ValidateManifest(m, attempt);
            var bytes = await api.DownloadAsync(path + "/file", (long)m["files"][0]["size"], token).ConfigureAwait(false);
            var hash = (string)m["files"][0]["sha256"];
            if (bytes.LongLength != (long)m["files"][0]["size"] || Hash(bytes) != hash) throw new InvalidDataException("Downloaded save is corrupt.");
            token.ThrowIfCancellationRequested(); CheckSafe();
            var drive = new DriveInfo(Path.GetPathRoot(Target));
            var required = bytes.LongLength * 3 + (File.Exists(Target) ? new FileInfo(Target).Length : 0) + 1024 * 1024;
            if (drive.AvailableFreeSpace < required) throw new IOException("Insufficient space to preserve existing save.");
            Directory.CreateDirectory(Path.GetDirectoryName(Target)); Directory.CreateDirectory(Workspace);
            var dir = Path.Combine(Workspace, (string)attempt["id"]); Directory.CreateDirectory(dir);
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Journal directory is a link.");
            var stage = Path.Combine(dir, "incoming.save"); DurableWrite(stage, bytes);
            var existed = File.Exists(Target); var backup = Path.Combine(dir, "original.save");
            FileStream original = null; JObject journal = null;
            try
            {
                if (existed)
                {
                    // Retain the existing-file handle through replacement. Delete sharing permits File.Replace,
                    // while concurrent readers/writers are denied, keeping the backup equal to the replaced file.
                    original = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Delete);
                    using (var copy = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { original.CopyTo(copy); copy.Flush(true); }
                }
                journal = new JObject { ["version"] = 1, ["target"] = Target, ["sessionId"] = session, ["attemptId"] = attempt["id"], ["artifactId"] = artifact, ["hash"] = hash,
                    ["existed"] = existed, ["originalHash"] = existed ? Hash(File.ReadAllBytes(backup)) : null, ["state"] = "Applying" };
                Journal(dir, journal);
                token.ThrowIfCancellationRequested(); CheckSafe();
                if (existed) File.Replace(stage, Target, null); else File.Move(stage, Target);
                if (Hash(File.ReadAllBytes(Target)) != hash) throw new IOException("Post-restore verification failed.");
                journal["state"] = "Applied"; Journal(dir, journal);
                return new RestoreReceipt(this, dir, journal, m);
            }
            catch { original?.Dispose(); if (journal != null) Rollback(dir, journal); throw; }
            finally { original?.Dispose(); }
        }
        public void RecoverIncomplete()
        {
            CheckSafe(); if (!Directory.Exists(Workspace)) return;
            foreach (var dir in Directory.GetDirectories(Workspace))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Journal path contains a link.");
                var path = Path.Combine(dir, "journal.json"); if (!File.Exists(path)) continue;
                var j = JObject.Parse(File.ReadAllText(path));
                if ((string)j["target"] != Target) throw new IOException("Journal target mismatch.");
                if ((string)j["state"] == "Applying") Rollback(dir, j);
                else if ((string)j["state"] == "Applied" || (string)j["state"] == "CommitPending")
                    throw new IOException("Incomplete handoff requires backend reconciliation before recovery.");
            }
        }
        public async Task RecoverIncompleteAsync(HoleInOneBackendClient api, CancellationToken token)
        {
            CheckSafe(); if (!Directory.Exists(Workspace)) return;
            foreach (var dir in Directory.GetDirectories(Workspace))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Journal path is a link.");
                var path = Path.Combine(dir, "journal.json"); if (!File.Exists(path)) continue;
                var j = JObject.Parse(File.ReadAllText(path));
                if ((string)j["target"] != Target) throw new IOException("Journal target mismatch.");
                var status = (string)j["state"];
                if (status == "Applying") { Rollback(dir, j); continue; }
                if (status != "Applied" && status != "CommitPending") continue;
                if (!Guid.TryParse((string)j["sessionId"], out _) || !Guid.TryParse((string)j["attemptId"], out _))
                    throw new IOException("Journal lacks backend identity; manual recovery is required.");
                // A crash between commit and journal completion is resolved against durable server state.
                // Expired/unreachable/another attempt cannot prove non-commit; keep both saves untouched.
                var state = await api.GetAsync("v2/sessions/" + (string)j["sessionId"], token).ConfigureAwait(false);
                if ((string)state["attempt"]?["id"] != (string)j["attemptId"]) throw new IOException("Historical attempt outcome is unresolved.");
                if ((string)state["state"] == "Committed") { j["state"] = "Committed"; Journal(dir, j); }
                else if ((string)state["state"] == "Failed" || (string)state["state"] == "Cancelled") Rollback(dir, j);
                else throw new IOException("Backend attempt is unresolved; original backup retained.");
            }
        }
        internal void Rollback(string dir, JObject journal)
        {
            CheckSafe();
            if ((string)journal["target"] != Target) throw new IOException("Rollback target mismatch.");
            // Never overwrite a run which started/changed after restore. Keep backup for manual recovery.
            if (File.Exists(Target))
            {
                var current = Hash(File.ReadAllBytes(Target));
                if (current != (string)journal["hash"] && current != (string)journal["originalHash"])
                    throw new IOException("Save changed since restore; preserved journal/backup requires manual recovery.");
            }
            if ((bool)journal["existed"])
            {
                var backup = Path.Combine(dir, "original.save");
                if ((File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0 || Hash(File.ReadAllBytes(backup)) != (string)journal["originalHash"])
                    throw new IOException("Original backup verification failed.");
                var stage = Path.Combine(dir, "rollback.save"); DurableWrite(stage, File.ReadAllBytes(backup));
                if (File.Exists(Target)) File.Replace(stage, Target, null); else File.Move(stage, Target);
            }
            else if (File.Exists(Target)) File.Delete(Target);
            journal["state"] = "RolledBack"; Journal(dir, journal);
        }
        public sealed class RestoreReceipt : IDisposable
        {
            private readonly SaveRestoreService owner; private readonly string directory; private readonly JObject journal;
            public JObject Manifest { get; }
            internal IDisposable Ownership { get; set; }
            internal RestoreReceipt(SaveRestoreService owner, string dir, JObject journal, JObject manifest)
            { this.owner = owner; directory = dir; this.journal = journal; Manifest = manifest; }
            public void Rollback() => owner.Rollback(directory, journal);
            public void MarkCommitPending() { journal["state"] = "CommitPending"; owner.Journal(directory, journal); }
            public void Commit() { journal["state"] = "Committed"; owner.Journal(directory, journal); }
            public void Dispose() { Ownership?.Dispose(); Ownership = null; }
        }
    }
}
