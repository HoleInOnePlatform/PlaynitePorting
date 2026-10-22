using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    // The legacy desktop stays on .NET Framework; the isolated worker hosts current SteamKit2.
    public sealed class SteamCloudBridge : IDisposable
    {
        public static string ExecutablePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "SteamCloud", "HoleInOne.SteamCloud.exe");
        public static string TokenPath => SteamLinkContext.IsConfigured ? SteamLinkContext.TokenPath : Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "SteamCloud", "login.dat");
        public static bool IsLinked => SteamLinkContext.IsConfigured ? SteamLinkContext.HasLocalLogin : File.Exists(TokenPath);
        private readonly Process process;
        private readonly SemaphoreSlim commands = new SemaphoreSlim(1);
        private bool disposed;
        private string steamId;

        public static string ActiveSteamId()
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            var account = value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
            if (account == 0) throw new InvalidOperationException("이 PC의 Steam 프로그램에서 로그인 상태를 확인할 수 없습니다. Steam을 열어 HoleInOne에 연결한 계정으로 로그인해 주세요.");
            return (76561197960265728UL + account).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public SteamCloudBridge()
        {
            if (!File.Exists(ExecutablePath)) throw new InvalidOperationException("Steam 세이브 연동 모듈이 없습니다. 새 배포본을 사용해 주세요.");
            process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath, WorkingDirectory = Path.GetDirectoryName(ExecutablePath),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            } };
            process.Start();
            // Drain diagnostic output without logging it: authentication material must never enter application logs.
            process.ErrorDataReceived += (sender, args) => { };
            process.BeginErrorReadLine();
        }
        public async Task AuthenticateAsync(Action<byte[]> qr, CancellationToken ct)
        {
            steamId = ActiveSteamId();
            var result = await CallAsync(new { operation = "auth", steamId, tokenPath = TokenPath }, "authenticated", qr, ct);
            if ((string)result["steamId"] != steamId || ActiveSteamId() != steamId) throw new InvalidOperationException("연결한 Steam 계정이 현재 계정과 다릅니다.");
        }
        public async Task PrepareAsync(uint appId, CancellationToken ct)
        {
            if (SteamLinkContext.IsConfigured)
            {
                await SteamLinkContext.RefreshAsync(ct);
                if (!SteamLinkContext.HasLocalLogin) throw new SteamLinkException("steam_link_required");
                steamId = SteamLinkContext.Current.SteamId;
            }
            else steamId = ActiveSteamId();
            var result = await CallAsync(new { operation = "session", steamId, appId, tokenPath = TokenPath }, "ready", null, ct);
            if ((string)result["steamId"] != steamId || (uint?)result["appId"] != appId) throw new InvalidDataException("Steam 세이브 연결 응답이 일치하지 않음.");
        }
        public async Task SyncAsync(string archive, string sha256, CancellationToken ct)
        {
            if (ActiveSteamId() != steamId) throw new InvalidOperationException("Steam 계정이 변경되어 전환을 중단함.");
            var result = await CallAsync(new { operation = "sync", archivePath = archive, sha256 }, "complete", null, ct);
            if ((bool?)result["preview"] != false) throw new InvalidDataException("Steam 업로드 완료를 확인하지 못함.");
            if (ActiveSteamId() != steamId) throw new InvalidOperationException("Steam 계정이 변경되어 전환을 중단함.");
        }
        private async Task<JObject> CallAsync(object request, string expected, Action<byte[]> qr, CancellationToken ct)
        {
            await commands.WaitAsync(ct);
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(SteamCloudBridge));
                process.StandardInput.WriteLine(JsonConvert.SerializeObject(request));
                process.StandardInput.Flush();
                var cancelled = new TaskCompletionSource<bool>();
                using (ct.Register(() => cancelled.TrySetResult(true)))
                {
                    while (true)
                    {
                        var read = process.StandardOutput.ReadLineAsync();
                        var winner = await Task.WhenAny(read, cancelled.Task, Task.Delay(TimeSpan.FromSeconds(200)));
                        if (winner != read) { Dispose(); ct.ThrowIfCancellationRequested(); throw new TimeoutException("Steam 세이브 연결 시간이 초과됨."); }
                        var line = await read;
                        if (line == null || line.Length > 1024 * 1024) throw new InvalidDataException("Steam 세이브 연결이 종료됨.");
                        var message = JObject.Parse(line);
                        var type = (string)message["type"];
                        if (type == "error") throw new InvalidOperationException(FailureMessage((string)message["code"]));
                        if (type == expected) return message;
                        if (type == "qr" && qr != null) qr(Convert.FromBase64String((string)message["png"]));
                        else throw new InvalidDataException("예상하지 않은 Steam 세이브 연결 응답임.");
                    }
                }
            }
            finally { commands.Release(); }
        }
        public static string FailureMessage(string code)
        {
            switch (code)
            {
                case "login_required": return "설정에서 Steam 세이브 계정을 다시 연결해 주세요.";
                case "account_mismatch": case "account_or_app_mismatch": return "연결한 Steam 계정 또는 게임이 일치하지 않아 전환을 중단함.";
                case "remote_changed": return "다른 기기에서 Steam 세이브가 변경되어 자동 덮어쓰기를 중단함.";
                case "remote_operation_pending": return "다른 기기의 Steam 동기화가 아직 끝나지 않았습니다. 잠시 후 다시 전환해 주세요.";
                case "unsupported_cloud_rule": case "unsupported_cloud_root": case "no_cloud_mapping": return "이 게임의 Steam 저장 위치는 아직 자동 업로드를 지원하지 않음.";
                case "cloud_quota_exceeded": return "이 게임의 Steam Cloud 저장 공간이 부족함.";
                case "operation_timeout": return "Steam 세이브 처리 시간이 초과됨. 다시 시도해 주세요.";
                default: return "Steam 세이브 동기화를 완료하지 못함. 로컬 실행을 보류함.";
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { process.StandardInput.WriteLine("{\"operation\":\"cancel\"}"); process.StandardInput.Flush(); }
            catch (Exception) { }
            // Allow the worker to close any open batch before terminating an unresponsive process.
            Task.Run(() =>
            {
                try { if (!process.WaitForExit(25000)) process.Kill(); }
                catch (Exception) { }
                finally { process.Dispose(); }
            });
        }
    }
}
