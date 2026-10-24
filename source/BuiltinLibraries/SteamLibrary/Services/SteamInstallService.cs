using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SteamLibrary.Services
{
    internal static class SteamInstallService
    {
        internal static string BuildArguments(uint appId)
        {
            if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
            // Leave destination selection to Steam. Never supply a library/volume override.
            return string.Format(CultureInfo.InvariantCulture, "-silent +app_install {0}", appId);
        }

        // Read-only discovery for progress tracking; these folders never select the install destination.
        internal static Dictionary<string, DateTime> CaptureManifests(uint appId, IEnumerable<string> folders)
        {
            var manifests = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                var file = Path.Combine(folder, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(file)) manifests[file] = File.GetLastWriteTimeUtc(file);
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
