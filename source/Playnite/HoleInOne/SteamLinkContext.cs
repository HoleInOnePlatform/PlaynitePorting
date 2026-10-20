using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Diagnostics;

namespace Playnite.HoleInOne
{
    // Only public link metadata is readable by the desktop. The worker alone handles DPAPI token material.
    public static class SteamLinkContext
    {
        private static string accountId, address, bearer;
        public static SteamLinkInfo Current { get; private set; }
        public static bool IsConfigured => accountId != null;
        public static string DirectoryPath
        {
            get
            {
                if (!IsConfigured) throw new LoginRequiredException("HoleInOne 로그인이 필요합니다.");
                using (var sha = SHA256.Create())
                    return Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "SteamLinks",
                        BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(address + "\n" + accountId))).Replace("-", "").ToLowerInvariant());
            }
        }
        public static string TokenPath => Path.Combine(DirectoryPath, "login.dat");
        public static bool HasLocalLogin
        {
            get
            {
                if (!IsConfigured || Current?.IsLinked != true || !File.Exists(TokenPath)) return false;
                var path = Path.Combine(DirectoryPath, "link.json");
                try { var saved = File.Exists(path) ? JsonConvert.DeserializeObject<SteamLinkInfo>(File.ReadAllText(path)) : null;
                    return saved?.Version == Current.Version && saved?.SteamId == Current.SteamId; }
                catch (Exception) { return false; }
            }
        }
        public static void Configure(string backendUrl, string owner, string token)
        {
            Forget(); BackendConnection.ValidateId(owner);
            if (BackendConnection.ValidateAddress(backendUrl).Scheme != "https") throw new SteamLinkException("https_required");
            accountId = owner; address = backendUrl; bearer = token;
        }
        public static BackendConnection Backend() => new BackendConnection(address, bearer);
        public static async Task<SteamLinkInfo> RefreshAsync(CancellationToken ct)
        {
            if (!IsConfigured) throw new LoginRequiredException("HoleInOne 로그인이 필요합니다.");
            var captured = accountId;
            using (var backend = Backend())
            {
                var info = await backend.GetSteamLinkAsync(ct).ConfigureAwait(false);
                if (accountId != captured) throw new OperationCanceledException();
                Current = info;
                if (!info.IsLinked) DeleteLocal();
                return info;
            }
        }
        public static void SaveMetadata(SteamLinkInfo info)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "link.json"), JsonConvert.SerializeObject(info));
            Current = info;
        }
        public static void DeleteLocal()
        {
            if (!IsConfigured) return;
            foreach (var file in new[] { TokenPath, Path.Combine(DirectoryPath,"link.json") }) if (File.Exists(file)) File.Delete(file);
        }
        public static void Forget() { accountId = address = bearer = null; Current = null; }
        public static void OpenLocalSteam()
        {
            var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            var executable = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "steam.exe");
            if (executable == null || !File.Exists(executable))
                throw new InvalidOperationException("이 PC에 Steam을 먼저 설치해 주세요. 클라우드 플레이는 계속할 수 있습니다.");
            // Open the PC client without replacing its account or passing authentication material.
            Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = true });
        }
        public static void RequireLocalAccount()
        {
            var expected = Current?.IsLinked == true ? Current.SteamId : null;
            if (expected != null && SteamCloudBridge.ActiveSteamId() != expected)
                throw new InvalidOperationException("PC Steam 계정이 HoleInOne에 연결한 계정과 다릅니다. Steam에서 같은 계정으로 로그인한 후 다시 시도해 주세요.");
        }
    }
}
