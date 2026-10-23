using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    public static class StorageManagement
    {
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public static bool IsBusy => gate.CurrentCount == 0;
        private sealed class Lease : IDisposable
        {
            private int disposed;
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
        }
        public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
        public static bool IsChild(string root, string child) => !string.IsNullOrWhiteSpace(child) &&
            Normalize(child).StartsWith(Normalize(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void CheckLinks(string path)
        {
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("연결된 폴더는 자동 저장공간 관리에 사용할 수 없습니다: " + dir.FullName);
        }
        public static ulong RecordedSize(Game game)
        {
            // Steam's small manifest is cheap to read and also covers existing libraries
            // whose database entry has never received installation size metadata.
            if (game.PluginId == BuiltinStoreLibrary.SteamId && uint.TryParse(game.GameId, out var appId))
            {
                var common = Directory.GetParent(game.InstallDirectory);
                var apps = common?.Parent;
                if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true && apps != null)
                {
                    var manifest = Path.Combine(apps.FullName, "appmanifest_" + appId + ".acf");
                    if (File.Exists(manifest))
                    {
                        var root = VdfNode.Read(File.ReadAllText(manifest));
                        if (root.Children.TryGetValue("AppState", out var state) && state.Get("appid") == game.GameId &&
                            ulong.TryParse(state.Get("SizeOnDisk"), out var size) && size > 0)
                            return size;
                    }
                }
            }
            if (game.InstallSize > 0) return game.InstallSize.Value;
            throw new InvalidOperationException("설치 용량 정보가 없는 게임이 있습니다: " + game.Name + ". 라이브러리를 새로고침한 뒤 다시 확인해 주세요. 파일 전체 탐색은 수행하지 않습니다.");
        }

        public static ulong Used(LauncherSettings settings, IEnumerable<Game> games)
        {
            settings.ValidateStorage();
            var roots = new[] { Normalize(settings.SteamInstallDirectory), Normalize(settings.DownloadDirectory) }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ulong total = 0;
            var installed = games.Where(g => g.IsInstalled && roots.Any(root => IsChild(root, g.InstallDirectory)))
                .GroupBy(g => Normalize(g.InstallDirectory), StringComparer.OrdinalIgnoreCase);
            foreach (var group in installed)
                total = checked(total + RecordedSize(group.First()));
            return total;
        }
        private static bool Idle(Game game) => game.IsInstalled && !game.IsRunning && !game.IsLaunching &&
            !game.IsInstalling && !game.IsUninstalling;

        // Hold the lease until installation completes, so simultaneous requests cannot spend the same space.
        public static async Task<IDisposable> PrepareAsync(IPlayniteAPI api, Game incoming, ulong required,
            LauncherSettings settings, string destination)
        {
            if (!await gate.WaitAsync(0)) throw new InvalidOperationException("다른 게임의 설치 또는 공간 확보가 진행 중입니다. 완료 후 다시 시도해 주세요.");
            var lease = new Lease();
            try
            {
                settings.ValidateStorage();
                var maximum = checked((ulong)settings.MaximumStorageGiB * 1024 * 1024 * 1024);
                // Reject impossible/unknown sizes before even scanning or invoking uninstallers.
                StoragePlanner.Plan(0, required, maximum, ulong.MaxValue, new StorageCandidate[0]);
                CheckLinks(destination);
                Directory.CreateDirectory(destination);
                var drive = Path.GetPathRoot(destination);
                var games = api.Database.Games.ToList();
                var eligible = games.Where(g => g.Id != incoming.Id && !settings.IsGameProtected(g.Id) && Idle(g) &&
                    ((g.PluginId == BuiltinStoreLibrary.SteamId && IsChild(settings.SteamInstallDirectory, g.InstallDirectory)) ||
                     (g.PluginId == BuiltinStoreLibrary.EpicId && IsChild(settings.DownloadDirectory, g.InstallDirectory))))
                    .Where(g => !games.Any(other => other.Id != g.Id && !string.IsNullOrWhiteSpace(other.InstallDirectory) &&
                        (string.Equals(Normalize(g.InstallDirectory), Normalize(other.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
                         IsChild(g.InstallDirectory, other.InstallDirectory) || IsChild(other.InstallDirectory, g.InstallDirectory))))
                    .ToDictionary(g => g.Id);
                var originalPaths = eligible.ToDictionary(pair => pair.Key, pair => pair.Value.InstallDirectory);
                var originalActivity = eligible.ToDictionary(pair => pair.Key, pair => pair.Value.LastActivity);
                var plan = await Task.Run(() => StoragePlanner.Plan(Used(settings, games), required, maximum,
                    (ulong)new DriveInfo(drive).AvailableFreeSpace, eligible.Values.Select(g => new StorageCandidate
                    {
                        Id = g.Id, Bytes = RecordedSize(g), LastPlayed = g.LastActivity, IsProtected = settings.IsGameProtected(g.Id),
                        OnTargetDrive = string.Equals(Path.GetPathRoot(g.InstallDirectory), drive, StringComparison.OrdinalIgnoreCase)
                    }).ToList()));
                // The entire ordered list exists before the first uninstall request.
                var currentSettings = LauncherSettings.Load();
                foreach (var item in plan)
                    if (currentSettings.IsGameProtected(item.Id) || !Idle(eligible[item.Id]) || eligible[item.Id].InstallDirectory != originalPaths[item.Id] ||
                        eligible[item.Id].LastActivity != originalActivity[item.Id])
                        throw new InvalidOperationException("정리 대상 게임의 정보가 바뀌었습니다. 게임을 제거하지 않았습니다. 다시 시도해 주세요.");
                foreach (var item in plan)
                {
                    var game = eligible[item.Id];
                    if (LauncherSettings.Load().IsGameProtected(item.Id) || !Idle(game) || game.InstallDirectory != originalPaths[item.Id] || game.LastActivity != originalActivity[item.Id])
                        throw new InvalidOperationException("정리 대상 게임의 상태가 바뀌어 설치를 중단했습니다. 다시 시도해 주세요.");
                    CheckLinks(game.InstallDirectory);
                    api.UninstallGame(game.Id);
                    var deadline = DateTime.UtcNow.AddMinutes(5);
                    while (game.IsInstalled || game.IsUninstalling)
                    {
                        if (!game.IsUninstalling || game.IsRunning || game.IsLaunching || DateTime.UtcNow >= deadline)
                            throw new IOException("게임 제거가 완료되지 않아 설치를 중단했습니다: " + game.Name);
                        await Task.Delay(500);
                    }
                }
                // Never expand the deletion plan after side effects; leftovers/errors stop this attempt.
                var remaining = api.Database.Games.ToList();
                var used = await Task.Run(() => Used(settings, remaining));
                if (used > maximum - required || (ulong)new DriveInfo(drive).AvailableFreeSpace < required)
                    throw new IOException("정리 후 실제 여유 공간이 부족합니다. 추가 게임은 제거하지 않았습니다.");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
    }
}
