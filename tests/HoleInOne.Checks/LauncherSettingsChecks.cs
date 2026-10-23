using Newtonsoft.Json;
using Playnite.HoleInOne;

static class LauncherSettingsChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "HoleInOne-Address-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var app = Path.Combine(root, "app"); var user = Path.Combine(root, "user");
            Directory.CreateDirectory(app); Directory.CreateDirectory(user);
            var settingsPath = Path.Combine(user, "holeinone.json");
            var overridePath = Path.Combine(user, "holeinone.development-address.json");
            var id = Guid.NewGuid();
            File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new {
                BearerToken = "old-secret", RequireLogin = false, SteamCloudUploadEnabled = true,
                BackendUrl = "http://old-server/", DownloadDirectory = "C:/Existing/Games", ProtectedGameIds = new[] { id } }));
            File.WriteAllText(Path.Combine(app, "holeinone.development.json"), "{\"BearerToken\":\"old-secret\",\"BackendUrl\":\"http://old-server/\"}");
            var settings = LauncherSettings.Load(settingsPath, app, false);
            var serialized = JsonConvert.SerializeObject(settings);
            check(!serialized.Contains("MaximumStorageGiB") && settings.AutomaticStorageLimit,
                "saving unrelated settings preserves automatic storage capacity");
            settings.MaximumStorageGiB = 500;
            var fixedSettings = JsonConvert.DeserializeObject<LauncherSettings>(JsonConvert.SerializeObject(settings));
            check(!fixedSettings.AutomaticStorageLimit && fixedSettings.MaximumStorageGiB == 500, "explicit capacity survives settings reload");
            fixedSettings.UseAutomaticStorageLimit();
            check(!JsonConvert.SerializeObject(fixedSettings).Contains("MaximumStorageGiB"), "automatic capacity can be restored without persisting a computed limit");
            check(!serialized.Contains("old-secret") && !serialized.Contains("RequireLogin") && !serialized.Contains("SteamCloudUploadEnabled") && !serialized.Contains("BackendUrl"),
                "legacy credentials, bypass flags and service address are not serialized into user settings");
            check(settings.BackendUrl == "http://127.0.0.1:5010/" && settings.CanChangeDevelopmentBackend,
                "legacy shared-token package is ignored and development defaults to loopback");
            check(settings.DownloadDirectory == "C:/Existing/Games" && settings.IsGameProtected(id), "migration retains installation and protected-game settings");
            DevelopmentServiceAddress.Save(overridePath, "https://dev.example/");
            settings = LauncherSettings.Load(settingsPath, app, false);
            check(settings.BackendUrl == "https://dev.example/" && settings.BackendAddressSource == "holeinone.development-address.json", "one development override determines address and source");
            foreach (var value in new[] { "", "http://203.0.113.1/", "file:///tmp/", "https://user:pass@host/", "https://host/api", "https://host/?x=1", "https://host/#fragment" })
            {
                var rejected = false;
                try { DevelopmentServiceAddress.Save(overridePath, value); } catch (InvalidDataException) { rejected = true; }
                check(rejected && DevelopmentServiceAddress.Load(overridePath).BackendUrl == "https://dev.example/", "invalid address cannot overwrite a valid override");
            }
            File.WriteAllText(Path.Combine(app, "holeinone.service.json"), "{\"BackendUrl\":\"https://service.example/\"}");
            foreach (var invalid in new[] { "{", "null", "{}", "{\"BackendUrl\":\"\"}", "{\"BackendUrl\":\"http://203.0.113.1/\"}" })
            {
                File.WriteAllText(overridePath, invalid);
                var recovered = LauncherSettings.Load(settingsPath, app, false);
                check(recovered.BackendUrl == "https://service.example/" && recovered.DevelopmentAddressWarning != null,
                    "invalid development override falls back to service with guidance");
                check(recovered.DownloadDirectory == "C:/Existing/Games" && recovered.IsGameProtected(id), "address recovery preserves game settings");
            }
            check(DevelopmentServiceAddress.Normalize("127.0.0.1") == "http://127.0.0.1:5010/", "bare loopback remains supported");
            settings = LauncherSettings.Load(settingsPath, app, true);
            check(!settings.CanChangeDevelopmentBackend && settings.BackendUrl == "https://service.example/", "distribution uses service address and ignores development files");
            check(!JsonConvert.DeserializeObject<LauncherSettings>("{\"ProtectedGameIds\":null}").IsGameProtected(id), "null protection list remains supported");
        }
        finally { Directory.Delete(root, true); }
    }
}
namespace Playnite
{
    static class PlaynitePaths { public static string ConfigRootPath => throw new InvalidOperationException("Use explicit test paths"); }
}
