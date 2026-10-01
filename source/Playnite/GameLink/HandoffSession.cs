using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    public enum HandoffState { Playing, PointObserved, Ready, Switching, Local, Failed, Cancelled, Closed }
    public sealed class HandoffSession : IDisposable
    {
        private readonly object sync = new object();
        private readonly HashSet<string> attempts = new HashSet<string>(StringComparer.Ordinal);
        private readonly ILocalHandoff local;
        private CancellationTokenSource attemptCancellation;
        private long sequence;
        private string activeAttempt;
        private bool disposed;
        public Guid GameId { get; }
        public string ProviderGameId { get; }
        public string SessionId { get; }
        public HandoffState State { get; private set; } = HandoffState.Playing;
        public bool PointObserved { get; private set; }
        public event EventHandler StateChanged;
        public event EventHandler LocalReady;
        public HandoffSession(Guid gameId, string providerGameId, string sessionId, ILocalHandoff local)
        { GameId = gameId; ProviderGameId = providerGameId; SessionId = sessionId; this.local = local; }
        public bool Receive(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16384) return false;
            JObject e;
            try { e = JObject.Parse(json); } catch { return false; }
            string type; long next; JObject a;
            try
            {
                type = (string)e["type"]; next = (long)e["sequence"]; a = e["payload"] as JObject;
                if ((int?)e["version"] != 2 || (string)e["sessionId"] != SessionId || (string)e["gameId"] != ProviderGameId || next <= 0 ||
                    !Guid.TryParse((string)e["eventId"], out _) || !DateTimeOffset.TryParse((string)e["occurredAtUtc"], out _)) return false;
                if (type == "handoff.ready" && (a == null || !Guid.TryParse((string)a["id"], out _) ||
                    !Guid.TryParse((string)a["artifactId"], out _) || a["expires"]?.Type != JTokenType.Integer ||
                    !(a["identity"] is JObject) || string.IsNullOrEmpty((string)a["generation"]))) return false;
                if (type != "handoff.point.reached" && type != "handoff.ready" && type != "handoff.failed" && type != "handoff.cancelled" &&
                    type != "handoff.prepare" && type != "handoff.frozen" && type != "local.restore.completed" &&
                    type != "handoff.committed" && type != "stream.terminated") return false;
            }
            catch { return false; }
            var start = false; CancellationToken token = default;
            lock (sync)
            {
                if (disposed || State == HandoffState.Closed) return false;
                if (next <= sequence) return true;
                sequence = next;
                if (State == HandoffState.Local) return true;
                if (type == "handoff.ready" && (long)a["expires"] <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return true;
                if (type == "handoff.point.reached") { PointObserved = true; if (State == HandoffState.Playing) State = HandoffState.PointObserved; }
                else if (type == "handoff.ready")
                {
                    var id = (string)a["id"];
                    if (attempts.Add(id))
                    {
                        attemptCancellation?.Cancel(); attemptCancellation?.Dispose();
                        attemptCancellation = new CancellationTokenSource(); token = attemptCancellation.Token;
                        activeAttempt = id; State = HandoffState.Switching; start = true;
                    }
                }
                else if (type == "handoff.failed" || type == "handoff.cancelled")
                {
                    if (activeAttempt == null || (string)a?["id"] == activeAttempt)
                    { attemptCancellation?.Cancel(); State = type == "handoff.failed" ? HandoffState.Failed : HandoffState.Cancelled; }
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (start) _ = SwitchAsync(a, token);
            return true;
        }
        public void RestoreSnapshot(JObject snapshot)
        {
            if ((string)snapshot["id"] != SessionId || (string)snapshot["gameId"] != ProviderGameId) throw new ArgumentException("Snapshot session mismatch.");
            JObject attempt = null; CancellationToken token = default; bool completed = false;
            lock (sync)
            {
                if (disposed || State == HandoffState.Local) return;
                sequence = Math.Max(sequence, (long?)snapshot["cursor"] ?? 0);
                if ((string)snapshot["state"] == "Committed") { State = HandoffState.Local; completed = true; }
                else if ((string)snapshot["state"] == "ArtifactReady" && snapshot["attempt"] is JObject a && attempts.Add((string)a["id"]))
                {
                    if ((long?)a["expires"] <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return;
                    attempt = (JObject)a.DeepClone(); activeAttempt = (string)a["id"];
                    attemptCancellation = new CancellationTokenSource(); token = attemptCancellation.Token; State = HandoffState.Switching;
                }
                else if ((string)snapshot["state"] == "Failed") State = HandoffState.Failed;
                else if ((string)snapshot["state"] == "Cancelled") State = HandoffState.Cancelled;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (completed) LocalReady?.Invoke(this, EventArgs.Empty);
            if (attempt != null) _ = SwitchAsync(attempt, token);
        }
        private async Task SwitchAsync(JObject attempt, CancellationToken token)
        {
            bool success;
            try { success = await local.SwitchAsync(GameId, (JObject)attempt.DeepClone(), token).ConfigureAwait(false); }
            catch { success = false; }
            lock (sync)
            {
                if (disposed || token.IsCancellationRequested || activeAttempt != (string)attempt["id"] || State != HandoffState.Switching) return;
                State = success ? HandoffState.Local : HandoffState.Failed;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (success) LocalReady?.Invoke(this, EventArgs.Empty);
        }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; State = HandoffState.Closed; attemptCancellation?.Cancel(); }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
