using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    public sealed class SteamLinkBridge : IDisposable
    {
        private readonly Process process;
        private bool disposed;
        public static string ExecutablePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Tools","SteamLink","HoleInOne.SteamLink.exe");
        public SteamLinkBridge()
        {
            if (!File.Exists(ExecutablePath)) throw new InvalidOperationException("Steam 연결 모듈이 없습니다. 새 HoleInOne 배포본을 사용해 주세요.");
            process = new Process { StartInfo = new ProcessStartInfo { FileName = ExecutablePath, WorkingDirectory = Path.GetDirectoryName(ExecutablePath),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.Start(); process.ErrorDataReceived += (s,e) => { }; process.BeginErrorReadLine();
        }
        private void Send(object value) { process.StandardInput.WriteLine(JsonConvert.SerializeObject(value)); process.StandardInput.Flush(); }
        private async Task<JObject> ReadAsync(CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read,cancelled.Task,Task.Delay(TimeSpan.FromMinutes(4))) != read)
                { Dispose(); ct.ThrowIfCancellationRequested(); throw new TimeoutException("Steam 연결 시간이 초과되었습니다."); }
                var line = await read;
                if (line == null || line.Length > 4*1024*1024) throw new InvalidDataException("Steam 연결 모듈이 종료되었습니다.");
                var message = JObject.Parse(line);
                if ((string)message["type"] == "error")
                {
                    var code = (string)message["code"];
                    if (code == "cancelled_or_timeout") { ct.ThrowIfCancellationRequested(); throw new TimeoutException("Steam 연결 시간이 초과되었습니다."); }
                    if (code != null && code.StartsWith("REMOTE_", StringComparison.Ordinal))
                        throw new InvalidOperationException(code == "REMOTE_TOKEN_EXCHANGE_FAILED"
                            ? "Steam 설치 요청용 토큰 발급이 거절되었습니다. 인증 만료로 단정할 수 없습니다. %LOCALAPPDATA%\\HoleInOne\\Logs의 steam-debug 로그를 확인해 주세요."
                            : code == "REMOTE_LOCAL_CLIENT_REQUIRED"
                            ? "이 PC의 Steam을 실행하고 연결한 계정으로 로그인해 주세요. 설치·제거 요청으로 Steam 창을 자동 실행하지 않습니다."
                            : code == "REMOTE_CLIENT_NOT_FOUND" || code == "REMOTE_CLIENT_AMBIGUOUS"
                            ? "Steam 서버에서 이 PC를 확인하지 못했습니다. Steam 온라인 상태를 확인한 뒤 다시 시도해 주세요."
                            : "Steam 서버가 설치·제거 요청을 확인하지 못했습니다. Steam 상태를 확인해 주세요. 로컬 실행 명령으로 재시도하지 않습니다.");
                    throw new SteamLinkException(code == "STEAM_LOGON_REJECTED" ? "steam_reauthentication_required" : "steam_validation_unavailable");
                }
                return message;
            }
        }
        public async Task<SteamLinkInfo> ConnectAsync(bool qr, string username, string password,
            Action<byte[]> showQr, Func<bool,bool,Task<string>> guard, CancellationToken ct)
        {
            var generation = SteamLinkContext.Generation;
            Send(new { operation="connect", method=qr?"qr":"password", username, password });
            password = null;
            while (true)
            {
                var message = await ReadAsync(ct);
                var type = (string)message["type"];
                if(type=="qr") { showQr(Convert.FromBase64String((string)message["png"])); continue; }
                if(type=="guard") { var code=await guard((bool)message["email"],(bool)message["incorrect"]); ct.ThrowIfCancellationRequested(); Send(new{operation="guard",code}); continue; }
                if(type!="authenticated") throw new InvalidDataException("잘못된 Steam 인증 응답입니다.");
                var requestId = Guid.NewGuid().ToString("N");
                SteamLinkInfo link = null;
                SteamLinkContext.RequireGeneration(generation);
                using(var backend=SteamLinkContext.Backend())
                {
                    // Repeat only uncertain transport failures, with the SAME request ID and credential.
                    for(var attempt=0;attempt<2;attempt++)
                    {
                        try { link=await backend.RegisterSteamLinkAsync(requestId,(string)message["accountName"],(string)message["refreshToken"],ct); break; }
                        catch(System.Net.Http.HttpRequestException) when(attempt==0 && !ct.IsCancellationRequested) { }
                    }
                }
                message.Remove("refreshToken");
                SteamLinkContext.RequireGeneration(generation);
                if(link?.IsLinked!=true || link.SteamId!=(string)message["steamId"]) throw new InvalidDataException("서버에서 확인한 Steam 계정이 다릅니다.");
                Send(new{operation="commit",steamId=link.SteamId,tokenPath=SteamLinkContext.TokenPath});
                var saved=await ReadAsync(ct);
                SteamLinkContext.RequireGeneration(generation);
                if((string)saved["type"]!="saved") throw new IOException("이 PC의 Steam 연결 저장을 완료하지 못했습니다.");
                SteamLinkContext.SaveMetadata(link);
                return link;
            }
        }
        public async Task<JArray> LibraryAsync(CancellationToken ct)
        {
            var generation = SteamLinkContext.Generation;
            var link = await SteamLinkContext.RefreshAsync(ct).ConfigureAwait(false);
            SteamLinkContext.RequireGeneration(generation);
            link.RequireLibraryConnection(SteamLinkContext.HasLocalLogin);
            var expected=SteamLinkContext.Current.SteamId;
            Send(new{operation="library",steamId=expected,tokenPath=SteamLinkContext.TokenPath});
            var result=await ReadAsync(ct).ConfigureAwait(false);
            SteamLinkContext.RequireGeneration(generation);
            if((string)result["type"]!="library" || (string)result["steamId"]!=expected || !(result["games"] is JArray games))
                throw new InvalidDataException("Steam 라이브러리 응답이 일치하지 않습니다.");
            return games;
        }
        public async Task RequestDownloadAsync(uint appId, bool uninstall, CancellationToken ct)
        {
            if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
            var generation = SteamLinkContext.Generation;
            await SteamLinkContext.RefreshAsync(ct).ConfigureAwait(false);
            SteamLinkContext.RequireGeneration(generation);
            if (!SteamLinkContext.HasLocalLogin) throw new SteamLinkException("steam_link_required");
            var expected = SteamLinkContext.Current.SteamId;
            if (LocalSteamAccount.RequireSteamId() != expected)
                throw new InvalidOperationException("PC Steam 계정이 HoleInOne에 연결한 계정과 다릅니다.");
            var operation = uninstall ? "uninstall" : "install";
            ct.ThrowIfCancellationRequested();
            Send(new { operation, appId, steamId = expected, tokenPath = SteamLinkContext.TokenPath });
            var result = await ReadAsync(ct).ConfigureAwait(false);
            SteamLinkContext.RequireGeneration(generation);
            if ((string)result["type"] != "accepted" || (string)result["steamId"] != expected ||
                (uint?)result["appId"] != appId || (string)result["operation"] != operation)
                throw new InvalidDataException("Steam 설치·제거 응답이 요청과 일치하지 않습니다.");
        }
        public void Dispose()
        {
            if(disposed)return; disposed=true;
            try { Send(new{operation="cancel"}); } catch(Exception) { }
            Task.Run(()=>{ try { if(!process.WaitForExit(5000))process.Kill(); } catch(Exception) { } finally { process.Dispose(); } });
        }
    }
}
