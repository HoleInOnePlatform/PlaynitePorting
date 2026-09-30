using Playnite.SDK.Models;
using Playnite.WebView;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using System.Text;
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
        internal IDisposable Server { get; set; }
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
            Process process = null;
            try
            {
                try { process = Process.Start(start); }
                catch (Win32Exception) { }
                var mock = Environment.GetEnvironmentVariable("GAMELINK_MOCK") == "1" ? "&mock=1" : "";
                var url = new Uri($"http://127.0.0.1:{port}/?token={token}&sessionId={sessionId}&gameId={Uri.EscapeDataString(game.GameId ?? game.Id.ToString())}{mock}");
                if (process != null)
                {
                    using (var client = new WebClient())
                    {
                        for (var attempt = 0; attempt < 40; attempt++)
                        {
                            if (process.HasExited) break;
                            try { client.DownloadString(url); return new InstantPlayAddress { SessionId = sessionId, Url = url, Server = new ProcessHost(process) }; }
                            catch (WebException) { Thread.Sleep(100); }
                        }
                    }
                    if (!process.HasExited) process.Kill();
                    process.Dispose();
                    process = null;
                }
                return new InstantPlayAddress { SessionId = sessionId, Url = url, Server = new LoopbackFallbackServer(root, port) };
            }
            catch { if (process != null) { if (!process.HasExited) process.Kill(); process.Dispose(); } throw; }
        }
    }

    internal sealed class ProcessHost : IDisposable
    {
        private readonly Process process;
        public ProcessHost(Process process) { this.process = process; }
        public void Dispose()
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    // Keeps the bundled page usable for local signal testing when Node.js or its AWS package is absent.
    internal sealed class LoopbackFallbackServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly string root;
        private bool disposed;

        public LoopbackFallbackServer(string root, int port)
        {
            this.root = Path.Combine(root, "public");
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _ = ListenAsync();
        }

        private async Task ListenAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 2000;
                    client.SendTimeout = 2000;
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var request = reader.ReadLine()?.Split(' ');
                    if (request == null || request.Length < 2 || request[0] != "GET") return;
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }
                    var path = request[1].Split('?')[0];
                    byte[] body;
                    string contentType;
                    int status;
                    if (path == "/api/options")
                    {
                        body = Encoding.UTF8.GetBytes("[]");
                        contentType = "application/json; charset=utf-8";
                        status = 200;
                    }
                    else if (path == "/" || path == "/index.html" || path == "/app.js" || path == "/signal.js" || path == "/style.css")
                    {
                        var name = path == "/" ? "index.html" : path.Substring(1);
                        body = File.ReadAllBytes(Path.Combine(root, name));
                        contentType = name.EndsWith(".css") ? "text/css; charset=utf-8" : name.EndsWith(".js") ? "text/javascript; charset=utf-8" : "text/html; charset=utf-8";
                        status = 200;
                    }
                    else
                    {
                        body = Encoding.UTF8.GetBytes("Not found");
                        contentType = "text/plain; charset=utf-8";
                        status = 404;
                    }
                    var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(header);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Write(body, 0, body.Length);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            disposed = true;
            listener.Stop();
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
        private readonly IDisposable server;
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
            server?.Dispose();
        }
    }
}
