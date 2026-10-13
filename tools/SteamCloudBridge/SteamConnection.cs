using System.Security.Cryptography;
using System.Text.Json;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace HoleInOne.SteamCloud;

public sealed record SavedLogin(string AccountName, string RefreshToken, ulong SteamId);
public static class LoginStore
{
    public static SavedLogin Load(string path)
    {
        if (!File.Exists(path)) throw new CloudFailure("login_required");
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SavedLogin>(plain) ?? throw new CloudFailure("login_required"); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { throw new CloudFailure("login_required"); }
    }
    public static void Save(string path, SavedLogin login)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(login);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public sealed class SteamConnection : IAsyncDisposable
{
    public SteamClient Client { get; } = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CallbackManager callbacks;
    private readonly SteamUser user;
    private readonly Task pump;
    public SteamConnection()
    {
        callbacks = new(Client);
        user = Client.GetHandler<SteamUser>()!;
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            connected.TrySetException(new CloudFailure("steam_disconnected"));
            loggedOn.TrySetException(new CloudFailure("steam_disconnected"));
        });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(r => loggedOn.TrySetResult(r));
        pump = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        });
    }
    public async Task LoginAsync(string tokenPath, ulong expectedId, bool interactive, Action<object> emit, CancellationToken ct)
    {
        Client.Connect();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
        SavedLogin login;
        if (interactive)
        {
            var auth = await Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            { DeviceFriendlyName = "HoleInOne Save Sync", IsPersistentSession = true }).WaitAsync(ct);
            void Draw()
            {
                using var data = QRCodeGenerator.GenerateQrCode(auth.ChallengeURL, QRCodeGenerator.ECCLevel.M);
                using var png = new PngByteQRCode(data);
                emit(new { type = "qr", png = Convert.ToBase64String(png.GetGraphic(6)) });
            }
            auth.ChallengeURLChanged = Draw;
            Draw();
            var result = await auth.PollingWaitForResultAsync(ct);
            login = new(result.AccountName, result.RefreshToken, 0);
        }
        else
        {
            login = LoginStore.Load(tokenPath);
            if (login.SteamId != expectedId) throw new CloudFailure("account_mismatch");
        }
        // A distinct login ID avoids replacing the desktop Steam session on the same public IP.
        user.LogOn(new SteamUser.LogOnDetails
        {
            Username = login.AccountName, AccessToken = login.RefreshToken, ShouldRememberPassword = true,
            LoginID = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)), MachineName = "HoleInOne Save Sync"
        });
        var response = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
        if (response.Result != EResult.OK) throw new CloudFailure("login_required");
        var actualId = Client.SteamID?.ConvertToUInt64() ?? 0;
        if (actualId != expectedId) throw new CloudFailure("account_mismatch");
        if (interactive) LoginStore.Save(tokenPath, login with { SteamId = actualId });
    }
    public async ValueTask DisposeAsync()
    {
        user.LogOff(); Client.Disconnect(); stop.Cancel();
        await pump; stop.Dispose();
    }
}

