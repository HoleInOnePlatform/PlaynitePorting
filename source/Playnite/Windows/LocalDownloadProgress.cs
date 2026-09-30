using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Windows
{
    internal sealed class LocalDownloadProgress
    {
        public double? Percent { get; private set; }
        public string Status { get; private set; }

        private LocalDownloadProgress(double? percent, string status)
        {
            Percent = percent;
            Status = status;
        }

        public static LocalDownloadProgress Read(Game game)
        {
            try
            {
                BuiltinExtension source;
                if (!BuiltinExtensions.ExtensionList.TryGetValue(game.PluginId, out source))
                {
                    return new LocalDownloadProgress(null, "지원되지 않는 게임 라이브러리");
                }

                if (source == BuiltinExtension.SteamLibrary)
                {
                    return ReadSteam(game.GameId);
                }

                if (source == BuiltinExtension.EpicLibrary)
                {
                    return ReadEpic(game);
                }

                return new LocalDownloadProgress(null, "지원되지 않는 게임 라이브러리");
            }
            catch (IOException)
            {
                return new LocalDownloadProgress(null, "다운로드 상태를 읽을 수 없음");
            }
            catch (UnauthorizedAccessException)
            {
                return new LocalDownloadProgress(null, "다운로드 상태에 접근할 수 없음");
            }
            catch (System.ArgumentException)
            {
                return new LocalDownloadProgress(null, "다운로드 상태를 읽을 수 없음");
            }
        }

        private static LocalDownloadProgress ReadSteam(string appId)
        {
            uint parsedId;
            if (!uint.TryParse(appId, out parsedId))
            {
                return new LocalDownloadProgress(null, "Steam 게임 ID를 확인할 수 없음");
            }

            var steamRoot = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamRoot) || !Directory.Exists(steamRoot))
            {
                return new LocalDownloadProgress(null, "Steam 설치 경로를 찾을 수 없음");
            }

            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            var foldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(foldersPath))
            {
                var folders = File.ReadAllText(foldersPath);
                foreach (Match match in Regex.Matches(folders, "\"path\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase))
                {
                    libraries.Add(match.Groups["path"].Value.Replace("\\\\", "\\"));
                }
            }

            foreach (var library in libraries)
            {
                var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{parsedId}.acf");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = File.ReadAllText(manifestPath);
                var total = SteamValue(manifest, "BytesToDownload");
                var downloaded = SteamValue(manifest, "BytesDownloaded");
                var stageTotal = SteamValue(manifest, "BytesToStage");
                var staged = SteamValue(manifest, "BytesStaged");
                if (total > 0)
                {
                    var percent = Math.Min(100d, 100d * downloaded / total);
                    if (downloaded >= total && stageTotal > 0 && staged < stageTotal)
                    {
                        return new LocalDownloadProgress(Math.Min(99d, percent), "Steam 설치 중");
                    }

                    return new LocalDownloadProgress(percent, "Steam 다운로드 기준");
                }

                return new LocalDownloadProgress(null, "Steam 진행률 정보가 아직 없음");
            }

            return new LocalDownloadProgress(null, "Steam 다운로드 기록을 찾을 수 없음");
        }

        private static ulong SteamValue(string manifest, string key)
        {
            var match = Regex.Match(manifest, "\"" + key + "\"\\s+\"(?<value>\\d+)\"", RegexOptions.IgnoreCase);
            ulong value;
            return match.Success && ulong.TryParse(match.Groups["value"].Value, out value) ? value : 0;
        }

        private static LocalDownloadProgress ReadEpic(Game game)
        {
            var manifestRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestRoot))
            {
                return new LocalDownloadProgress(null, "Epic 설치 기록을 찾을 수 없음");
            }

            var files = Directory.EnumerateFiles(manifestRoot, "*.item")
                .Concat(Directory.Exists(Path.Combine(manifestRoot, "Pending"))
                    ? Directory.EnumerateFiles(Path.Combine(manifestRoot, "Pending"), "*.item")
                    : Enumerable.Empty<string>());
            foreach (var file in files)
            {
                JObject item;
                try
                {
                    item = JObject.Parse(File.ReadAllText(file));
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    continue;
                }

                var appName = (string)item["AppName"];
                var catalogId = (string)item["CatalogItemId"];
                var displayName = (string)item["DisplayName"];
                if (!string.Equals(game.GameId, appName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(game.GameId, catalogId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(game.Name, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installPath = (string)item["InstallLocation"];
                var total = (long?)item["InstallSize"] ?? 0;
                if (total <= 0 || string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                {
                    return new LocalDownloadProgress(null, "Epic 설치 용량을 확인할 수 없음");
                }

                var installed = Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains(".egstore"))
                    .Sum(path => new FileInfo(path).Length);
                var incomplete = (bool?)item["bIsIncompleteInstall"] == true ||
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "Pending", StringComparison.OrdinalIgnoreCase);
                var percent = Math.Min(incomplete ? 99d : 100d, 100d * installed / total);
                return new LocalDownloadProgress(percent, incomplete ? "Epic 설치 파일 기준 추정" : "Epic 설치 파일 기준");
            }

            return new LocalDownloadProgress(null, "Epic 다운로드 기록을 찾을 수 없음");
        }
    }
}
