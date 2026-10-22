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
    public sealed partial class BackendConnection : IDisposable, IPreparedSessionBackend
    {
        private readonly HttpClient client;

        public async Task CheckDevelopmentHealthAsync(CancellationToken cancellationToken)
        {
            var health = await SendAsync<BackendHealth>(HttpMethod.Get, "health", null, cancellationToken);
            if (health.Status != "ok" || health.Service != "holeinone" || health.ContractVersion != 2
                || health.Authentication != "accounts")
                throw new InvalidDataException("서버와 앱의 계약 버전이 일치하지 않습니다. 서비스 주소와 앱 업데이트를 확인해 주세요.");
        }

        public async Task<AccountLogin> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            if (client.BaseAddress.Scheme != "https" && !client.BaseAddress.IsLoopback)
                throw new InvalidOperationException("로그인 서버에 안전하게 연결할 수 없습니다.");
            var result = await SendAsync<AccountLogin>(HttpMethod.Post, "v1/holeinone/auth/login",
                new { username, password }, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.AccessToken) || result.AccessToken.Length != 64 ||
                result.AccessToken.Any(c => !Uri.IsHexDigit(c)) || result.ExpiresAt <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(result.AccountId) || string.IsNullOrWhiteSpace(result.Username))
                throw new InvalidDataException("로그인 응답을 확인할 수 없습니다.");
            return result;
        }

        public async Task<AccountLogin> RegisterAsync(string username, string password, CancellationToken ct)
        {
            if (client.BaseAddress.Scheme != "https" && !client.BaseAddress.IsLoopback)
                throw new InvalidOperationException("회원가입 서버에 안전하게 연결할 수 없습니다.");
            var result = await SendAsync<AccountLogin>(HttpMethod.Post, "v1/holeinone/auth/register", new { username, password }, ct);
            if (string.IsNullOrWhiteSpace(result.AccessToken) || result.AccessToken.Length != 64 ||
                result.AccessToken.Any(c => !Uri.IsHexDigit(c)) || result.ExpiresAt <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(result.AccountId) || string.IsNullOrWhiteSpace(result.Username))
                throw new InvalidDataException("회원가입 응답을 확인할 수 없습니다.");
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
            client.Timeout = Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
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
        {
            ValidateId(gameId);
            var session = await SendAsync<CloudSession>(HttpMethod.Post, SessionPath(sessionId) + "/play", new { gameId }, ct).ConfigureAwait(false);
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

        public Task<ExportHandoffStatus> StartExportAsync(string sessionId, CancellationToken ct) =>
            RecoverHandoffAsync(sessionId, true, ct);

        public Task<ExportHandoffStatus> GetExportAsync(string sessionId, CancellationToken ct) =>
            RecoverHandoffAsync(sessionId, false, ct);
        public Task<PendingSaveRecovery[]> GetRecoveriesAsync(CancellationToken ct) =>
            SendAsync<PendingSaveRecovery[]>(HttpMethod.Get, "v1/holeinone/recovery", null, ct);

        private async Task<ExportHandoffStatus> RecoverHandoffAsync(string sessionId, bool start, CancellationToken ct)
        {
            var path = SessionPath(sessionId) + "/export-handoff";
            var submit = start;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(75));
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                        {
                            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                            return await SendAsync<ExportHandoffStatus>(submit ? HttpMethod.Post : HttpMethod.Get,
                                path, null, requestTimeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (BackendRequestException error) when (start && !submit && error.Status == 404)
                    {
                        // A lost POST response may still have started an export. Only submit again
                        // when the server confirms that no transfer is available.
                        submit = true;
                    }
                    catch (Exception error) when (IsTransientHandoffFailure(error, ct))
                    {
                        // Recover the existing request, including a failed one. Never silently
                        // restart a completed/failed export after losing its POST response.
                        submit = false;
                    }
                    if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                    if (attempt >= 10 || deadline.IsCancellationRequested)
                        throw new HttpRequestException("세이브 전환 서버 연결이 일시적으로 끊겼습니다. 창을 닫지 말고 잠시 후 전환을 다시 시도해 주세요. 로컬 세이브는 변경하지 않았습니다.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt))), deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new HttpRequestException("세이브 전환 서버에 다시 연결하지 못했습니다. 창을 유지한 채 잠시 후 전환을 다시 시도해 주세요.");
                    }
                }
            }
        }

        private static bool IsTransientHandoffFailure(Exception error, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || error is LoginRequiredException) return false;
            if (error is BackendRequestException response)
                return response.Status == 502 || response.Status == 503 || response.Status == 504;
            return error is HttpRequestException || error is OperationCanceledException;
        }

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
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                if (body != null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        if (path == "v1/holeinone/auth/logout") return; // Already revoked or expired.
                        throw new LoginRequiredException("로그인이 만료되었습니다. 다시 로그인해 주세요.");
                    }
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound && path.EndsWith("/stop", StringComparison.Ordinal)) return;
                    await RequireSuccessAsync(response, "POST", path).ConfigureAwait(false);
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
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var seconds = path == "health" ? 12 : path.Contains("/auth/") ? 30 :
                        path.EndsWith("/prepare", StringComparison.Ordinal) || path.EndsWith("/play", StringComparison.Ordinal) ? 40 : 20;
                    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                    return await SendCoreAsync<T>(method, path, body, timeout.Token, value => status = value).ConfigureAwait(false);
                }
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
                    await RequireSuccessAsync(response, method.Method, path).ConfigureAwait(false);
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

        private static async Task RequireSuccessAsync(HttpResponseMessage response, string method, string path)
        {
            if (response.IsSuccessStatusCode) return;
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            string code = null;
            try { if (text.Length < 4096) code = JsonConvert.DeserializeObject<BackendError>(text)?.ErrorCode; }
            catch (JsonException) { } // Proxies can return HTML or non-object JSON.
            if (status == 401)
                throw new LoginRequiredException(path.EndsWith("/auth/login", StringComparison.Ordinal)
                    ? "아이디 또는 비밀번호가 올바르지 않습니다." : "로그인이 만료되었습니다. 다시 로그인해 주세요.");
            if (code != null && (code.StartsWith("steam_", StringComparison.Ordinal) || code == "https_required"))
                throw new SteamLinkException(code);
            if (status == 429)
                throw new BackendRequestException(status, method, path, code, code == "login_rate_limited"
                    ? "로그인 시도가 너무 많습니다. 5분 후 다시 시도해 주세요."
                    : "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.");
            if (path == "v1/holeinone/auth/register" && status < 500)
                throw new LoginRequiredException(status == 409 ? "이미 사용 중인 아이디입니다. 다른 아이디를 입력하거나 로그인해 주세요."
                    : status == 400 ? "아이디는 영문·숫자·밑줄·하이픈 3~32자, 비밀번호는 8~256자로 입력해 주세요." : "현재 회원가입을 이용할 수 없습니다.");
            var preparation = path == "v1/holeinone/sessions/prepare";
            var play = path.EndsWith("/play", StringComparison.Ordinal);
            if ((preparation || play) && (status == 410 || (play && status == 404)))
                throw new PreparedSessionExpiredException();
            if (preparation && status == 404) throw new PrewarmingUnavailableException();
            if ((preparation || play) && (code == "prepared_game_mismatch" || code == "game_not_supported" || code == "game_not_configured"))
                throw new CloudPlayUnavailableException("이 게임은 현재 클라우드 플레이를 지원하지 않습니다. PC에 설치한 뒤 플레이해 주세요.");
            if (path.EndsWith("/export-handoff", StringComparison.Ordinal) && status == 409)
                throw new BackendRequestException(status, method, path, code, code == "baseline_unverified"
                    ? "클라우드의 기존 세이브 구성을 먼저 확인해야 합니다."
                    : "이 게임의 세이브 저장 위치 또는 클라우드 전환 조건을 확인해 주세요.");
            throw new BackendRequestException(status, method, path, code);
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
        public string AccountId { get; set; }
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

    public sealed class BackendRequestException : HttpRequestException
    {
        public int Status { get; }
        public string ErrorCode { get; }
        public BackendRequestException(int status, string method, string path, string code = null, string message = null)
            : base(message ?? "홀인원 Backend 요청 실패: HTTP " + status)
        { Status = status; ErrorCode = code; Data["HioHttpStatus"] = status; Data["Method"] = method; Data["Path"] = path; }
    }

    public sealed class PendingSaveRecovery
    {
        public string SessionId { get; set; }
        public ExportHandoffStatus Transfer { get; set; }
    }
    public sealed class CloudSession
    {
        public string SessionId { get; set; }
        public string GameId { get; set; }
        public string StreamUrl { get; set; }
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
                ErrorCode == "recovery_required" ? "서버 재시작 전 작업의 복구 확인이 필요함. 새 내보내기를 시작하지 않았음." :
                ErrorCode == "service_stopping" ? "서버가 종료되어 전환이 중단됨. 다시 로그인한 뒤 기존 요청을 확인해야 함." :
                ErrorCode == "export_already_reserved" ? "이미 예약된 파일 내보내기의 저장 위치를 확인할 수 없어 전환을 중단함. 반복 시도 대신 세이브 복구가 필요함." :
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

}
