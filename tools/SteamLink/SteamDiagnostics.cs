using System.Globalization;

namespace HoleInOne.SteamLink;

// Accept only explicit diagnostic fields. Never pass credentials, URLs, response bodies or exception messages.
internal static class SteamDiagnostics
{
    private static readonly string Attempt = Guid.NewGuid().ToString("N");
    internal static Action<string> Sink = Append;
    internal static void Write(string stage, string outcome, int? result = null, int? httpStatus = null)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("HOLEINONE_STEAM_DIAGNOSTICS") != "1") return;
            var line = $"{DateTimeOffset.UtcNow:O} attempt={Attempt} stage={stage} outcome={outcome} result={result?.ToString(CultureInfo.InvariantCulture) ?? "-"} http={httpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
            Sink(line);
        }
        catch (Exception) { /* Diagnostics must never change install behavior. */ }
    }
    private static void Append(string line)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoleInOne", "Logs");
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, $"steam-debug-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}.log"), line + Environment.NewLine);
    }
}
