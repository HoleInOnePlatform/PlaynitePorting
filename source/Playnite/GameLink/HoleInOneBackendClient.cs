using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.GameLink
{
    // Credentials are supplied by the native login/development adapter, never the WebView.
    public sealed class HoleInOneBackendClient : IDisposable
    {
        private readonly HttpClient http;
        private readonly Func<string> credentials;
        public Uri BaseUri { get; }
        public HoleInOneBackendClient(Uri address, Func<string> credentials)
        {
            if (address == null || !(address.Scheme == "https" || address.Scheme == "http" && address.IsLoopback) ||
                address.AbsolutePath != "/" || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
                throw new ArgumentException("Backend must use HTTPS, or loopback HTTP for development.");
            BaseUri = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
            this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        }
        public static HoleInOneBackendClient FromEnvironment()
        {
            var initial = NativeBackendConfiguration.Load() ?? throw new NativeBackendConfigurationException("즉시 플레이의 백엔드 연결 설정이 필요합니다.");
            return new HoleInOneBackendClient(initial.Address, () =>
            {
                var current = NativeBackendConfiguration.Load();
                if (current == null || current.Address != initial.Address) throw new NativeBackendConfigurationException("백엔드 연결 설정이 변경되었습니다. 즉시 플레이를 다시 시작해 주세요.");
                return current.Token;
            });
        }
        public Task<JObject> GetAsync(string path, CancellationToken token) => JsonAsync(HttpMethod.Get, path, null, null, token);
        public Task<JObject> PostAsync(string path, object body, CancellationToken token, string requestId = null) =>
            JsonAsync(HttpMethod.Post, path, body, requestId ?? Guid.NewGuid().ToString("N"), token);
        public async Task<JObject> JsonAsync(HttpMethod method, string path, object body, string requestId, CancellationToken token)
        {
            var bytes = await RequestAsync(method, path, body, requestId, 2 * 1024 * 1024, token).ConfigureAwait(false);
            return JObject.Parse(Encoding.UTF8.GetString(bytes));
        }
        public Task<byte[]> DownloadAsync(string path, long maxBytes, CancellationToken token) =>
            RequestAsync(HttpMethod.Get, path, null, null, maxBytes, token);
        private async Task<byte[]> RequestAsync(HttpMethod method, string path, object body, string requestId, long maxBytes, CancellationToken token)
        {
            if (path.StartsWith("/") || path.Contains("..") || Uri.IsWellFormedUriString(path, UriKind.Absolute))
                throw new ArgumentException("Only backend-relative API paths are allowed.");
            var payload = body == null ? null : JsonConvert.SerializeObject(body);
            for (var retry = 0; ; retry++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var request = new HttpRequestMessage(method, new Uri(BaseUri, path)))
                    {
                        var bearer = credentials(); // Allows an external native login adapter to refresh credentials.
                        if (string.IsNullOrWhiteSpace(bearer)) throw new InvalidOperationException("Native user authentication is required.");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                        if (requestId != null) request.Headers.Add("Idempotency-Key", requestId);
                        if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                        {
                            if ((int)response.StatusCode >= 500 && retry < 2) throw new HttpRequestException("Backend temporarily unavailable.");
                            if (!response.IsSuccessStatusCode) throw new BackendRejectedException((int)response.StatusCode);
                            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Response exceeds contract size.");
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new MemoryStream())
                            {
                                var buffer = new byte[8192]; int read;
                                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                                {
                                    if (output.Length + read > maxBytes) throw new InvalidDataException("Response exceeds contract size.");
                                    output.Write(buffer, 0, read);
                                }
                                return output.ToArray();
                            }
                        }
                    }
                }
                catch (HttpRequestException) when (retry < 2) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested && retry < 2) { }
                await Task.Delay(250 * (retry + 1), token).ConfigureAwait(false);
            }
        }
        public void Dispose() => http.Dispose();
    }
    public sealed class BackendRejectedException : Exception
    {
        public int Status { get; }
        public BackendRejectedException(int status) : base("Backend rejected request (HTTP " + status + ").") { Status = status; }
    }
}
