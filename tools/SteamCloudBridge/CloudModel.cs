using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HoleInOne.SteamCloud;

public sealed class CloudFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed record RemoteFile(string Name, string Sha1, long Size, uint Platforms = uint.MaxValue);
public sealed record CloudSnapshot(ulong ChangeNumber, IReadOnlyDictionary<string, RemoteFile> Files);
// Own a snapshot so callers cannot change the content after its hash is cached.
internal sealed class SaveContent(byte[] bytes)
{
    private readonly byte[] bytes = (byte[])bytes.Clone();
    private string? sha1;
    public ReadOnlyMemory<byte> Bytes => bytes;
    public string Sha1 => sha1 ??= Convert.ToHexStringLower(SHA1.HashData(bytes));
}
public sealed record SaveFile
{
    public string Root { get; init; }
    public string Path { get; init; }
    internal SaveContent Content { get; }
    public ReadOnlyMemory<byte> Bytes => Content.Bytes;
    public string Sha1 => Content.Sha1;
    public SaveFile(string root, string path, byte[] bytes)
    { Root = root; Path = path; Content = new SaveContent(bytes); }
}
public sealed record UploadFile
{
    public string Name { get; }
    public uint Platforms { get; }
    private readonly SaveContent content;
    public ReadOnlyMemory<byte> Bytes => content.Bytes;
    public string Sha1 => content.Sha1;
    public UploadFile(string name, byte[] bytes, uint platforms) : this(name, new SaveContent(bytes), platforms) { }
    internal UploadFile(string name, SaveContent content, uint platforms)
    { Name = name; this.content = content; Platforms = platforms; }
}
public sealed record CloudRule(string Root, string Path, string Pattern, bool Recursive, uint Platforms = 1);
public sealed record UploadPlan(IReadOnlyList<UploadFile> Files, int Unchanged, int LocalOnly);

public static class CloudPreparation
{
    public static async Task<(IReadOnlyList<CloudRule> Rules, CloudSnapshot Baseline)> LoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<CloudRule>>> readRules,
        Func<CancellationToken, Task<CloudSnapshot>> readSnapshot, CancellationToken ct)
    {
        var rules = readRules(ct);
        var baseline = readSnapshot(ct);
        // Observe both requests before disposing their shared connection, including on failure.
        await Task.WhenAll(rules, baseline);
        return (await rules, await baseline);
    }
}

public static class SaveBundle
{
    private sealed record Manifest(int Version, string GameId, List<ManifestFile> Files);
    private sealed record ManifestFile(string Entry, string Root, string Path, long Size, string Sha256);
    public const long MaxBytes = 256L * 1024 * 1024;
    public static async Task<IReadOnlyList<SaveFile>> ReadAsync(string path, string expectedHash, uint appId, CancellationToken ct)
    {
        // Hold the archive read-only throughout validation; never resolve archive entries onto disk.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxBytes || !Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$")) throw new CloudFailure("invalid_bundle");
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) != expectedHash) throw new CloudFailure("bundle_hash_mismatch");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var metadata = zip.GetEntry("manifest.json");
        if (metadata == null || metadata.Length > 16 * 1024 * 1024 || zip.Entries.Count > 50001) throw new CloudFailure("invalid_bundle");
        using var json = metadata.Open();
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        if (manifest == null || manifest.Version != 1 || manifest.GameId != "steam-" + appId || manifest.Files == null ||
            manifest.Files.Count == 0 || manifest.Files.Count + 1 != zip.Entries.Count) throw new CloudFailure("invalid_bundle");
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<SaveFile>();
        long total = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (file == null || file.Root == null || file.Path == null || !Regex.IsMatch(file.Entry ?? "", "^files/[0-9]{6}$") ||
                !entries.Add(file.Entry!) || !targets.Add(file.Root + "/" + file.Path) || file.Size < 0 ||
                !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new CloudFailure("invalid_bundle");
            ValidatePath(file.Path.Replace("{steamAccountId}", "1").Replace("{steamId64}", "1"));
            total = checked(total + file.Size);
            if (total > MaxBytes) throw new CloudFailure("bundle_too_large");
            var entry = zip.GetEntry(file.Entry!);
            if (entry == null || entry.Length != file.Size || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new CloudFailure("invalid_bundle");
            using var stream = entry.Open();
            var bytes = new byte[checked((int)file.Size)];
            await stream.ReadExactlyAsync(bytes, ct);
            if (stream.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new CloudFailure("bundle_hash_mismatch");
            files.Add(new(file.Root, file.Path, bytes));
        }
        if (zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count ||
            zip.Entries.Any(e => !entries.Contains(e.FullName))) throw new CloudFailure("invalid_bundle");
        return files;
    }
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Contains('\\') ||
            path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') ||
                p.Any(c => c < 32 || ":*?\"<>|{}".Contains(c)))) throw new CloudFailure("unsafe_path");
    }
}

