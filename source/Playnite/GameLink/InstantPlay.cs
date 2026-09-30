using Playnite.SDK.Models;
using Playnite.WebView;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        private TcpListener localSignalListener;
        private volatile bool disposed;
        public HandoffSession Session { get; }

        public InstantPlayView(WebView.WebView view, InstantPlayAddress address, Game game, ILocalHandoff handoff)
        {
            this.view = view;
            origin = new Uri(address.Url.GetLeftPart(UriPartial.Authority));
            Session = new HandoffSession(game.Id, game.GameId ?? game.Id.ToString(), address.SessionId, handoff);
            view.GameLinkMessageReceived += OnMessage;
            view.WindowHost.Closed += OnClosed;
            Session.LocalReady += OnLocalReady;
            StartLocalSignalListener();
        }

        private void StartLocalSignalListener()
        {
            if (Session.ProviderGameId != "2868840") return;
            // Only the active Slay the Spire 2 instant-play view receives this signal.
            try
            {
                localSignalListener = new TcpListener(IPAddress.Loopback, 8767);
                localSignalListener.Start();
                _ = ListenForLocalRunAsync(localSignalListener);
            }
            catch (SocketException)
            {
                localSignalListener?.Stop();
                localSignalListener = null;
            }
        }

        private async Task ListenForLocalRunAsync(TcpListener listener)
        {
            while (!disposed)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => ReceiveLocalRun(client));
            }
        }

        private void ReceiveLocalRun(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 1000;
                    client.SendTimeout = 1000;
                    using (var reader = new StreamReader(client.GetStream()))
                    using (var writer = new StreamWriter(client.GetStream()) { AutoFlush = true })
                    {
                        var message = reader.ReadLine();
                        if (disposed || message != "GAMELOADER_RUN_LOADED_V1 2868840" ||
                            Session.ProviderGameId != "2868840" || Session.State == HandoffState.Closed)
                        {
                            writer.WriteLine("IGNORED");
                            return;
                        }

                        writer.WriteLine("OK");
                        Session.ConfirmLocalRunLoaded();
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
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

        private void OnLocalReady(object sender, EventArgs e)
        {
            view.Close();
        }
        private void OnClosed(object sender, EventArgs e) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            localSignalListener?.Stop();
            localSignalListener = null;
            view.GameLinkMessageReceived -= OnMessage;
            view.WindowHost.Closed -= OnClosed;
            Session.LocalReady -= OnLocalReady;
            Session.Dispose();
            view.Dispose();
        }
    }
}
