using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    public sealed class SteamLinkInfo
    {
        public string State { get; set; }
        public string SteamId { get; set; }
        public string Version { get; set; }
        public string SessionState { get; set; }
        public bool DeletionPending { get; set; }
        [JsonIgnore] public bool IsLinked => State == "linked";
    }
    public sealed class SteamLinkException : Exception
    {
        public string Code { get; }
        public SteamLinkException(string code) : base(MessageFor(code)) { Code = code; }
        private static string MessageFor(string code)
        {
            switch (code)
            {
                case "steam_session_in_use": return "이 계정은 다른 PC 또는 현재 클라우드에서 사용 중입니다. 이용을 정상 종료한 후 다시 시도해 주세요.";
                case "steam_session_closing": return "준비 세션을 종료하고 있습니다. 잠시 후 다시 시도해 주세요.";
                case "steam_link_required": case "steam_reauthentication_required": return "Steam 연결 또는 재인증이 필요합니다. HoleInOne 설정에서 연결해 주세요.";
                case "steam_link_disabled": case "https_required": return "서버의 안전한 Steam 연결 기능이 아직 준비되지 않았습니다. 로컬 라이브러리는 계속 사용할 수 있습니다.";
                case "steam_validation_unavailable": return "Steam 인증 확인이 지연되고 있습니다. 기존 연결은 유지되며 잠시 후 다시 시도할 수 있습니다.";
                default: return "Steam 연결 요청을 완료하지 못했습니다. 연결 상태를 확인한 뒤 다시 시도해 주세요.";
            }
        }
    }
    public sealed partial class BackendConnection
    {
        public Task<SteamLinkInfo> GetSteamLinkAsync(CancellationToken ct) => SteamLinkRequestAsync(HttpMethod.Get, "", null, ct);
        public Task<SteamLinkInfo> RegisterSteamLinkAsync(string requestId, string accountName, string refreshToken, CancellationToken ct) =>
            SteamLinkRequestAsync(HttpMethod.Put, "", new { requestId, accountName, refreshToken }, ct);
        public async Task DeleteSteamLinkAsync(CancellationToken ct) { await SteamLinkRequestAsync(HttpMethod.Delete, "", null, ct); }
        public async Task ReleaseSteamPreparationAsync(CancellationToken ct) { await SteamLinkRequestAsync(HttpMethod.Post, "/release-preparation", null, ct); }
        private async Task<SteamLinkInfo> SteamLinkRequestAsync(HttpMethod method, string suffix, object body, CancellationToken ct)
        {
            if (client.BaseAddress.Scheme != "https") throw new SteamLinkException("https_required");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(method, "v1/holeinone/steam-link" + suffix))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(method == HttpMethod.Put ? 80 : 30));
                if (body != null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    await RequireSuccessAsync(response, method.Method, "v1/holeinone/steam-link" + suffix).ConfigureAwait(false);
                    if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
                    var result = JsonConvert.DeserializeObject<SteamLinkInfo>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (result == null || (result.State != "linked" && result.State != "unlinked" && result.State != "reauth_required"))
                        throw new InvalidDataException("Steam 연결 상태를 확인할 수 없습니다.");
                    if (result.State != "unlinked")
                    {
                        ValidateId(result.Version);
                        if (!ulong.TryParse(result.SteamId, out var id) || id <= 76561197960265728UL || id > 76561197960265728UL + uint.MaxValue)
                            throw new InvalidDataException("Steam 계정을 확인할 수 없습니다.");
                    }
                    return result;
                }
            }
        }
    }
}
