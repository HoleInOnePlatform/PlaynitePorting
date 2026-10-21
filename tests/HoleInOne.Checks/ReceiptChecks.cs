using Playnite.HoleInOne;

internal static class ReceiptChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "receipts-" + Guid.NewGuid().ToString("N"));
        const string address = "http://localhost:5010/";
        var hash = new string('a', 64);
        try
        {
            var queue = new ExportReceiptOutbox(root, address, "account:one");
            queue.Enqueue("session", "request", hash);
            queue.Enqueue("session", "request", hash);
            check(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 1, "receipt enqueue is durable and idempotent");
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var sending = queue.DrainAsync(async (receipt, ct) =>
            {
                check(receipt.SessionId == "session" && receipt.RequestId == "request" && receipt.Sha256 == hash, "receipt identity preserved");
                entered.SetResult(true);
                await release.Task;
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!sending.IsCompleted && Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 1, "slow response retains pending receipt without blocking caller");
            release.SetResult(true);
            await sending;
            check(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0, "successful receipt removes durable work");
            queue.Enqueue("session", "request", hash);
            var attempts = 0;
            await queue.DrainAsync((_, ct) => { attempts++; throw new HttpRequestException(); }, default, (_, ct) => Task.CompletedTask);
            check(attempts == 3 && Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 1, "failed receipt retries and survives for restart");
            attempts = 0;
            await queue.DrainAsync((_, ct) =>
            {
                if (++attempts < 3) throw new HttpRequestException("Lost acknowledgement");
                return Task.CompletedTask;
            }, default, (_, ct) => Task.CompletedTask);
            check(attempts == 3 && Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0, "transient failures are retried through successful acknowledgement");
            queue.Enqueue("session", "request", hash);
            var foreignCalls = 0;
            foreach (var foreign in new[] { new ExportReceiptOutbox(root, address, "account:two"), new ExportReceiptOutbox(root, "http://localhost:5011/", "account:one") })
                await foreign.DrainAsync((_, ct) => { foreignCalls++; return Task.CompletedTask; }, default);
            check(foreignCalls == 0, "receipts never replay to another account or service");
            var resumed = new ExportReceiptOutbox(root, address, "account:one");
            await resumed.DrainAsync((_, ct) => Task.CompletedTask, default);
            check(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0, "new queue instance resumes persisted work");
            queue.Enqueue("session", "request", hash);
            attempts = 0;
            await queue.DrainAsync((_, ct) => { attempts++; throw new LoginRequiredException("expired"); }, default);
            check(attempts == 1 && Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 1, "expired login retains receipt without repeated unauthorized calls");
            using var cancel = new CancellationTokenSource();
            try
            {
                await queue.DrainAsync((_, ct) => { cancel.Cancel(); return Task.FromCanceled(ct); }, cancel.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { check(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 1, "shutdown preserves pending receipt"); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
