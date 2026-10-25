using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;

namespace Playnite.HoleInOne
{
    public sealed class LauncherSettings
    {
        [JsonIgnore]
        public string BackendUrl { get; set; } = "http://127.0.0.1:5010/";
        [JsonIgnore]
        public bool CanChangeDevelopmentBackend { get; private set; }
        [JsonIgnore] public string BackendAddressSource { get; private set; }
        [JsonIgnore] public string DevelopmentAddressWarning { get; private set; }
        // Retain the serialized name for existing Epic installations/settings.
        public string DownloadDirectory { get; set; } = Path.Combine(
            Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        public HashSet<Guid> ProtectedGameIds { get; set; } = new HashSet<Guid>();
        public bool IsGameProtected(Guid id) => ProtectedGameIds?.Contains(id) == true;
        private long? maximumStorageGiB;
        [JsonIgnore] public bool AutomaticStorageLimit => !maximumStorageGiB.HasValue;
        public bool ShouldSerializeMaximumStorageGiB() => maximumStorageGiB.HasValue;
        public void UseAutomaticStorageLimit() => maximumStorageGiB = null;
        public long MaximumStorageGiB
        {
            get => maximumStorageGiB ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DownloadDirectory))).TotalSize / (1024L * 1024 * 1024);
            set => maximumStorageGiB = value;
        }

        public void ValidateStorage()
        {
            if (string.IsNullOrWhiteSpace(DownloadDirectory) || !Path.IsPathRooted(DownloadDirectory) || DownloadDirectory.IndexOf(':') != 1)
                throw new ArgumentException("설치 폴더는 로컬 드라이브의 절대 경로여야 합니다.");
            var full = Path.GetFullPath(DownloadDirectory).TrimEnd('\\', '/');
            if (full.Length <= 3) throw new ArgumentException("드라이브 전체를 설치 폴더로 지정할 수 없습니다.");
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
            settings.BackendUrl = service?.BackendUrl ?? "http://127.0.0.1:5010/";
            settings.CanChangeDevelopmentBackend = !serviceRequired;
            settings.BackendAddressSource = service == null ? "local development default" : "holeinone.service.json";
            if (!serviceRequired)
            {
                var addressPath = Path.Combine(Path.GetDirectoryName(settingsPath), "holeinone.development-address.json");
                try
                {
                    var address = DevelopmentServiceAddress.Load(addressPath);
                    if (address != null) { settings.BackendUrl = address.BackendUrl; settings.BackendAddressSource = "holeinone.development-address.json"; }
                }
                catch (Exception error) when (error is JsonException || error is InvalidDataException || error is IOException || error is UnauthorizedAccessException)
                {
                    settings.DevelopmentAddressWarning = "저장된 개발 서버 주소를 읽을 수 없어 기본 주소로 돌아왔습니다. 설정에서 개발 서버 주소를 다시 입력해 주세요.";
                }
                if (service == null && settings.BackendAddressSource == "local development default")
                    settings.DevelopmentAddressWarning = settings.DevelopmentAddressWarning ?? "로컬 개발 기본 주소를 사용합니다. 실행 중인 서버가 없다면 설정에서 개발 서버 주소를 입력해 주세요.";
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