public sealed class SteamCloudTransport : ICloudTransport, IDisposable
{
    private readonly SteamConnection connection;
    private readonly Cloud cloud;
    private readonly uint appId;
    private readonly ulong clientId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private ulong buildId;
    public SteamCloudTransport(SteamConnection connection, uint appId)
    {
        this.connection = connection; this.appId = appId;
        cloud = connection.Client.GetHandler<SteamUnifiedMessages>()!.CreateService<Cloud>();
    }
    private static async Task<T> Receive<T>(AsyncJob<SteamUnifiedMessages.ServiceMethodResponse<T>> job, CancellationToken ct)
        where T : ProtoBuf.IExtensible, new()
    {
        var response = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(25), ct);
        if (response.Result != EResult.OK) throw new CloudFailure("steam_request_failed_" + (int)response.Result);
        return response.Body;
    }
    public async Task<IReadOnlyList<CloudRule>> RulesAsync(CancellationToken ct)
    {
        var apps = connection.Client.GetHandler<SteamApps>()!;
        var tokens = await apps.PICSGetAccessTokens(appId, null).ToTask().WaitAsync(ct);
        tokens.AppTokens.TryGetValue(appId, out var token);
        var product = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(appId, token), null).ToTask().WaitAsync(ct);
        var info = product.Results?.SelectMany(r => r.Apps).Where(r => r.Key == appId).Select(r => r.Value).FirstOrDefault();
        if (info == null || info.MissingToken) throw new CloudFailure("app_metadata_unavailable");
        var ufs = info.KeyValues["ufs"];
        // A shared-cloud app or platform root override requires an explicit validated mapping; never guess it.
        if (ufs["sharecloud"].AsUnsignedInteger() != 0 || ufs["rootoverrides"].Children.Count != 0)
            throw new CloudFailure("unsupported_cloud_rule");
        var rules = new List<CloudRule>();
        foreach (var rule in ufs["savefiles"].Children)
        {
            var os = rule["os"].Value ?? "";
            if (os != "" && os != "all" && os != "Windows" && os != "windows") continue;
            var root = rule["root"].Value ?? "";
            if (root == "AppInstallDirectory") root = "GameInstall";
            if (!new[] { "WinAppDataRoaming", "WinAppDataLocal", "WinAppDataLocalLow", "WinMyDocuments", "WinSavedGames", "GameInstall" }.Contains(root))
                throw new CloudFailure("unsupported_cloud_rule");
            var path = rule["path"].Value ?? "";
            var pattern = rule["pattern"].Value ?? "";
            if (pattern.Length == 0) throw new CloudFailure("unsupported_cloud_rule");
            rules.Add(new(root, path, pattern, rule["recursive"].AsBoolean(), os == "all" ? uint.MaxValue : 1));
        }
        return rules;
    }
    public async Task<CloudSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var response = await Receive(cloud.GetAppFileChangelist(new() { appid = appId, synced_change_number = 0 }), ct);
        if (response.is_only_delta) throw new CloudFailure("incomplete_cloud_listing");
        buildId = response.app_buildid_hwm;
        var files = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in response.files)
        {
            if (file.persist_state != ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted) continue;
            string prefix = "";
            if (file.ShouldSerializepath_prefix_index())
            {
                if (file.path_prefix_index >= response.path_prefixes.Count) throw new CloudFailure("invalid_cloud_listing");
                prefix = response.path_prefixes[(int)file.path_prefix_index];
            }
            // Prefixes supplied by Steam include their required separators/root tokens.
            var name = prefix + (prefix.Length == 0 || prefix.EndsWith('/') || prefix.EndsWith('%') ? "" : "/") + file.file_name;
            SaveBundle.ValidatePath(name);
            if (file.sha_file.Length != 20 || !files.TryAdd(name, new(name, Convert.ToHexStringLower(file.sha_file), file.raw_file_size, file.platforms_to_sync)))
                throw new CloudFailure("invalid_cloud_listing");
        }
        return new(response.current_change_number, files);
    }
    public async Task<ulong> BeginAsync(IReadOnlyList<UploadFile> files, ulong expectedChange, CancellationToken ct)
    {
        var latest = await SnapshotAsync(ct);
        if (latest.ChangeNumber != expectedChange) throw new CloudFailure("remote_changed");
        var quota = await Receive(cloud.ClientGetAppQuotaUsage(new() { appid = appId }), ct);
        var newCount = latest.Files.Count + files.Count(f => !latest.Files.ContainsKey(f.Name));
        var newSize = latest.Files.Values.Sum(f => f.Size) + files.Sum(f => (long)f.Bytes.Length - (latest.Files.TryGetValue(f.Name, out var old) ? old.Size : 0));
        if ((ulong)newCount > quota.max_num_files || (ulong)newSize > quota.max_num_bytes) throw new CloudFailure("cloud_quota_exceeded");
        var request = new CCloud_BeginAppUploadBatch_Request { appid = appId, client_id = clientId, machine_name = "HoleInOne Save Sync", app_build_id = buildId };
        request.files_to_upload.AddRange(files.Select(f => f.Name));
        var response = await Receive(cloud.BeginAppUploadBatch(request), ct);
        if (response.batch_id == 0) throw new CloudFailure("invalid_upload_batch");
        return response.batch_id;
    }
    public async Task UploadAsync(ulong batch, UploadFile file, CancellationToken ct)
    {
        var sha = Convert.FromHexString(file.Sha1);
        var begin = await Receive(cloud.ClientBeginFileUpload(new()
        {
            appid = appId, filename = file.Name, file_size = checked((uint)file.Bytes.Length), raw_file_size = checked((uint)file.Bytes.Length),
            file_sha = sha, upload_batch_id = batch, time_stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            platforms_to_sync = file.Platforms, can_encrypt = false
        }), ct);
        var success = false;
        try
        {
            if (begin.encrypt_file || begin.block_requests.Count == 0) throw new CloudFailure("unsupported_upload_protocol");
            foreach (var block in begin.block_requests)
            {
                if (!block.use_https || !Uri.TryCreate("https://" + block.url_host + block.url_path, UriKind.Absolute, out var uri) ||
                    !IsStorageHost(uri) || block.http_method is not (3 or 4) || block.block_offset > (ulong)file.Bytes.Length ||
                    block.block_length > (ulong)file.Bytes.Length - block.block_offset) throw new CloudFailure("unsupported_upload_protocol");
                var body = block.explicit_body_data.Length > 0 ? block.explicit_body_data : file.Bytes.Span.Slice((int)block.block_offset, (int)block.block_length).ToArray();
                using var request = new HttpRequestMessage(block.http_method == 4 ? HttpMethod.Put : HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
                foreach (var header in block.request_headers)
                {
                    if (header.name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                        header.name.Equals("Host", StringComparison.OrdinalIgnoreCase)) throw new CloudFailure("unsupported_upload_protocol");
                    if (!request.Headers.TryAddWithoutValidation(header.name, header.value)) request.Content.Headers.TryAddWithoutValidation(header.name, header.value);
                }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) throw new CloudFailure("file_upload_failed");
            }
            success = true;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var committed = await Receive(cloud.ClientCommitFileUpload(new()
            { appid = appId, filename = file.Name, file_sha = sha, transfer_succeeded = success }), cleanup.Token);
            if (success && !committed.file_committed) throw new CloudFailure("file_commit_failed");
        }
    }
    public static bool IsStorageHost(Uri uri)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.IdnHost;
        return new[] { ".steamcontent.com", ".steampowered.com", ".blob.core.windows.net", ".storage.googleapis.com", ".amazonaws.com" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
    public async Task CompleteAsync(ulong batch, bool success, CancellationToken ct) =>
        _ = await Receive(cloud.CompleteAppUploadBatchBlocking(new() { appid = appId, batch_id = batch, batch_eresult = success ? 1U : 2U }), ct);

    public async Task EnsureLaunchReadyAsync(CancellationToken ct)
    {
        // Check using our own identity only. Never impersonate the cloud PC or clear its pending operation.
        try
        {
            var intent = await Receive(cloud.SignalAppLaunchIntent(new()
            { appid = appId, client_id = clientId, machine_name = "HoleInOne Save Sync", ignore_pending_operations = false, os_type = (int)EOSType.Win11 }), ct);
            if (intent.pending_remote_operations.Any(p => p.client_id != clientId && p.operation != ECloudPendingRemoteOperation.k_ECloudPendingRemoteOperationNone))
                throw new CloudFailure("remote_operation_pending");
        }
        finally
        {
            cloud.SignalAppExitSyncDone(new() { appid = appId, client_id = clientId, uploads_completed = true, uploads_required = false });
        }
    }
    public void Dispose() => http.Dispose();
}
