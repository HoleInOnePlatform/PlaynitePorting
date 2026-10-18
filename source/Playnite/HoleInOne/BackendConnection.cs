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
    public sealed class BackendConnection : IDisposable, IPreparedSessionBackend, IDeferredPreparedSessionBackend
    {
        private readonly HttpClient client;

        public async Task CheckDevelopmentHealthAsync(CancellationToken cancellationToken)
        {
            var health = await SendAsync<BackendHealth>(HttpMethod.Get, "health", null, cancellationToken);
            if (health.Status != "ok" || health.Service != "holeinone" || health.ContractVersion != 1
                || health.Authentication != "development")
                throw new InvalidDataException("호환되는 홀인원 개발 서버가 아닙니다. 주소를 확인해 주세요.");
        }

        public async Task<AccountLogin> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            if (client.BaseAddress.Scheme != "https" && !client.BaseAddress.IsLoopback)
                throw new InvalidOperationException("로그인 서버에 안전하게 연결할 수 없습니다.");
            var result = await SendAsync<AccountLogin>(HttpMethod.Post, "v1/holeinone/auth/login",
                new { username, password }, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.AccessToken) || result.AccessToken.Length != 64 ||
                result.AccessToken.Any(c => !Uri.IsHexDigit(c)) || result.ExpiresAt <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(result.Username))
                throw new InvalidDataException("로그인 응답을 확인할 수 없습니다.");
            return result;
        }

        public Task LogoutAsync(CancellationToken ct) => PostEmptyAsync("v1/holeinone/auth/logout", null, ct);

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

        public async Task<PreparedCloudSession> PrepareSessionAsync(string requestId, CancellationToken ct)
        {
            ValidateId(requestId);
            var prepared = await SendAsync<PreparedCloudSession>(HttpMethod.Post, "v1/holeinone/sessions/prepare", new { requestId }, ct).ConfigureAwait(false);
            ValidateId(prepared.SessionId);
            ValidateId(prepared.GameId);
            if (prepared.ExpiresAt <= DateTimeOffset.UtcNow) throw new PreparedSessionExpiredException();
            return prepared;
        }

        public async Task<CloudSession> PlayPreparedSessionAsync(string sessionId, string gameId, CancellationToken ct)
            => await ClaimPreparedSessionAsync(sessionId, gameId, false, ct).ConfigureAwait(false);

        public async Task<CloudSession> ConnectPreparedSessionAsync(string sessionId, string gameId, CancellationToken ct)
            => await ClaimPreparedSessionAsync(sessionId, gameId, true, ct).ConfigureAwait(false);

        public Task AllowPreparedLaunchAsync(string sessionId, CancellationToken ct) =>
            PostEmptyAsync(SessionPath(sessionId) + "/allow-launch", null, ct);

        private async Task<CloudSession> ClaimPreparedSessionAsync(string sessionId, string gameId, bool deferred, CancellationToken ct)
        {
            ValidateId(gameId);
            var session = await SendAsync<CloudSession>(HttpMethod.Post, SessionPath(sessionId) + (deferred ? "/connect-only" : "/play"), new { gameId }, ct).ConfigureAwait(false);
            if (deferred && !session.LaunchDeferred) throw new InvalidDataException("게임 시작 대기를 확인하지 못했습니다.");
            if (session.SessionId != sessionId || session.GameId != gameId) throw new InvalidDataException("요청한 게임과 준비된 세션이 다릅니다.");
            // A local backend may advertise its public HTTPS tunnel for cloud callbacks.
            // Never navigate that advertised origin: use the configured loopback server instead.
            var expected = new Uri(client.BaseAddress, "stream/v5/index.html");
            if (!Uri.TryCreate(session.StreamUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(uri.Fragment, "^#" + sessionId + "/[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("준비된 세션의 연결 주소를 확인할 수 없습니다.");
            if (uri.GetLeftPart(UriPartial.Path) != expected.AbsoluteUri)
            {
                if (!client.BaseAddress.IsLoopback || uri.Scheme != "https" || uri.AbsolutePath != expected.AbsolutePath)
                    throw new InvalidDataException("준비된 세션의 연결 주소를 확인할 수 없습니다.");
                session.StreamUrl = expected.AbsoluteUri + uri.Fragment;
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

        public Task<ExportHandoffStatus> StartExportAsync(string sessionId, CancellationToken ct) =>
            SendAsync<ExportHandoffStatus>(HttpMethod.Post, SessionPath(sessionId) + "/export-handoff", null, ct);

        public Task<ExportHandoffStatus> GetExportAsync(string sessionId, CancellationToken ct) =>
            SendAsync<ExportHandoffStatus>(HttpMethod.Get, SessionPath(sessionId) + "/export-handoff", null, ct);

        public Task DownloadCompleteAsync(string sessionId, CancellationToken ct) =>
            PostEmptyAsync(SessionPath(sessionId) + "/download-complete", null, ct);

        public Task StopCloudAsync(string sessionId, CancellationToken ct) =>
            PostEmptyAsync(SessionPath(sessionId) + "/stop", null, ct);

        public Task ReceiveExportAsync(string sessionId, string requestId, string sha256, CancellationToken ct)
        {
            ValidateId(requestId);
            return PostEmptyAsync(SessionPath(sessionId) + "/export-handoff/" + requestId + "/received", new { sha256 }, ct);
        }

        private async Task PostEmptyAsync(string path, object body, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                if (body != null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        if (path == "v1/holeinone/auth/logout") return; // Already revoked or expired.
                        throw new LoginRequiredException("로그인이 만료되었습니다. 다시 로그인해 주세요.");
                    }
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound && path.EndsWith("/stop", StringComparison.Ordinal)) return;
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException("홀인원 Backend 요청 실패: HTTP " + (int)response.StatusCode);
                }
            }
        }

        public async Task DownloadExportAsync(string sessionId, ExportHandoffStatus status, string path, CancellationToken ct)
        {
            ValidateId(status.RequestId);
            if (status.Status != "ready" || status.Size == null || status.Size < 1 || status.Size > 4L * 1024 * 1024 * 1024 ||
                !System.Text.RegularExpressions.Regex.IsMatch(status.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("잘못된 세이브 전송 정보임.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                using (var response = await client.GetAsync(SessionPath(sessionId) + "/export-handoff/" + status.RequestId + "/file",
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        throw new LoginRequiredException("로그인이 만료되었습니다. 다시 로그인해 주세요.");
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException("세이브 다운로드 실패: HTTP " + (int)response.StatusCode);
                    if (!response.Headers.TryGetValues("X-Content-SHA256", out var hashes) || hashes.SingleOrDefault() != status.Sha256)
                        throw new InvalidDataException("세이브 다운로드 해시가 요청과 다름.");
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buffer = new byte[65536];
                        long written = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) != 0)
                        {
                            written += read;
                            if (written > status.Size.Value) throw new InvalidDataException("세이브 크기 제한을 초과함.");
                            await output.WriteAsync(buffer, 0, read, timeout.Token);
                        }
                        if (written != status.Size.Value) throw new InvalidDataException("세이브 다운로드가 완전하지 않음.");
                    }
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (await Task.Run(() => ExportSaveInstaller.HashFile(path), timeout.Token) != status.Sha256)
                    throw new InvalidDataException("세이브 다운로드 검증 실패.");
            }
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
        {
            int? status = null;
            try
            {
                return await SendCoreAsync<T>(method, path, body, cancellationToken, value => status = value).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (status.HasValue) error.Data["HioHttpStatus"] = status.Value;
                throw;
            }
        }

        // Diagnostic fields are codes only: exception messages can contain URLs or credentials.
        public static string DescribeFailure(Exception error, CancellationToken cancellationToken)
        {
            var result = "exception=" + error.GetType().Name + " http_status=" +
                (error.Data["HioHttpStatus"] is int status ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none");
            if (error is OperationCanceledException)
                result += " reason=" + (cancellationToken.IsCancellationRequested ? "cancelled" : "timeout");
            int depth = 0;
            for (var cause = error; cause != null && depth < 8; cause = cause.InnerException, depth++)
            {
                result += " cause" + depth + "=" + cause.GetType().Name + " hresult" + depth + "=" + cause.HResult;
                if (cause is System.Net.Sockets.SocketException socket)
                    result += " socket_error=" + socket.SocketErrorCode + " native_error=" + socket.NativeErrorCode;
                if (cause is System.Net.WebException web)
                    result += " web_error=" + web.Status;
            }
            return result;
        }

        private async Task<T> SendCoreAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken, Action<int> recordStatus)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null)
                {
                    request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                }

                using (var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    recordStatus((int)response.StatusCode);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound && path.EndsWith("/connect-only", StringComparison.Ordinal))
                        throw new DeferredLaunchUnavailableException();
                    // Do not include response bodies or authentication values in error messages.
                    if (path == "v1/holeinone/sessions/prepare" &&
                        (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.Conflict))
                        throw new PrewarmingUnavailableException();
                    if ((response.StatusCode == System.Net.HttpStatusCode.Gone || (response.StatusCode == System.Net.HttpStatusCode.NotFound && path.EndsWith("/play", StringComparison.Ordinal))) &&
                            (path == "v1/holeinone/sessions/prepare" || path.EndsWith("/play", StringComparison.Ordinal) || path.EndsWith("/connect-only", StringComparison.Ordinal)))
                        throw new PreparedSessionExpiredException();
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            throw new LoginRequiredException(path.EndsWith("/auth/login", StringComparison.Ordinal)
                                ? "아이디 또는 비밀번호가 올바르지 않습니다." : "로그인이 만료되었습니다. 다시 로그인해 주세요.");
                        if ((int)response.StatusCode == 429)
                            throw new HttpRequestException("로그인 시도가 너무 많습니다. 5분 후 다시 시도해 주세요.");
                        if (method == HttpMethod.Post && path == "v1/holeinone/sessions" &&
                            (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                             response.StatusCode == System.Net.HttpStatusCode.Conflict ||
                             response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable))
                        {
                            BackendError details = null;
                            try { details = JsonConvert.DeserializeObject<BackendError>(await response.Content.ReadAsStringAsync().ConfigureAwait(false)); }
                            catch (JsonException) { } // Proxies may return HTML or a non-object JSON response.
                            if (details?.ErrorCode == "game_not_supported" || details?.ErrorCode == "game_not_configured" ||
                                (string.IsNullOrEmpty(details?.ErrorCode) && details?.Error == "This game has no configured cloud application."))
                                throw new CloudPlayUnavailableException("이 게임은 현재 클라우드 플레이를 지원하지 않음. 설치 버튼으로 PC에 설치한 뒤 플레이할 수 있음.");
                            if (details?.ErrorCode == "game_not_ready")
                                throw new CloudPlayUnavailableException("이 게임은 현재 클라우드에서 실행할 수 없는 상태임. 게임 준비를 기다리지 않고 요청을 종료함.");
                            if (details?.ErrorCode == "game_catalog_unavailable")
                                throw new CloudPlayUnavailableException("클라우드 지원 게임 목록을 확인할 수 없음. 서버 연결 및 조회 권한 확인 필요.");
                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                                throw new HttpRequestException("클라우드 플레이 요청 경로를 찾을 수 없음(HTTP 404). 서버 주소와 Backend 버전을 확인해야 함.");
                        }
                        if (path.EndsWith("/export-handoff", StringComparison.Ordinal) && response.StatusCode == System.Net.HttpStatusCode.Conflict)
                        {
                            // Only map known error codes; never surface raw response bodies or secrets.
                            var details = JsonConvert.DeserializeObject<ExportHandoffStatus>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            if (details?.ErrorCode == "export_not_configured") throw new InvalidOperationException("클라우드 세이브 전환 설정이 아직 준비되지 않음.");
                            if (details?.ErrorCode == "baseline_unverified") throw new InvalidOperationException("클라우드의 기존 세이브 구성을 먼저 확인해야 함.");
                            throw new InvalidOperationException("이 게임의 세이브 저장 위치 또는 클라우드 전환 조건을 확인해야 함.");
                        }
                        throw new HttpRequestException("홀인원 Backend 요청 실패: HTTP " + (int)response.StatusCode);
                    }

                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    public sealed class LoginRequiredException : HttpRequestException
    {
        public LoginRequiredException(string message) : base(message) { }
    }

    public sealed class CloudPlayUnavailableException : InvalidOperationException
    {
        public CloudPlayUnavailableException(string message) : base(message) { }
    }

    public sealed class AccountLogin
    {
        public string AccessToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Username { get; set; }
    }

    internal sealed class BackendHealth
    {
        public string Status { get; set; }
        public string Service { get; set; }
        public int ContractVersion { get; set; }
        public string Authentication { get; set; }
    }

    internal sealed class BackendError
    {
        public string Error { get; set; }
        public string ErrorCode { get; set; }
    }

    public sealed class CloudSession
    {
        public string SessionId { get; set; }
        public string GameId { get; set; }
        public string StreamUrl { get; set; }
        public bool LaunchDeferred { get; set; }
    }

    public sealed class ExportHandoffStatus
    {
        public string RequestId { get; set; }
        public string GameId { get; set; }
        public string Status { get; set; }
        public long? Size { get; set; }
        public string Sha256 { get; set; }
        public string ErrorCode { get; set; }
        public string FailureStage { get; set; }
        public string AwsErrorCode { get; set; }

        public string FailureMessage()
        {
            var stage = FailureStage == "resolve_session" ? "클라우드 세션 조회" :
                FailureStage == "resolve_saves" ? "세이브 전환 설정 확인" :
                FailureStage == "schedule_export" ? "파일 내보내기 요청" :
                FailureStage == "terminate_session" ? "클라우드 세션 종료" :
                FailureStage == "wait_export" ? "AWS 파일 생성" :
                FailureStage == "download_export" ? "S3 파일 다운로드" :
                FailureStage == "prepare_saves" ? "세이브 파일 준비" : "세이브 전환";
            var reason = ErrorCode == "export_timeout" ? "처리 시간이 초과됨." :
                ErrorCode == "export_not_configured" ? "클라우드 세이브 전환 설정이 아직 준비되지 않음." :
                ErrorCode == "baseline_unverified" ? "클라우드 세이브 초기 상태 확인이 필요함." :
                ErrorCode == "stream_not_exportable" ? "클라우드 세션이 종료되었거나 세이브를 내보낼 수 없는 상태임." :
                AwsErrorCode == "AccessDenied" || AwsErrorCode == "AccessDeniedException" ? "AWS 접근 권한이 부족함." :
                AwsErrorCode == "NoSuchBucket" ? "S3 버킷을 찾을 수 없음." :
                AwsErrorCode == "NoSuchKey" ? "S3 내보내기 파일을 찾을 수 없음." :
                AwsErrorCode == "ValidationException" ? "AWS 요청 조건을 확인해야 함." : "처리에 실패함.";
            return stage + " 단계에서 " + reason + " Backend 오류 기록 확인 필요. 요청: " + RequestId;
        }
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
