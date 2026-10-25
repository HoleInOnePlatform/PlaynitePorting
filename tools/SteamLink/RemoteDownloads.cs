using Microsoft.Win32;
using SteamKit2;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;

namespace HoleInOne.SteamLink;

// The linked credential stays inside the worker. No native Steam process is launched here.
internal static class RemoteDownloads
{
    internal static async Task RunAsync(Credential credential, uint appId, bool uninstall, CancellationToken ct)
    {
        SteamDiagnostics.Write(uninstall ? "uninstall" : "install", "started");
        if (appId == 0) throw new ProbeFailure("REMOTE_INVALID_APP");
        credential.Validate(DateTimeOffset.UtcNow);
        RequireLocalClient(credential.SteamId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var client = new SteamClient();
        var callbacks = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onConnect = callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        using var onLogon = callbacks.Subscribe<SteamUser.LoggedOnCallback>(reply => loggedOn.TrySetResult(reply));
        using var onDisconnect = callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            connected.TrySetException(new ProbeFailure("REMOTE_DISCONNECTED"));
            loggedOn.TrySetException(new ProbeFailure("REMOTE_DISCONNECTED"));
        });
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(() => { while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); });
        try
        {
            SteamDiagnostics.Write("connect", "started");
            client.Connect();
            await connected.Task.WaitAsync(deadline.Token);
            SteamDiagnostics.Write("connect", "ok");
            // Client refresh tokens require an authenticated CM session for this API.
            // Use a distinct login ID so the helper does not reuse the native client's login session.
            SteamDiagnostics.Write("LogOn", "started");
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = credential.AccountName,
                AccessToken = credential.RefreshToken,
                ShouldRememberPassword = false,
                LoginID = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)),
                MachineName = "HoleInOne-InstallHelper"
            });
            var login = await loggedOn.Task.WaitAsync(deadline.Token);
            SteamDiagnostics.Write("LogOn", "response", (int)login.Result);
            if (login.Result != EResult.OK)
                throw new ProbeFailure(login.Result is EResult.InvalidPassword or EResult.Expired or EResult.AccessDenied
                    ? "STEAM_LOGON_REJECTED" : "REMOTE_LOGON_UNAVAILABLE");
            if (client.SteamID?.ConvertToUInt64() != credential.SteamId)
                throw new ProbeFailure("REMOTE_ACCOUNT_MISMATCH");
            SteamDiagnostics.Write("GenerateAccessTokenForApp", "started");
            var access = await client.Authentication.GenerateAccessTokenForAppAsync(new SteamID(credential.SteamId),
                credential.RefreshToken, allowRenewal: false).WaitAsync(deadline.Token);
            SteamDiagnostics.Write("GenerateAccessTokenForApp", "ok");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remote = new RemoteDownloadApi(http, access.AccessToken);
            await remote.RequestAsync(appId, uninstall, Environment.MachineName, addresses,
                () => RequireLocalClient(credential.SteamId), deadline.Token);
        }
        catch (SteamKit2.Authentication.AuthenticationException error)
        {
            SteamDiagnostics.Write("GenerateAccessTokenForApp", "authentication_failed", (int)error.Result);
            // An access-token exchange failure does not prove the saved login is expired.
            throw new ProbeFailure("REMOTE_TOKEN_EXCHANGE_FAILED");
        }
        finally
        {
            await RemoteSessionCleanup.RunAsync(user.LogOff, () => client.Disconnect(), stop.Cancel, pump);
        }
    }

    private static void RequireLocalClient(ulong steamId)
    {
        var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
        var account = value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
        var pid = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "pid", null);
        if (account == 0 || 76561197960265728UL + account != steamId || pid is not int processId || processId <= 0)
            throw new ProbeFailure("REMOTE_LOCAL_CLIENT_REQUIRED");
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || !process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                throw new ProbeFailure("REMOTE_LOCAL_CLIENT_REQUIRED");
        }
        catch (ArgumentException) { throw new ProbeFailure("REMOTE_LOCAL_CLIENT_REQUIRED"); }
    }
}
