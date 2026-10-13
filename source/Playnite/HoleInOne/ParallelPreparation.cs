using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    public static class ParallelPreparation
    {
        public static async Task<T> RunAsync<T>(Func<CancellationToken, T> prepare,
            Func<CancellationToken, Task> synchronize, CancellationToken ct)
        {
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var prepared = Task.Run(() =>
                {
                    try { return prepare(lifetime.Token); }
                    catch { lifetime.Cancel(); throw; }
                }, lifetime.Token);
                var synchronized = SynchronizeAsync(synchronize, lifetime);
                // Join both before disposing resources or allowing the caller to commit.
                await Task.WhenAll(prepared, synchronized).ConfigureAwait(false);
                return await prepared.ConfigureAwait(false);
            }
        }
        private static async Task SynchronizeAsync(Func<CancellationToken, Task> synchronize, CancellationTokenSource lifetime)
        {
            try { await synchronize(lifetime.Token).ConfigureAwait(false); }
            catch { lifetime.Cancel(); throw; }
        }
    }
}
