using Playnite.SDK.Models;
using Playnite.WebView;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    public sealed class InstantPlayAddress
    {
        public string SessionId { get; set; }
        public Uri Url { get; set; }
    }

    public interface IInstantPlayAddressProvider
    {
        InstantPlayAddress Create(Game game);
    }

    // Development endpoint. Replace this provider when the GameLink session service is connected.
    internal sealed class LoopbackInstantPlayAddressProvider : IInstantPlayAddressProvider
    {
        public InstantPlayAddress Create(Game game)
        {
            var sessionId = Guid.NewGuid().ToString();
            return new InstantPlayAddress
            {
                SessionId = sessionId,
                Url = new Uri("http://127.0.0.1:8765/mock.html?sessionId=" + sessionId + "&gameId=" + Uri.EscapeDataString(game.GameId ?? game.Id.ToString()))
            };
        }
    }

    internal sealed class UnavailableLocalHandoff : ILocalHandoff
    {
        public Task<bool> SwitchAsync(Guid gameId, string handoffId, string artifactId, CancellationToken cancellationToken)
        {
            // No save restoration is implemented in this repository yet. Fail closed.
            return Task.FromResult(false);
        }
    }

    internal sealed class InstantPlayView : IDisposable
    {
        private readonly WebView.WebView view;
        private readonly Uri origin;
        private bool disposed;
        public HandoffSession Session { get; }

        public InstantPlayView(WebView.WebView view, InstantPlayAddress address, Game game, ILocalHandoff handoff)
        {
            this.view = view;
            origin = new Uri(address.Url.GetLeftPart(UriPartial.Authority));
            Session = new HandoffSession(game.Id, game.GameId ?? game.Id.ToString(), address.SessionId, handoff);
            view.GameLinkMessageReceived += OnMessage;
            view.WindowHost.Closed += OnClosed;
            Session.StateChanged += OnStateChanged;
        }

        private void OnMessage(object sender, CefSharp.JavascriptMessageReceivedEventArgs args)
        {
            if (disposed || args.Frame == null || !args.Frame.IsMain) return;
            if (!Uri.TryCreate(args.Frame.Url, UriKind.Absolute, out var frameUrl) ||
                frameUrl.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)) return;
            if (!Uri.TryCreate(view.GetCurrentAddress(), UriKind.Absolute, out var currentUrl) ||
                currentUrl.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)) return;
            if (args.Message is string json) Session.Receive(json);
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            // Temporary flow: a validated ready closes the cloud view without local restoration.
            if (Session.State == HandoffState.Ready) view.Close();
        }
        private void OnClosed(object sender, EventArgs e) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            view.GameLinkMessageReceived -= OnMessage;
            view.WindowHost.Closed -= OnClosed;
            Session.StateChanged -= OnStateChanged;
            Session.Dispose();
            view.Dispose();
        }
    }
}
