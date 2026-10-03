using Newtonsoft.Json;
using System;
using System.IO;

namespace Playnite.HoleInOne
{
    public sealed class LauncherSettings
    {
        public string BackendUrl { get; set; } = "http://127.0.0.1:5010/";
        public string BearerToken { get; set; }
        public string DownloadDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoleInOne", "Games");

        public static string SettingsPath => Path.Combine(PlaynitePaths.ConfigRootPath, "holeinone.json");
        public static LauncherSettings Load() => File.Exists(SettingsPath)
            ? JsonConvert.DeserializeObject<LauncherSettings>(File.ReadAllText(SettingsPath)) ?? new LauncherSettings()
            : new LauncherSettings();

        public void Save()
        {
            BackendConnection.ValidateAddress(BackendUrl);
            if (!Path.IsPathRooted(DownloadDirectory)) throw new ArgumentException("다운로드 폴더의 절대 경로가 필요함.");
            Directory.CreateDirectory(PlaynitePaths.ConfigRootPath);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
