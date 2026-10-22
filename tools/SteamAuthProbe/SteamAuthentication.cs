using System.Security.Cryptography;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;

namespace HoleInOne.SteamAuthProbe;

public sealed class ProbeFailure(string code) : Exception(code);
public sealed record GuardPrompt(bool Email, bool Incorrect);

public sealed class SteamAuthentication : IAsyncDisposable
{
    private readonly SteamClient client = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SteamUser user;
    private readonly Task pump;

    public SteamAuthentication()
    {
        var callbacks = new CallbackManager(client);
        user = client.GetHandler<SteamUser>()!;
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => {
            connected.TrySetException(new ProbeFailure("STEAM_DISCONNECTED"));
            loggedOn.TrySetException(new ProbeFailure("STEAM_DISCONNECTED"));
        });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(value => loggedOn.TrySetResult(value));
        pump = Task.Run(() => { while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); });
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        client.Connect();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    public async Task<Credential> AuthenticateAsync(bool qr, string username, string password,
        Action<byte[]> showQr, Func<GuardPrompt, Task<string>> requestCode, CancellationToken ct)
    {
        await ConnectAsync(ct);
        AuthPollResult result;
        var details = new AuthSessionDetails { DeviceFriendlyName = "HoleInOne Login Compatibility Test", IsPersistentSession = true };
        if (qr)
        {
            var session = await client.Authentication.BeginAuthSessionViaQRAsync(details).WaitAsync(ct);
            void Draw()
            {
                using var data = QRCodeGenerator.GenerateQrCode(session.ChallengeURL, QRCodeGenerator.ECCLevel.M);
                using var png = new PngByteQRCode(data);
                showQr(png.GetGraphic(5));
            }
            session.ChallengeURLChanged = Draw;
            Draw();
            try { result = await session.PollingWaitForResultAsync(ct); }
            finally { session.ChallengeURLChanged = null; }
        }
        else
        {
            details.Username = username; details.Password = password;
            details.Authenticator = new GuardAuthenticator(requestCode, ct);
            CredentialsAuthSession session;
            try { session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(details).WaitAsync(ct); }
            finally { details.Password = null; }
            result = await session.PollingWaitForResultAsync(ct);
        }
        var id = await LogOnAsync(result.AccountName, result.RefreshToken, ct);
        var credential = new Credential { AccountName = result.AccountName, SteamId = id,
            RefreshToken = result.RefreshToken, AuthenticatedAt = DateTimeOffset.UtcNow };
        credential.Validate(DateTimeOffset.UtcNow);
        return credential;
    }

    public async Task ValidateAsync(Credential credential, CancellationToken ct)
    {
        credential.Validate(DateTimeOffset.UtcNow);
        await ConnectAsync(ct);
        if (await LogOnAsync(credential.AccountName, credential.RefreshToken, ct) != credential.SteamId)
            throw new ProbeFailure("STEAM_ACCOUNT_MISMATCH");
    }

    private async Task<ulong> LogOnAsync(string account, string token, CancellationToken ct)
    {
        user.LogOn(new SteamUser.LogOnDetails { Username = account, AccessToken = token, ShouldRememberPassword = true,
            LoginID = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)), MachineName = "HoleInOne Auth Probe" });
        var response = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (response.Result != EResult.OK) throw new ProbeFailure("STEAM_LOGON_REJECTED");
        return client.SteamID?.ConvertToUInt64() ?? throw new ProbeFailure("STEAM_ID_MISSING");
    }

    public async ValueTask DisposeAsync()
    {
        user.LogOff(); client.Disconnect(); stop.Cancel();
        await pump;
        stop.Dispose();
    }

    private sealed class GuardAuthenticator(Func<GuardPrompt, Task<string>> request, CancellationToken ct) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => request(new(false, previousCodeWasIncorrect)).WaitAsync(ct);
        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => request(new(true, previousCodeWasIncorrect)).WaitAsync(ct);
        // Password mode explicitly asks for Guard codes; QR mode handles mobile approval.
        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);
    }
}
