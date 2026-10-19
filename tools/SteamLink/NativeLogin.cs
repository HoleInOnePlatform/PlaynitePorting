using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HoleInOne.SteamLink;

public static class NativeLogin
{
    public static async Task RunAsync(Credential credential, string steamExe, Action<string> report, CancellationToken ct)
    {
        credential.Validate(DateTimeOffset.UtcNow);
        steamExe = Path.GetFullPath(steamExe);
        if (!File.Exists(steamExe) || !Path.GetFileName(steamExe).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new ProbeFailure("SELECT_STEAM_EXE");
        var root = Path.GetDirectoryName(steamExe)!;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "local.vdf");
        var config = Path.Combine(root, "config", "config.vdf");
        var users = Path.Combine(root, "config", "loginusers.vdf");
        var files = new[] { local, config, users };
        RequireClean(files);
        report("Steam에서 인증 정보의 계정을 확인하고 있습니다.");
        await using (var validation = new SteamAuthentication()) await validation.ValidateAsync(credential, ct);
        ct.ThrowIfCancellationRequested();
        RequireClean(files);

        var token = Encoding.UTF8.GetBytes(credential.RefreshToken);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(token, Encoding.UTF8.GetBytes(credential.AccountName), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(token); }
        var contents = new[] { NativeSteamFiles.LocalVdf(credential, encrypted), NativeSteamFiles.ConfigVdf(credential), NativeSteamFiles.LoginUsersVdf(credential) };
        var written = new List<string>();
        var registryWritten = false;
        var started = false;
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(files[i])!);
                using var file = new FileStream(files[i], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                written.Add(files[i]);
                var bytes = Encoding.UTF8.GetBytes(contents[i]); file.Write(bytes);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam"))
            {
                registryWritten = true;
                key.SetValue("AutoLoginUser", credential.AccountName, RegistryValueKind.String);
                key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
            }
            ct.ThrowIfCancellationRequested();
            using var process = NativeSteamProcess.Start(steamExe);
            if (process == null) throw new ProbeFailure("STEAM_START_FAILED");
            started = true;
            report("Steam 실행 중입니다. 직접 로그인하지 말고 자동 로그인 결과를 기다려 주세요.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(180);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var account = ActiveAccount(steamExe);
                if (account != 0)
                {
                    if (account != credential.AccountId) throw new ProbeFailure("NATIVE_ACCOUNT_MISMATCH");
                    return;
                }
                await Task.Delay(1000, ct);
            }
            throw new ProbeFailure("NATIVE_LOGIN_NOT_CONFIRMED");
        }
        finally
        {
            // Once Steam starts it owns these files. Never race its writes or kill a user's process.
            if (!started)
            {
                foreach (var file in written) File.Delete(file);
                if (registryWritten)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true);
                    key?.DeleteValue("AutoLoginUser", false); key?.DeleteValue("RememberPassword", false);
                }
            }
        }
    }

    private static void RequireClean(string[] files)
    {
        foreach (var process in Process.GetProcessesByName("steam")) { process.Dispose(); throw new ProbeFailure("STEAM_ALREADY_RUNNING"); }
        if (files.Any(File.Exists)) throw new ProbeFailure("CLEAN_TEST_PROFILE_REQUIRED");
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = hive.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("AutoLoginUser") != null || key?.GetValue("RememberPassword") != null)
                throw new ProbeFailure("CLEAN_TEST_PROFILE_REQUIRED");
            using var active = hive.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (active?.GetValue("ActiveUser") is int account && account != 0)
                throw new ProbeFailure("CLEAN_TEST_PROFILE_REQUIRED");
        }
    }

    private static uint ActiveAccount(string steamExe)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = hive.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is not int account || account == 0 || key.GetValue("pid") is not int pid || pid <= 0) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited && string.Equals(process.MainModule?.FileName, steamExe, StringComparison.OrdinalIgnoreCase))
                    return unchecked((uint)account);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return 0;
    }
}
