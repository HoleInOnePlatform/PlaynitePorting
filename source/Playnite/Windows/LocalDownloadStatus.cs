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
    internal sealed class SteamInstallSnapshot
    {
        public bool HasManifest { get; set; }
        public bool IsDownloading { get; set; }
        public bool IsInstalling { get; set; }
        public bool IsInstalled { get; set; }
        public bool IsUninstalled { get; set; }
    }

    internal static class LocalDownloadStatus
    {
        public static string Read(Game game)
        {
            try
            {
                BuiltinExtension source;
                if (!BuiltinExtensions.ExtensionList.TryGetValue(game.PluginId, out source))
                {
                    return "지원되지 않는 게임 라이브러리";
                }

                if (source == BuiltinExtension.SteamLibrary)
                {
                    return ReadSteam(game);
                }

                if (source == BuiltinExtension.EpicLibrary)
                {
                    return ReadEpic(game);
                }

                return "지원되지 않는 게임 라이브러리";
            }
            catch (IOException)
            {
                return "다운로드 상태를 읽을 수 없음";
            }
            catch (UnauthorizedAccessException)
            {
                return "다운로드 상태에 접근할 수 없음";
            }
            catch (ArgumentException)
            {
                return "다운로드 상태를 읽을 수 없음";
            }
        }

        private static string ReadSteam(Game game)
        {
            if (!uint.TryParse(game.GameId, out _))
            {
                return "Steam 게임 ID를 확인할 수 없음";
            }

            var snapshot = GetSteamSnapshot(game.GameId);
            if (snapshot == null)
            {
                return "Steam 설치 경로를 찾을 수 없음";
            }

            if (snapshot.IsDownloading)
            {
                return "Steam 다운로드 중";
            }

            if (snapshot.IsInstalling)
            {
                return "Steam 설치 중";
            }

            if (snapshot.IsInstalled || game.IsInstalled)
            {
                return "다운로드 완료";
            }

            if (snapshot.IsUninstalled)
            {
                return "다운로드 취소됨";
            }

            return game.IsInstalling ? "Steam 다운로드 준비 중" : "Steam 다운로드 대기 중";
        }

        internal static SteamInstallSnapshot GetSteamSnapshot(string gameId)
        {
            uint appId;
            if (!uint.TryParse(gameId, out appId))
            {
                return null;
            }

            var steamRoot = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamRoot) || !Directory.Exists(steamRoot))
            {
                return null;
            }

            var snapshot = new SteamInstallSnapshot();
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
                var steamApps = Path.Combine(library, "steamapps");
                var downloadingPath = Path.Combine(steamApps, "downloading", appId.ToString());
                if (Directory.Exists(downloadingPath))
                {
                    snapshot.IsDownloading = true;
                }
            }

            foreach (var library in libraries)
            {
                var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                snapshot.HasManifest = true;
                var manifest = File.ReadAllText(manifestPath);
                var flags = SteamValue(manifest, "StateFlags");
                var total = SteamValue(manifest, "BytesToDownload");
                var downloaded = SteamValue(manifest, "BytesDownloaded");
                var stageTotal = SteamValue(manifest, "BytesToStage");
                var staged = SteamValue(manifest, "BytesStaged");
                if (stageTotal > 0 && staged < stageTotal && total > 0 && downloaded >= total)
                {
                    snapshot.IsInstalling = true;
                }

                snapshot.IsInstalled = (flags & 4) != 0;
                snapshot.IsUninstalled = (flags & 1) != 0;
                break;
            }

            return snapshot;
        }

        private static ulong SteamValue(string manifest, string key)
        {
            var match = Regex.Match(manifest, "\"" + key + "\"\\s+\"(?<value>\\d+)\"", RegexOptions.IgnoreCase);
            ulong value;
            return match.Success && ulong.TryParse(match.Groups["value"].Value, out value) ? value : 0;
        }

        private static string ReadEpic(Game game)
        {
            var manifestRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestRoot))
            {
                return game.IsInstalled ? "다운로드 완료" : "Epic 다운로드 준비 중";
            }

            var pendingPath = Path.Combine(manifestRoot, "Pending");
            var files = (Directory.Exists(pendingPath)
                    ? Directory.EnumerateFiles(pendingPath, "*.item")
                    : Enumerable.Empty<string>())
                .Concat(Directory.EnumerateFiles(manifestRoot, "*.item"));
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
                var matchesId = !string.IsNullOrEmpty(game.GameId) &&
                    (string.Equals(game.GameId, appName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(game.GameId, catalogId, StringComparison.OrdinalIgnoreCase));
                if (!matchesId && !string.Equals(game.Name, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pending = string.Equals(Path.GetDirectoryName(file), pendingPath, StringComparison.OrdinalIgnoreCase);
                if (pending || (bool?)item["bIsIncompleteInstall"] == true)
                {
                    return "Epic 다운로드 중";
                }

                return "다운로드 완료";
            }

            return game.IsInstalled ? "다운로드 완료" : "Epic 다운로드 준비 중";
        }
    }
}
