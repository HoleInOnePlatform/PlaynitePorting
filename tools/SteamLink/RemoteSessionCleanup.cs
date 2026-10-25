namespace HoleInOne.SteamLink;

internal static class RemoteSessionCleanup
{
    // Every cleanup step must run without replacing the request outcome.
    internal static async Task RunAsync(Action logOff, Action disconnect, Action stop, Task pump)
    {
        Attempt(logOff, "LogOff");
        Attempt(disconnect, "Disconnect");
        Attempt(stop, "StopCallbacks");
        try { await pump; }
        catch (Exception) { SteamDiagnostics.Write("CallbackPump", "cleanup_failed"); }
    }

    private static void Attempt(Action action, string stage)
    {
        try { action(); }
        catch (Exception) { SteamDiagnostics.Write(stage, "cleanup_failed"); }
    }
}
