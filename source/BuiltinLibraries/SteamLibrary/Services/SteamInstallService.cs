using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SteamLibrary.Services
{
    internal static class SteamInstallService
    {
        internal static async Task WaitForRemovalAsync(Func<bool> isPresent, CancellationToken token,
            TimeSpan timeout, TimeSpan interval)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var present = isPresent();
                    token.ThrowIfCancellationRequested();
                    if (!present) return;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException)
                {
                    // Steam can rewrite library metadata while removing a game.
                    // An unreadable state is unknown, never proof of removal.
                }
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Steam 제거 완료를 확인하지 못했습니다. Steam 다운로드 상태를 확인해 주세요.");
        }

        // Read-only discovery for progress tracking; these folders never select the install destination.
        internal static Dictionary<string, DateTime> CaptureManifests(uint appId, IEnumerable<string> folders, bool requireReadable = false)
        {
            var manifests = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                var file = Path.Combine(folder, "steamapps", $"appmanifest_{appId}.acf");
                // File.Exists hides access errors; completion needs a successful directory read.
                var exists = requireReadable
                    ? Directory.GetFiles(Path.Combine(folder, "steamapps"), $"appmanifest_{appId}.acf").Length != 0
                    : File.Exists(file);
                if (exists) manifests[file] = File.GetLastWriteTimeUtc(file);
            }
            return manifests;
        }

        internal static bool HasManifestChanged(Dictionary<string, DateTime> initial, Dictionary<string, DateTime> current)
        {
            return initial != null && current.Any(item =>
                !initial.TryGetValue(item.Key, out var previous) || previous != item.Value);
        }
    }
}