public static class CloudPlanner
{
    private static readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["roaming"] = "WinAppDataRoaming", ["local"] = "WinAppDataLocal", ["localLow"] = "WinAppDataLocalLow",
        ["documents"] = "WinMyDocuments", ["savedGames"] = "WinSavedGames", ["install"] = "GameInstall"
    };
    public static UploadPlan Create(IReadOnlyList<SaveFile> files, CloudSnapshot baseline, CloudSnapshot current,
        IReadOnlyList<CloudRule> rules, uint appId, ulong steamId)
    {
        var all = new Dictionary<string, UploadFile>(StringComparer.OrdinalIgnoreCase);
        var localOnly = 0;
        foreach (var file in files)
        {
            var mapped = Map(file, current, rules, appId, steamId);
            if (mapped == null) { localOnly++; continue; }
            if (all.TryGetValue(mapped.Name, out var previous) && previous.Sha1 != mapped.Sha1)
                throw new CloudFailure("ambiguous_cloud_path");
            all[mapped.Name] = mapped;
        }
        if (all.Count == 0) throw new CloudFailure("no_cloud_mapping");
        var changed = new List<UploadFile>();
        foreach (var file in all.Values)
        {
            current.Files.TryGetValue(file.Name, out var now);
            if (now?.Sha1 == file.Sha1 && now.Size == file.Bytes.Length) continue;
            baseline.Files.TryGetValue(file.Name, out var before);
            // A remote edit or deletion since session start must never be overwritten silently.
            if (before?.Sha1 != now?.Sha1 || before?.Size != now?.Size) throw new CloudFailure("remote_changed");
            changed.Add(file);
        }
        return new(changed, all.Count - changed.Count, localOnly);
    }
    private static UploadFile? Map(SaveFile file, CloudSnapshot current, IReadOnlyList<CloudRule> rules, uint appId, ulong steamId)
    {
        var account = (steamId - 76561197960265728UL).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = file.Path.Replace("{steamId64}", steamId.ToString()).Replace("{steamAccountId}", account);
        SaveBundle.ValidatePath(path);
        var root = file.Root;
        if (root is "steamUserDataRoot" or "steamUserData")
        {
            var prefix = (root == "steamUserDataRoot" ? account + "/" : "") + appId + "/remote/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new CloudFailure("account_or_app_mismatch");
            var name = path[prefix.Length..];
            SaveBundle.ValidatePath(name);
            if (name.Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase) || name.Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase))
                throw new CloudFailure("steam_metadata_in_bundle");
            current.Files.TryGetValue(name, out var remote);
            return new(name, file.Content, remote?.Platforms ?? uint.MaxValue);
        }
        if (root == "profile")
        {
            foreach (var pair in new[] { ("AppData/Roaming/", "roaming"), ("AppData/LocalLow/", "localLow"),
                ("AppData/Local/", "local"), ("Documents/", "documents"), ("Saved Games/", "savedGames") })
                if (path.StartsWith(pair.Item1, StringComparison.OrdinalIgnoreCase)) { root = pair.Item2; path = path[pair.Item1.Length..]; break; }
        }
        if (!Roots.TryGetValue(root, out var steamRoot)) throw new CloudFailure("unsupported_cloud_root");
        if (path.Split('/').Last().Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase)) return null;
        var candidate = "%" + steamRoot + "%" + path;
        var existing = current.Files.Values.Where(f => Canonical(f.Name).Equals(Canonical(candidate), StringComparison.OrdinalIgnoreCase)).ToList();
        if (existing.Count > 1) throw new CloudFailure("ambiguous_cloud_path");
        if (existing.Count == 1) return new(existing[0].Name, file.Content, existing[0].Platforms);
        var matching = rules.Where(r => r.Root == steamRoot && Matches(r, path, steamId, account)).ToList();
        if (matching.Count == 0) return null; // Export also contains local-only preferences, which must not be uploaded.
        if (matching.Select(r => r.Platforms).Distinct().Count() != 1) throw new CloudFailure("ambiguous_cloud_path");
        return new(candidate, file.Content, matching[0].Platforms);
    }
    private static string Canonical(string name) => Regex.Replace(name, "^(%[^%]+%)/", "$1");
    private static bool Matches(CloudRule rule, string path, ulong steamId, string account)
    {
        var directory = rule.Path.Replace("{64BitSteamID}", steamId.ToString()).Replace("{Steam3AccountID}", account).Replace('\\', '/').Trim('/');
        if (directory == ".") directory = "";
        if (directory.Contains('{') || directory.Contains('*') || directory.Contains('?')) throw new CloudFailure("unsupported_cloud_rule");
        if (directory.Length != 0) SaveBundle.ValidatePath(directory);
        if (directory.Length != 0 && !path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)) return false;
        var tail = directory.Length == 0 ? path : path[(directory.Length + 1)..];
        if (!rule.Recursive && tail.Contains('/')) return false;
        var pattern = rule.Pattern == "*.*" ? "*" : rule.Pattern;
        if (pattern.Contains('/') || pattern.Contains('\\')) throw new CloudFailure("unsupported_cloud_rule");
        return Regex.IsMatch(tail.Split('/').Last(), "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

public interface ICloudTransport
{
    Task<CloudSnapshot> SnapshotAsync(CancellationToken ct);
    Task<ulong> BeginAsync(IReadOnlyList<UploadFile> files, ulong expectedChange, CancellationToken ct);
    Task UploadAsync(ulong batch, UploadFile file, CancellationToken ct);
    Task CompleteAsync(ulong batch, bool success, CancellationToken ct);
    Task EnsureLaunchReadyAsync(CancellationToken ct);
}

public sealed class CloudSyncEngine(ICloudTransport transport)
{
    public async Task<UploadPlan> SyncAsync(IReadOnlyList<SaveFile> files, CloudSnapshot baseline, IReadOnlyList<CloudRule> rules,
        uint appId, ulong steamId, bool preview, CancellationToken ct)
    {
        var current = await transport.SnapshotAsync(ct);
        var plan = CloudPlanner.Create(files, baseline, current, rules, appId, steamId);
        if (preview) return plan;
        if (plan.Files.Count > 0)
        {
            var batch = await transport.BeginAsync(plan.Files, current.ChangeNumber, ct);
            var success = false;
            try
            {
                foreach (var file in plan.Files) await transport.UploadAsync(batch, file, ct);
                success = true;
            }
            finally
            {
                // Cancellation must not leave an upload batch open. Never report a failed batch as successful.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await transport.CompleteAsync(batch, success, cleanup.Token);
            }
        }
        var verified = await transport.SnapshotAsync(ct);
        foreach (var file in plan.Files)
            if (!verified.Files.TryGetValue(file.Name, out var remote) || remote.Sha1 != file.Sha1 || remote.Size != file.Bytes.Length)
                throw new CloudFailure("upload_verification_failed");
        // Recheck unchanged files too: a different device may have edited them during this upload.
        var recheck = CloudPlanner.Create(files, baseline, verified, rules, appId, steamId);
        if (recheck.Files.Count != 0) throw new CloudFailure("upload_verification_failed");
        await transport.EnsureLaunchReadyAsync(ct);
        return plan;
    }
}
