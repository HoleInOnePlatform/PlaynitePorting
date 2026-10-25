using SteamLibrary.Services;

internal static class RemovalChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("removal retries transient reads before confirming absence", async () =>
        {
            var reads = 0;
            await SteamInstallService.WaitForRemovalAsync(() => ++reads switch
            {
                1 => throw new IOException(),
                2 => throw new UnauthorizedAccessException(),
                3 => throw new FormatException(),
                4 => true,
                _ => false
            }, default, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));
            if (reads != 5) throw new Exception("Removal confirmed before a successful absent read");
        });
        await check("persistent removal read failures time out", async () =>
        {
            try
            {
                await SteamInstallService.WaitForRemovalAsync(() => throw new IOException(), default,
                    TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(1));
                throw new Exception("Unreadable state counted as removal");
            }
            catch (TimeoutException) { }
        });
        await check("cancellation during removal read prevents completion", async () =>
        {
            using var stop = new CancellationTokenSource();
            try
            {
                await SteamInstallService.WaitForRemovalAsync(() => { stop.Cancel(); return false; }, stop.Token,
                    TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));
                throw new Exception("Cancelled removal completed");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        await check("missing library is unknown while readable empty library confirms absence", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "SteamRemoval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                try
                {
                    SteamInstallService.CaptureManifests(42, new[] { root }, requireReadable: true);
                    throw new Exception("Missing library counted as empty");
                }
                catch (DirectoryNotFoundException) { }
                Directory.CreateDirectory(Path.Combine(root, "steamapps"));
                if (SteamInstallService.CaptureManifests(42, new[] { root }, true).Count != 0)
                    throw new Exception("Empty library counted as installed");
                File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_42.acf"), "fixture");
                if (SteamInstallService.CaptureManifests(42, new[] { root }, true).Count != 1)
                    throw new Exception("Existing manifest missed");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
    }
}
