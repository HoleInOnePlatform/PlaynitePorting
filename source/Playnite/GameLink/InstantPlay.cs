using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    public sealed class InstantPlayAddress
    {
        public string SessionId { get; set; }
        public Uri Url { get; set; }
        public string PlaybackMode { get; set; }
        internal HoleInOneBackendClient Backend { get; set; }
        internal NativeSessionJournal Journal { get; set; }
    }
    public interface IInstantPlayAddressProvider
    { Task<InstantPlayAddress> CreateAsync(Game game, CancellationToken token); }
    internal sealed class BackendInstantPlayAddressProvider : IInstantPlayAddressProvider
    {
        public async Task<InstantPlayAddress> CreateAsync(Game game, CancellationToken token)
        {
            if (game.GameId != "2868840") throw new InvalidOperationException("This game has no verified cloud-save adapter.");
            if (!BackendConnectionDialog.EnsureConfigured()) throw new OperationCanceledException();
            var api = HoleInOneBackendClient.FromEnvironment(); string session = null; NativeSessionJournal journal = null;
            try
            {
                var capabilities = await api.GetAsync("v2/capabilities", token).ConfigureAwait(false);
                if ((bool?)capabilities["video"] != true)
                    throw new InvalidOperationException("현재 백엔드가 테스트(fake) 모드로 실행되어 실제 GameLift 세션을 만들지 않습니다. 로컬 백엔드에서도 AWS 연동 모드를 사용하면 게임 영상을 재생할 수 있습니다.");
                journal = new NativeSessionJournal(api.BaseUri, game.Id);
                JObject state = null;
                if (journal.SessionId != null)
                {
                    try { state = await api.GetAsync("v2/sessions/" + journal.SessionId, token).ConfigureAwait(false); }
                    catch (BackendRejectedException e) when (e.Status == 404 || e.Status == 410) { journal.Reset(); }
                    var previousStream = (string)state?["stream"];
                    if (state != null && (string)state["state"] != "Committed" &&
                        (previousStream == "TERMINATED" || previousStream == "ERROR" || previousStream == "FAILED"))
                    {
                        if (previousStream != "TERMINATED")
                            await api.PostAsync("v2/sessions/" + journal.SessionId + "/close", new { }, token).ConfigureAwait(false);
                        journal.Reset(); state = null;
                    }
                }
                if (state == null) state = await api.PostAsync("v2/sessions", new { gameId = game.GameId }, token, journal.RequestId).ConfigureAwait(false);
                session = (string)state["id"];
                if (!Guid.TryParse(session, out _)) throw new InvalidOperationException("Invalid backend session.");
                journal.SessionId = session;
                if ((string)state["state"] == "Preparing" || (string)state["state"] == "Frozen" || (string)state["state"] == "Restored")
                    state = await api.PostAsync("v2/sessions/" + session + "/fail", new { attemptId = (string)state["attempt"]["id"], reason = "native_restarted" }, token,
                        "restart-" + (string)state["attempt"]["id"]).ConfigureAwait(false);
                if ((string)state["state"] == "Committed")
                    return new InstantPlayAddress { SessionId = session, Backend = api, Journal = journal, Url = api.BaseUri };
                // URL_READY means an unused hosted link is ready; it is not proof of a running game.
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    startup.CancelAfter(TimeSpan.FromMinutes(10));
                    try
                    {
                        while ((string)state["stream"] != "ACTIVE" && (string)state["stream"] != "PENDING_CLIENT_RECONNECTION" && (string)state["stream"] != "URL_READY")
                        {
                            if ((string)state["stream"] == "TERMINATED" || (string)state["stream"] == "TERMINATING" || (string)state["stream"] == "ERROR" || (string)state["stream"] == "FAILED")
                                throw new InvalidOperationException(BackendStartupFailure.Describe((string)state["startupError"]));
                            await Task.Delay(500, startup.Token).ConfigureAwait(false);
                            state = await api.GetAsync("v2/sessions/" + session, startup.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new InvalidOperationException("GameLift 스트림 준비가 제한 시간을 초과했습니다. 백엔드 상태를 확인한 뒤 다시 실행해 주세요."); }
                }
                var ticket = await api.PostAsync("v2/sessions/" + session + "/ticket", new { }, token).ConfigureAwait(false);
                return new InstantPlayAddress { SessionId = session, Backend = api, Journal = journal,
                    Url = BackendPlaybackAddress.Validate(api.BaseUri, ticket, DateTimeOffset.UtcNow), PlaybackMode = (string)ticket["playbackMode"] };
            }
            catch
            {
                // Preserve durable create/request state on transient loss so a restart reuses the same session.
                journal?.Dispose(); api.Dispose(); throw;
            }
        }
    }
    internal sealed class InstantPlayView : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly InstantPlayWebView view; private readonly HoleInOneBackendClient api;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly string clientId = Guid.NewGuid().ToString("N"); private bool disposed;
        private readonly NativeSessionJournal journal;
        private readonly string playbackMode;
        private long cursor;
        public HandoffSession Session { get; }
        public bool RestSiteReached => Session.PointObserved;
        public InstantPlayView(InstantPlayWebView view, InstantPlayAddress address, Game game, ILocalGameAdapter adapter)
        {
            this.view = view; api = address.Backend ?? throw new InvalidOperationException("Native backend client is required.");
            playbackMode = address.PlaybackMode;
            view.HostedPlayback = playbackMode == "aws-hosted-url";
            journal = address.Journal; cursor = journal?.Cursor ?? 0;
            Session = new HandoffSession(game.Id, game.GameId, address.SessionId, new BackendLocalHandoff(api, address.SessionId, adapter));
            view.WindowHost.Closed += OnClosed; view.RetryRequested += OnRetryRequested; Session.LocalReady += OnLocalReady; _ = SubscribeAsync();
        }
        private string Route(string op) => "v2/sessions/" + Session.SessionId + "/" + op;
        private async Task SubscribeAsync()
        {
            // JSON replay is deliberately used on net462/x86. Authentication stays in C#; cursor is global and gaps are valid.
            var token = cancellation.Token;
            try
            {
                var snapshot = await api.GetAsync(Route(""), token).ConfigureAwait(false);
                Session.RestoreSnapshot(snapshot); cursor = Math.Max(cursor, (long?)snapshot["cursor"] ?? 0);
                if (journal != null) journal.Cursor = cursor;
            }
            catch { }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (journal != null && journal.CredentialFingerprint != NativeSessionJournal.Credentials())
                    { Session.Dispose(); cancellation.Cancel(); return; }
                    var page = await api.GetAsync(Route("events?after=" + cursor), token).ConfigureAwait(false);
                    if (!(page["events"] is JArray events)) throw new InvalidOperationException("Invalid backend replay page.");
                    foreach (var e in events)
                    {
                        if ((string)e["sessionId"] != Session.SessionId || (string)e["gameId"] != Session.ProviderGameId) throw new InvalidOperationException("Replay session mismatch.");
                        var next = (long)e["sequence"]; if (next <= cursor) continue;
                        if (!Session.Receive(e.ToString(Newtonsoft.Json.Formatting.None)))
                            throw new System.IO.InvalidDataException("Replay event rejected by native contract; cursor retained.");
                        cursor = next;
                    }
                    // Repeat the highest ACK even for an empty page after an ACK response was lost.
                    await api.PostAsync(Route("ack"), new { clientId, sequence = cursor }, token).ConfigureAwait(false);
                    if (journal != null) journal.Cursor = cursor;
                    var state = await api.GetAsync(Route(""), token).ConfigureAwait(false);
                    if ((string)state["stream"] == "TERMINATED" && (string)state["state"] != "Committed") return;
                    await Task.Delay(700, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (BackendRejectedException e) when (e.Status == 401 || e.Status == 403 || e.Status == 410)
                { logger.Warn("Native backend subscription stopped (authorization/session unavailable)."); Session.Dispose(); return; }
                catch (NativeBackendConfigurationException)
                { Session.Dispose(); cancellation.Cancel(); return; }
                catch (Exception) { if (!token.IsCancellationRequested) await Task.Delay(1500, token).ConfigureAwait(false); }
            }
        }
        public void SetInstallationReady()
        {
            if (!disposed) _ = ReportInstallationAsync();
        }
        private async Task ReportInstallationAsync()
        { try { await api.PostAsync(Route("installation"), new { ready = true }, cancellation.Token).ConfigureAwait(false); } catch { } }
        private async void OnRetryRequested(object sender, EventArgs args)
        {
            if (playbackMode == "aws-hosted-url")
            {
                // Hosted links start a new AWS session and cannot reconnect an existing one.
                // In particular, do not navigate away/reload and consume a second link on F5.
                view.ShowStatus("AWS 스트림 링크는 기존 세션에 재접속할 수 없습니다. 새 세션을 시작하려면 이 창을 닫고 즉시 플레이를 다시 실행해 주세요.");
                return;
            }
            try
            {
                view.Disconnect();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    while (true)
                    {
                        var current = await api.GetAsync(Route(""), timeout.Token);
                        var stream = (string)current["stream"];
                        if (stream == "ACTIVE" || stream == "PENDING_CLIENT_RECONNECTION") break;
                        if (stream == "ERROR" || stream == "TERMINATED" || stream == "TERMINATING") throw new InvalidOperationException("Stream cannot reconnect.");
                        await Task.Delay(400, timeout.Token);
                    }
                }
                var ticket = await api.PostAsync(Route("ticket"), new { }, cancellation.Token);
                if ((string)ticket["playbackMode"] != playbackMode) throw new InvalidOperationException("Playback mode changed during reconnect.");
                if (!disposed) view.Navigate(BackendPlaybackAddress.Validate(api.BaseUri, ticket, DateTimeOffset.UtcNow).AbsoluteUri);
            }
            catch { logger.Warn("Native stream reconnect ticket unavailable."); }
        }
        private void OnLocalReady(object sender, EventArgs e) => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => view.Close()));
        private void OnClosed(object sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var completed = Session.State == HandoffState.Local;
            cancellation.Cancel(); Session.Dispose();
            view.WindowHost.Closed -= OnClosed; view.RetryRequested -= OnRetryRequested; Session.LocalReady -= OnLocalReady; view.Dispose();
            _ = CloseAsync(completed);
        }
        private async Task CloseAsync(bool completed)
        {
            var closeConfirmed = completed;
            try
            {
                if (!completed) using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var response = await api.PostAsync(Route("close"), new { }, timeout.Token).ConfigureAwait(false);
                    closeConfirmed = (string)response["stream"] == "TERMINATED" || (string)response["stream"] == "TERMINATING";
                }
            }
            catch { logger.Warn("Backend close response unavailable; server session expiry remains active."); }
            finally { if (journal != null) { if (closeConfirmed) journal.Closed = true; journal.Dispose(); } api.Dispose(); cancellation.Dispose(); }
        }
    }
}
