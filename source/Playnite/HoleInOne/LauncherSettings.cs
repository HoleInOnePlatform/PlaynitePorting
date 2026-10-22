using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Playnite.HoleInOne
{
    public sealed class LauncherSettings
    {
        public bool SteamCloudUploadEnabled { get; set; }
        public string BackendUrl { get; set; } = "http://127.0.0.1:5010/";
        public string BearerToken { get; set; }
        public bool RequireLogin { get; set; }
        [JsonIgnore]
        public bool CanChangeDevelopmentBackend { get; private set; }
        // Retain the serialized name for existing Epic installations/settings.
        public string DownloadDirectory { get; set; } = Path.Combine(
            Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        public string SteamInstallDirectory { get; set; } = DefaultSteamDirectory();
        public HashSet<Guid> ProtectedGameIds { get; set; } = new HashSet<Guid>();
        public bool IsGameProtected(Guid id) => ProtectedGameIds?.Contains(id) == true;
        private long? maximumStorageGiB;
        public long MaximumStorageGiB
        {
            get => maximumStorageGiB ?? new[] { SteamInstallDirectory, DownloadDirectory }
                .Select(path => Path.GetPathRoot(Path.GetFullPath(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Sum(drive => new DriveInfo(drive).TotalSize) / (1024L * 1024 * 1024);
            set => maximumStorageGiB = value;
        }

        private static string DefaultSteamDirectory()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser?.OpenSubKey(@"Software\Valve\Steam"))
                return Path.Combine(key?.GetValue("SteamPath") as string ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"), "steamapps", "common");
        }

        public void ValidateStorage()
        {
            foreach (var path in new[] { DownloadDirectory, SteamInstallDirectory })
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.IndexOf(':') != 1)
                    throw new ArgumentException("설치 폴더는 로컬 드라이브의 절대 경로여야 합니다.");
                var full = Path.GetFullPath(path).TrimEnd('\\', '/');
                if (full.Length <= 3) throw new ArgumentException("드라이브 전체를 설치 폴더로 지정할 수 없습니다.");
            }
            if (MaximumStorageGiB <= 0 || MaximumStorageGiB > long.MaxValue / (1024L * 1024 * 1024))
                throw new ArgumentException("최대 저장공간은 1 GiB 이상의 유효한 정수여야 합니다.");
        }

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
            ValidateStorage();
            Directory.CreateDirectory(PlaynitePaths.ConfigRootPath);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
