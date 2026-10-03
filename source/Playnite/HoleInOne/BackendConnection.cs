using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    // Uses the same HTTP contract as the Backend project. No Playnite service calls.
    public sealed class BackendConnection : IDisposable
    {
        private readonly HttpClient client;

        public BackendConnection(string backendUrl, string bearerToken = null, HttpMessageHandler handler = null)
        {
            var address = ValidateAddress(backendUrl);
            client = handler == null
                ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                : new HttpClient(handler);
            client.BaseAddress = address;
            client.Timeout = TimeSpan.FromSeconds(15);
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
        }

        public async Task<CloudSession> CreateSessionAsync(CancellationToken cancellationToken, string gameId = null)
        {
            if (gameId != null) ValidateId(gameId);
            var session = await SendAsync<CloudSession>(HttpMethod.Post, "v1/holeinone/sessions",
                gameId == null ? null : new { gameId }, cancellationToken);
            ValidateId(session.SessionId);
            if (gameId != null && session.GameId != gameId)
                throw new InvalidDataException("요청한 게임과 클라우드 세션이 다름.");
            if (!Uri.TryCreate(session.StreamUrl, UriKind.Absolute, out var streamUri) || streamUri.Scheme != "https")
            {
                throw new InvalidOperationException("클라우드 플레이 URL이 없음. Backend의 스트리밍 설정을 확인해야 함.");
            }

            return session;
        }

        public async Task<List<CampfireEvent>> GetCampfiresAsync(string sessionId, long after, CancellationToken cancellationToken)
        {
            if (after < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(after));
            }

            var events = await SendAsync<List<CampfireEvent>>(HttpMethod.Get,
                SessionPath(sessionId) + "/campfires?after=" + after, null, cancellationToken);
            long sequence = after;
            foreach (var entry in events)
            {
                if (entry == null || entry.Notice == null || entry.Sequence <= sequence)
                {
                    throw new InvalidDataException("잘못된 모닥불 알림 응답임.");
                }

                ValidateId(entry.Notice.RunId);
                sequence = entry.Sequence;
            }

            return events;
        }

        public Task<SaveTransferStatus> RequestSaveAsync(string sessionId, string runId, CancellationToken cancellationToken)
        {
            ValidateId(runId);
            return SendAsync<SaveTransferStatus>(HttpMethod.Post, SessionPath(sessionId) + "/save-requests",
                new { runId }, cancellationToken);
        }

        public Task<SaveTransferStatus> GetSaveAsync(string sessionId, string requestId, CancellationToken cancellationToken)
        {
            ValidateId(requestId);
            return SendAsync<SaveTransferStatus>(HttpMethod.Get,
                SessionPath(sessionId) + "/save-requests/" + requestId, null, cancellationToken);
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null)
                {
                    request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                }

                using (var response = await client.SendAsync(request, cancellationToken))
                {
                    // Do not include response bodies or authentication values in error messages.
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException("홀인원 Backend 요청 실패: HTTP " + (int)response.StatusCode);
                    }

                    var text = await response.Content.ReadAsStringAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = JsonConvert.DeserializeObject<T>(text);
                    if (result == null)
                    {
                        throw new InvalidDataException("Backend 응답이 비어 있음.");
                    }

                    return result;
                }
            }
        }

        private static string SessionPath(string sessionId)
        {
            ValidateId(sessionId);
            return "v1/holeinone/sessions/" + sessionId;
        }

        internal static void ValidateId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128 ||
                id.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') || c == '-' || c == '_')))
            {
                throw new InvalidDataException("잘못된 플레이 식별자임.");
            }
        }

        internal static Uri ValidateAddress(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidDataException("올바른 Backend HTTP(S) 주소가 필요함.");
            }

            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }

        public void Dispose() => client.Dispose();
    }

    public sealed class CloudSession
    {
        public string SessionId { get; set; }
        public string GameId { get; set; }
        public string StreamUrl { get; set; }
    }

    public sealed class CampfireEvent
    {
        public long Sequence { get; set; }
        public CampfireNotice Notice { get; set; }
    }

    public sealed class CampfireNotice
    {
        public string RunId { get; set; }
    }

    public sealed class SaveTransferStatus
    {
        public string RequestId { get; set; }
        public string RunId { get; set; }
        public string Status { get; set; }
        public TransferTicket Ticket { get; set; }
    }

    public sealed class TransferTicket
    {
        [JsonProperty("backendUrl")]
        public string BackendUrl { get; set; }
        [JsonProperty("sessionId")]
        public string SessionId { get; set; }
        [JsonProperty("requestId")]
        public string RequestId { get; set; }
        [JsonProperty("runId")]
        public string RunId { get; set; }
        [JsonProperty("bearerToken")]
        public string BearerToken { get; set; }
    }
}
