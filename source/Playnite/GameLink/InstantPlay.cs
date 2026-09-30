using Playnite.SDK.Models;
using Playnite.WebView;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    public sealed class InstantPlayAddress
    {
        public string SessionId { get; set; }
        public Uri Url { get; set; }
        internal Process Server { get; set; }
    }

    public interface IInstantPlayAddressProvider
    {
        InstantPlayAddress Create(Game game);
    }

    internal sealed class LoopbackInstantPlayAddressProvider : IInstantPlayAddressProvider
    {
        public InstantPlayAddress Create(Game game)
        {
            var sessionId = Guid.NewGuid().ToString();
            var root = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "GameLink", "WebClient");
            if (!File.Exists(Path.Combine(root, "server.js"))) throw new FileNotFoundException("GameLink web client was not installed.");
            int port;
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            finally { reservation.Stop(); }
            var token = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo("node", "server.js")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["PORT"] = port.ToString();
            start.EnvironmentVariables["GAMELINK_TOKEN"] = token;
            var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("Could not start the GameLink server. Install Node.js and run npm install in GameLink/WebClient.");
            try
            {
                var mock = Environment.GetEnvironmentVariable("GAMELINK_MOCK") == "1" ? "&mock=1" : "";
                var url = new Uri($"http://127.0.0.1:{port}/?token={token}&sessionId={sessionId}&gameId={Uri.EscapeDataString(game.GameId ?? game.Id.ToString())}{mock}");
                using (var client = new WebClient())
                {
                    var ready = false;
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        if (process.HasExited) break;
                        try { client.DownloadString(url); ready = true; break; }
                        catch (WebException) { Thread.Sleep(100); }
                    }
                    if (!ready) throw new InvalidOperationException("GameLink server did not start. Check Node.js, npm dependencies, and GameLink configuration.");
                }
                return new InstantPlayAddress { SessionId = sessionId, Url = url, Server = process };
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
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
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly WebView.WebView view;
        private readonly Uri origin;
        private readonly Process server;
        private TcpListener localSignalListener;
        private volatile bool disposed;
        public HandoffSession Session { get; }
        public bool RestSiteReached { get; private set; }

        public InstantPlayView(WebView.WebView view, InstantPlayAddress address, Game game, ILocalHandoff handoff)
        {
            this.view = view;
            server = address.Server;
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
            if (args.Message is string json)
            {
                if (IsRestSiteSignal(json))
                {
                    if (!RestSiteReached)
                    {
                        RestSiteReached = true;
                        logger.Info($"GameLink session {Session.SessionId}: rest site reached");
                    }
                    return;
                }
                Session.Receive(json);
            }
        }

        internal static bool IsRestSiteSignal(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 256) return false;
            try
            {
                var message = JObject.Parse(json);
                return message.Count == 1 && (string)message["type"] == "rest_site_reached";
            }
            catch { return false; }
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
            if (server != null)
            {
                try { if (!server.HasExited) server.Kill(); }
                catch (InvalidOperationException) { }
                server.Dispose();
            }
        }
    }
}
