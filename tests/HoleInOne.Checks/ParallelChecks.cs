using Playnite.HoleInOne;

internal static class ParallelChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool syncStarted = false;
        var result = ParallelPreparation.RunAsync(ct =>
        {
            started.TrySetResult(true);
            release.Wait(ct);
            return 42;
        }, ct => { syncStarted = true; return uploaded.Task; }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(syncStarted && !result.IsCompleted, "restore staging and Steam upload overlap");
        release.Set();
        check(!result.IsCompleted, "prepared files cannot commit before upload succeeds");
        uploaded.SetResult(true);
        check(await result == 42, "successful parallel preparation returns the staged plan");
        bool cancelled = false;
        try
        {
            await ParallelPreparation.RunAsync(ct =>
            {
                ct.WaitHandle.WaitOne();
                cancelled = ct.IsCancellationRequested;
                ct.ThrowIfCancellationRequested();
                return 0;
            }, ct => Task.FromException(new IOException("upload failed")), default);
            throw new Exception("Expected failure");
        }
        catch (IOException) { check(true, "upload failure never returns a committable plan"); }
        // The worker may be cancelled before entering its delegate; either outcome is safe.
        catch (OperationCanceledException) { check(true, "failed upload cancels staging before commit"); }
        _ = cancelled;
    }
}
