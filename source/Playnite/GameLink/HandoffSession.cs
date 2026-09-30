using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace Playnite.GameLink
{
    public enum HandoffState { Playing, PointObserved, Ready, Switching, Local, Failed, Cancelled, Closed }

    public interface ILocalHandoff
    {
        // Return only after the save is restored and local continuation is confirmed.
        Task<bool> SwitchAsync(Guid gameId, string handoffId, string artifactId, CancellationToken cancellationToken);
    }

    public sealed class HandoffSession : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HashSet<string> eventIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object sync = new object();
        private readonly ILocalHandoff localHandoff;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private long lastSequence = -1;
        private bool disposed;

        public Guid GameId { get; }
        public string ProviderGameId { get; }
        public string SessionId { get; }
        public HandoffState State { get; private set; } = HandoffState.Playing;
        public event EventHandler StateChanged;
        public event EventHandler LocalReady;

        public HandoffSession(Guid gameId, string providerGameId, string sessionId, ILocalHandoff localHandoff)
        {
            GameId = gameId;
            ProviderGameId = providerGameId ?? throw new ArgumentNullException(nameof(providerGameId));
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            this.localHandoff = localHandoff ?? throw new ArgumentNullException(nameof(localHandoff));
        }

        public bool Receive(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 8192) return false;
            JObject message;
            try { message = JObject.Parse(json); }
            catch { return false; }

            string type, eventId, sessionId, gameId;
            long sequence;
            try
            {
                if ((int)message["version"] != 1) return false;
                type = (string)message["type"];
                eventId = (string)message["eventId"];
                sessionId = (string)message["sessionId"];
                gameId = (string)message["gameId"];
                sequence = (long)message["sequence"];
                if (!Guid.TryParse(eventId, out _) || !Guid.TryParse(sessionId, out _)) return false;
                if (!DateTimeOffset.TryParse((string)message["occurredAtUtc"], out _)) return false;
            }
            catch { return false; }
            if (sessionId != SessionId || gameId != ProviderGameId || sequence < 0) return false;
            if (type != "handoff.point.reached" && type != "handoff.ready" &&
                type != "handoff.failed" && type != "handoff.cancelled") return false;

            string handoffId = null, artifactId = null;
            try
            {
                if (type == "handoff.ready")
                {
                    var payload = message["payload"] as JObject;
                    handoffId = (string)payload?["handoffId"];
                    artifactId = (string)payload?["artifactId"];
                    if (!Guid.TryParse(handoffId, out _) || string.IsNullOrWhiteSpace(artifactId)) return false;
                }
            }
            catch { return false; }

            lock (sync)
            {
                if (disposed || sequence <= lastSequence || !eventIds.Add(eventId) ||
                    State == HandoffState.Switching || State == HandoffState.Local ||
                    State == HandoffState.Closed) return false;
                lastSequence = sequence;
                if (type == "handoff.point.reached") State = HandoffState.PointObserved;
                else if (type == "handoff.ready") State = HandoffState.Ready;
                else State = type == "handoff.failed" ? HandoffState.Failed : HandoffState.Cancelled;
            }
            logger.Info($"GameLink session {SessionId}, event {eventId}, state {State}");
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (type == "handoff.ready") _ = SwitchAsync(handoffId, artifactId);
            return true;
        }

        private async Task SwitchAsync(string handoffId, string artifactId)
        {
            lock (sync)
            {
                if (disposed || State != HandoffState.Ready) return;
                State = HandoffState.Switching;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            bool succeeded = false;
            try
            {
                var switchTask = localHandoff.SwitchAsync(GameId, handoffId, artifactId, cancellation.Token);
                var finished = await Task.WhenAny(switchTask, Task.Delay(TimeSpan.FromMinutes(2), cancellation.Token));
                if (finished == switchTask) succeeded = await switchTask;
            }
            catch (OperationCanceledException) { }
            catch { }
            lock (sync)
            {
                if (disposed) return;
                State = succeeded ? HandoffState.Local : HandoffState.Failed;
            }
            logger.Info($"GameLink session {SessionId}, state {State}");
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (succeeded) LocalReady?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; State = HandoffState.Closed; }
            cancellation.Cancel();
            cancellation.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
