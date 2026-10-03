using Newtonsoft.Json;
using Playnite.HoleInOne;

static class StoreChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "HoleInOne-Stores-" + Guid.NewGuid().ToString("N"));
        try
        {
            var steam = Path.Combine(root, "Steam");
            var second = Path.Combine(root, "Other Library");
            var common = Path.Combine(second, "steamapps", "common", "Game");
            Directory.CreateDirectory(common);
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\" { // nested library\n\"1\" { \"path\" " + JsonConvert.SerializeObject(second) + " } }");
            var manifest = Path.Combine(second, "steamapps", "appmanifest_123.acf");
            void State(string flag) => File.WriteAllText(manifest,
                "\"AppState\" { \"appid\" \"123\" \"name\" \"Game\" \"installdir\" \"Game\" \"StateFlags\" \"" + flag + "\" }");
            State("1026");
            check(StoreFiles.ReadSteam(steam).Count == 0, "Steam partial download is not installed");
            State("4");
            var games = StoreFiles.ReadSteam(steam);
            check(games.Count == 1 && games["123"].Directory == common, "Steam secondary library and escaped path detected");
            State("6");
            check(StoreFiles.ReadSteam(steam).Count == 0, "Steam update-required state cannot trigger local handoff");
            File.WriteAllText(manifest, "\"AppState\" {");
            check(StoreFiles.ReadSteam(steam).Count == 0, "Steam incomplete manifest is ignored while launcher writes it");
            var epic = Path.Combine(root, "EpicManifests");
            Directory.CreateDirectory(epic);
            File.WriteAllText(Path.Combine(common, "game.exe"), "fixture");
            void Epic(bool incomplete, string executable) => File.WriteAllText(Path.Combine(epic, "game.item"), JsonConvert.SerializeObject(new {
                AppName = "epic-game", DisplayName = "Epic Game", InstallLocation = common, LaunchExecutable = executable,
                bIsIncompleteInstall = incomplete, CatalogNamespace = "ns", CatalogItemId = "catalog"
            }));
            Epic(true, "game.exe");
            check(StoreFiles.ReadEpic(epic).Count == 0, "Epic incomplete installation is ignored");
            Epic(false, "game.exe");
            check(StoreFiles.ReadEpic(epic)["epic-game"].LaunchUri.Contains("ns%3Acatalog%3Aepic-game"), "Epic manifest builds official launcher URI");
            Epic(false, "missing.exe");
            check(StoreFiles.ReadEpic(epic).Count == 0, "Epic requires installed executable");
            Epic(false, "../game.exe");
            check(StoreFiles.ReadEpic(epic).Count == 0, "Epic rejects executable outside game folder");
            var settingsPath = Path.Combine(root, "settings.save");
            Sts2ModSetup.Enable(settingsPath);
            var settings = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(settingsPath));
            check((int)settings["schema_version"] == 5 && (bool)settings["mod_settings"]["mods_enabled"], "fresh Slay the Spire 2 settings enable mods before first launch");
            File.WriteAllText(settingsPath, "{\"schema_version\":5,\"volume_master\":0.2,\"mod_settings\":{\"mods_enabled\":false,\"mod_list\":[{\"id\":\"GameLoader\",\"is_enabled\":false},{\"id\":\"OtherMod\",\"is_enabled\":false}]}}");
            Sts2ModSetup.Enable(settingsPath);
            settings = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(settingsPath));
            check((double)settings["volume_master"] == 0.2 && (bool)settings["mod_settings"]["mod_list"][0]["is_enabled"]
                && !(bool)settings["mod_settings"]["mod_list"][1]["is_enabled"], "mod setup preserves sound settings and other mod choices");
            File.WriteAllText(settingsPath, "{broken");
            var rejected = false;
            try { Sts2ModSetup.Enable(settingsPath); } catch (JsonException) { rejected = true; }
            check(rejected && File.ReadAllText(settingsPath) == "{broken", "invalid existing settings are never replaced with defaults");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
