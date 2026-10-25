using Playnite.HoleInOne;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.HoleInOne
{
    internal static class LauncherSettingsService
    {
        public static async Task CheckAddressAsync(string address, CancellationToken ct)
        {
            using (var backend = new BackendConnection(DevelopmentServiceAddress.Normalize(address)))
                await backend.CheckDevelopmentHealthAsync(ct);
        }
        public static async Task<LauncherSettings> ChangeAddressAsync(string address)
        {
            var normalized = DevelopmentServiceAddress.Normalize(address);
            await CloudPreparation.ResetAsync();
            LauncherSettings.SaveDevelopmentBackendAddress(normalized);
            LauncherLogin.Forget();
            return LauncherSettings.Load();
        }
        public static LauncherSettings PrepareStorage(string epic, bool automatic, string maximum)
        {
            RequireIdle();
            long capacity = 0;
            if (!automatic && !long.TryParse(maximum, out capacity)) throw new ArgumentException("최대 용량은 GiB 단위의 정수로 입력해 주세요.");
            var updated = LauncherSettings.Load();
            updated.DownloadDirectory = epic.Trim();
            if (automatic) updated.UseAutomaticStorageLimit(); else updated.MaximumStorageGiB = capacity;
            updated.ValidateStorage();
            return updated;
        }
        public static void SaveStorage(LauncherSettings settings)
        {
            RequireIdle();
            settings.ProtectedGameIds = LauncherSettings.Load().ProtectedGameIds;
            settings.Save();
        }
        public static void ProtectGame(Guid id, bool protect)
        {
            RequireIdle();
            var settings = LauncherSettings.Load();
            settings.ProtectedGameIds = settings.ProtectedGameIds ?? new HashSet<Guid>();
            if (protect) settings.ProtectedGameIds.Add(id); else settings.ProtectedGameIds.Remove(id);
            settings.Save();
        }
        private static void RequireIdle()
        {
            if (StorageManagement.IsBusy) throw new InvalidOperationException("설치 또는 공간 확보가 진행 중입니다. 완료 후 설정을 변경해 주세요.");
        }
    }
}
