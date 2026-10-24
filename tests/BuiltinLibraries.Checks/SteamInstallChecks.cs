using SteamLibrary.Services;
using Playnite.HoleInOne;
using Playnite.SDK.Models;
using Newtonsoft.Json;
using System;
using System.IO;

internal static class SteamInstallChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "HoleInOne-SteamInstallChecks-" + Guid.NewGuid());
        try
        {
            var first = Path.Combine(root, "First");
            var second = Path.Combine(root, "Second");
            Directory.CreateDirectory(Path.Combine(first, "steamapps"));
            Directory.CreateDirectory(Path.Combine(second, "steamapps"));
            var folders = new[] { first, second };
            var empty = SteamInstallService.CaptureManifests(42, folders);
            Check(!SteamInstallService.HasManifestChanged(empty, empty), "no manifest does not accept request");
            var manifest = Path.Combine(second, "steamapps", "appmanifest_42.acf");
            File.WriteAllText(manifest, "pending");
            var created = SteamInstallService.CaptureManifests(42, folders);
            Check(SteamInstallService.HasManifestChanged(empty, created), "new manifest detected wherever Steam installs");
            Check(!SteamInstallService.HasManifestChanged(created, SteamInstallService.CaptureManifests(42, folders)), "unchanged manifest does not accept request");
            File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow.AddMinutes(-5));
            Check(SteamInstallService.HasManifestChanged(created, SteamInstallService.CaptureManifests(42, folders)), "existing manifest update accepts request");
            Check(SteamInstallService.CaptureManifests(99, folders).Count == 0, "other game manifests ignored");
            var settings = JsonConvert.DeserializeObject<LauncherSettings>("{\"SteamInstallDirectory\":\"obsolete-invalid-path\",\"MaximumStorageGiB\":1}");
            settings.DownloadDirectory = root;
            settings.ValidateStorage();
            Check(!JsonConvert.SerializeObject(settings).Contains("SteamInstallDirectory"), "legacy Steam path is ignored and not saved");
            var games = new[]
            {
                new Game { IsInstalled = true, PluginId = BuiltinStoreLibrary.EpicId, InstallDirectory = Path.Combine(root, "Epic"), InstallSize = 100 },
                new Game { IsInstalled = true, PluginId = BuiltinStoreLibrary.SteamId, InstallDirectory = Path.Combine(root, "Steam"), InstallSize = 200 },
                new Game { IsInstalled = true, PluginId = BuiltinStoreLibrary.EpicId, InstallDirectory = root + "Outside", InstallSize = 400 }
            };
            Check(StorageManagement.Used(settings, games) == 100, "Epic capacity excludes Steam and games outside configured folder");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("7 Steam installation and storage checks passed.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static void Expect<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }
}
