using Playnite.HoleInOne;
using System.Collections.Concurrent;

internal static class PerformanceChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var lines = new ConcurrentQueue<string>();
        var trace = new PerformanceTrace("test", lines.Enqueue);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = trace.RunAsync("slow", () => release.Task);
        check(lines.Any(l => l.Contains("stage=slow event=begin")) && !waiting.IsCompleted,
            "timing records a blocked step before it completes");
        check(await trace.RunAsync("fast", () => Task.FromResult(42)) == 42 && !waiting.IsCompleted,
            "timing preserves concurrent operation results without serializing");
        release.SetResult(7);
        check(await waiting == 7, "timing preserves awaited return values");
        var expected = new IOException("secret-token https://private.example");
        try { await trace.RunAsync("failure", () => Task.FromException(expected)); }
        catch (IOException actual) { check(ReferenceEquals(expected, actual), "timing preserves the original exception"); }
        try { await trace.RunAsync("cancel", () => Task.FromCanceled(new CancellationToken(true))); }
        catch (OperationCanceledException) { }
        using (var step = trace.Begin("once")) { step.Complete(); step.Complete(); }
        using (trace.Begin("abandoned")) { }
        check(lines.Count(l => l.Contains("stage=once event=end")) == 1,
            "step completion is logged once even when disposed");
        check(lines.Any(l => l.Contains("stage=cancel event=end") && l.EndsWith("outcome=cancelled")) &&
            lines.Any(l => l.Contains("stage=failure event=end") && l.EndsWith("outcome=failed")) &&
            lines.Any(l => l.Contains("stage=abandoned event=end") && l.EndsWith("outcome=interrupted")),
            "timing distinguishes cancellation, failure and incomplete steps");
        check(lines.All(l => !l.Contains("secret-token") && !l.Contains("private.example")),
            "timing never logs exception text or URLs");
        var brokenLog = new PerformanceTrace("test", _ => throw new IOException("logger unavailable"));
        check(await brokenLog.RunAsync("still_works", () => Task.FromResult(9)) == 9,
            "logging failure cannot fail game operations");
    }
}
