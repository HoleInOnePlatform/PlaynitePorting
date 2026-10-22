using System.Diagnostics;

namespace HoleInOne.SteamLink;

public static class NativeSteamProcess
{
    public static Process Start(string steamExe) => Process.Start(new ProcessStartInfo(steamExe)
    {
        WorkingDirectory = Path.GetDirectoryName(steamExe)!,
        // Steam is an independent GUI application. CreateProcess with inherited
        // handles would let its output corrupt/block the helper's private JSON pipe.
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        Arguments = "-silent"
    }) ?? throw new InvalidOperationException("STEAM_START_FAILED");
}
