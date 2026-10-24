using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SteamLibrary.Services
{
    internal static class SteamInstallService
    {
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
