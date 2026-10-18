using Newtonsoft.Json;
using Playnite.HoleInOne;

static class LauncherSettingsChecks
{
    public static void Run(Action<bool, string> check)
    {
        var protectedId = Guid.NewGuid();
        var saved = new LauncherSettings { MaximumStorageGiB = 100 };
        saved.ProtectedGameIds.Add(protectedId);
        var restored = JsonConvert.DeserializeObject<LauncherSettings>(JsonConvert.SerializeObject(saved));
        check(restored.IsGameProtected(protectedId) && !restored.IsGameProtected(Guid.NewGuid()), "game protection survives settings reload and applies only to selected IDs");
        check(!JsonConvert.DeserializeObject<LauncherSettings>("{\"ProtectedGameIds\":null}").IsGameProtected(protectedId), "legacy or null protection list is supported");
        var root = Path.Combine(Path.GetTempPath(), "HoleInOne-Address-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = Path.Combine(root, "app");
            var user = Path.Combine(root, "user");
            Directory.CreateDirectory(app);
            Directory.CreateDirectory(user);
            var settingsPath = Path.Combine(user, "holeinone.json");
            var manifestPath = Path.Combine(app, "holeinone.development.json");
            var overridePath = Path.Combine(user, "holeinone.development-address.json");
            var manifest = JsonConvert.SerializeObject(new { BackendUrl = "http://203.0.113.10:5010/", BearerToken = new string('t', 64) });
            File.WriteAllText(manifestPath, manifest);
            File.WriteAllText(settingsPath, "{\"DownloadDirectory\":\"C:/Existing/Games\"}");
            var settings = LauncherSettings.Load(settingsPath, app, false);
            check(settings.CanChangeDevelopmentBackend && settings.BackendUrl == "http://203.0.113.10:5010/", "team package initially uses bundled address and permits editing");
            DevelopmentServiceAddress.Save(overridePath, " 203.0.113.11 ");
            settings = LauncherSettings.Load(settingsPath, app, false);
            check(settings.BackendUrl == "http://203.0.113.11:5010/", "per-PC saved IP overrides bundled address after reload");
            check(settings.BearerToken == new string('t', 64) && !settings.RequireLogin, "address override preserves package authentication");
            check(settings.DownloadDirectory == "C:/Existing/Games" && File.ReadAllText(manifestPath) == manifest, "address changes preserve other settings and package files");
            check(!File.ReadAllText(overridePath).Contains("BearerToken"), "per-PC address override does not copy credentials");
            DevelopmentServiceAddress.Save(overridePath, "http://203.0.113.12:5010");
            check(LauncherSettings.Load(settingsPath, app, false).BackendUrl == "http://203.0.113.12:5010/", "repeated address save replaces prior override");
            foreach (var value in new[] { "", "not an address", "file:///tmp/", "http://user:pass@host/", "http://host/api", "http://host/?x=1", "http://host/#fragment" })
            {
                try { DevelopmentServiceAddress.Save(overridePath, value); throw new Exception("Invalid address accepted"); }
                catch (InvalidDataException) { check(true, "invalid address rejected without changing saved address"); }
            }
            check(DevelopmentServiceAddress.Load(overridePath).BackendUrl == "http://203.0.113.12:5010/", "failed saves preserve last good address");
            File.Delete(manifestPath);
            File.WriteAllText(Path.Combine(app, "holeinone.service.json"), "{\"BackendUrl\":\"https://service.example.com/\"}");
            settings = LauncherSettings.Load(settingsPath, app, true);
            check(!settings.CanChangeDevelopmentBackend && settings.BackendUrl == "https://service.example.com/"
                && settings.RequireLogin && settings.BearerToken == null, "production ignores development address override and keeps account mode");
        }
        finally { Directory.Delete(root, true); }
    }
}

// The linked settings code uses this only in its desktop entry points.
namespace Playnite
{
    static class PlaynitePaths
    {
        public static string ConfigRootPath => throw new InvalidOperationException("Use explicit test paths");
    }
}
