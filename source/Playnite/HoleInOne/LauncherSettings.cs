using Newtonsoft.Json;
using System;
using System.IO;

namespace Playnite.HoleInOne
{
    public sealed class LauncherSettings
    {
        public string BackendUrl { get; set; } = "http://127.0.0.1:5010/";
        public string BearerToken { get; set; }
        public bool RequireLogin { get; set; }
        // Explicitly enabled after connecting a Steam account; no prompts are introduced during handoff.
        public bool SteamCloudUploadEnabled { get; set; }
        [JsonIgnore]
        public bool CanChangeDevelopmentBackend { get; private set; }
        public string DownloadDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoleInOne", "Games");

        public static string SettingsPath => Path.Combine(PlaynitePaths.ConfigRootPath, "holeinone.json");
        public static string DevelopmentAddressPath => Path.Combine(PlaynitePaths.ConfigRootPath, "holeinone.development-address.json");
        public static LauncherSettings Load()
        {
#if HOLEINONE_DISTRIBUTION
            const bool serviceRequired = true;
#else
            const bool serviceRequired = false;
#endif
            return Load(SettingsPath, AppDomain.CurrentDomain.BaseDirectory, serviceRequired);
        }

        internal static LauncherSettings Load(string settingsPath, string applicationDirectory, bool serviceRequired)
        {
            var settings = File.Exists(settingsPath)
                ? JsonConvert.DeserializeObject<LauncherSettings>(File.ReadAllText(settingsPath)) ?? new LauncherSettings()
                : new LauncherSettings();
            // The release bundle supplies only the public service address, never credentials.
            var servicePath = Path.Combine(applicationDirectory, "holeinone.service.json");
            var service = ServiceConfiguration.Load(servicePath, serviceRequired);
            var development = DevelopmentServiceConfiguration.Load(
                Path.Combine(applicationDirectory, "holeinone.development.json"), serviceRequired);
            if (service != null && development != null)
                throw new InvalidDataException("서비스 설정과 개발용 설정이 함께 있습니다. 올바른 배포 폴더를 사용해 주세요.");
            if (service != null)
            {
                settings.BackendUrl = service.BackendUrl;
                settings.BearerToken = null;
                settings.RequireLogin = true;
            }
            else if (development != null)
            {
                var addressPath = Path.Combine(Path.GetDirectoryName(settingsPath), "holeinone.development-address.json");
                settings.BackendUrl = DevelopmentServiceAddress.Load(addressPath)?.BackendUrl ?? development.BackendUrl;
                settings.BearerToken = development.BearerToken;
                settings.RequireLogin = false;
                settings.CanChangeDevelopmentBackend = true;
            }
            return settings;
        }

        public static void SaveDevelopmentBackendAddress(string address)
        {
            if (!Load().CanChangeDevelopmentBackend)
                throw new InvalidOperationException("팀 개발용 배포에서만 서버 주소를 변경할 수 있습니다.");
            DevelopmentServiceAddress.Save(DevelopmentAddressPath, address);
        }

        public void Save()
        {
            BackendConnection.ValidateAddress(BackendUrl);
            if (!Path.IsPathRooted(DownloadDirectory)) throw new ArgumentException("다운로드 폴더의 절대 경로가 필요함.");
            Directory.CreateDirectory(PlaynitePaths.ConfigRootPath);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
