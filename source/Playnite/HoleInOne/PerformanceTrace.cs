#nullable disable
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    // Only call-site labels/opaque IDs belong here, never URLs, credentials or exception messages.
    public sealed class PerformanceTrace
    {
        private readonly Action<string> write;
        private readonly string flow;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int sequence;
        public string Id { get; }
        public PerformanceTrace(string flow, Action<string> write, string id = null)
        { this.flow = flow; this.write = write; Id = id ?? Guid.NewGuid().ToString("N"); }
        public Step Begin(string stage) => new Step(this, stage, Interlocked.Increment(ref sequence));
        public T Run<T>(string stage, Func<T> action)
        {
            using (var step = Begin(stage))
            {
                try { var result = action(); step.Complete(); return result; }
                catch (Exception error) { step.Fail(error); throw; }
            }
        }
        public void Run(string stage, Action action) => Run(stage, () => { action(); return true; });
        public async Task<T> RunAsync<T>(string stage, Func<Task<T>> action)
        {
            using (var step = Begin(stage))
            {
                try { var result = await action().ConfigureAwait(false); step.Complete(); return result; }
                catch (Exception error) { step.Fail(error); throw; }
            }
        }
        public Task RunAsync(string stage, Func<Task> action) => RunAsync(stage, async () =>
        { await action().ConfigureAwait(false); return true; });
        public sealed class Step : IDisposable
        {
            private readonly PerformanceTrace trace;
            private readonly string stage;
            private readonly int number;
            private readonly Stopwatch watch = Stopwatch.StartNew();
            private int ended;
            internal Step(PerformanceTrace trace, string stage, int number)
            { this.trace = trace; this.stage = stage; this.number = number; Emit("begin", "pending"); }
            private void Emit(string kind, string outcome)
            {
                try
                {
                    trace.write?.Invoke(string.Format(CultureInfo.InvariantCulture,
                        "[HIO-PERF] flow={0} trace={1} span={2} stage={3} event={4} elapsed_ms={5} total_ms={6} outcome={7}",
                        trace.flow, trace.Id, number, stage, kind, watch.ElapsedMilliseconds, trace.clock.ElapsedMilliseconds, outcome));
                }
                catch (Exception) { } // Diagnostics must never change game/save behavior.
            }
            private void End(string outcome)
            { if (Interlocked.Exchange(ref ended, 1) == 0) Emit("end", outcome); }
            public void Complete() => End("ok");
            public void Fail(Exception error) => End(error is OperationCanceledException ? "cancelled" : "failed");
            public void Dispose() => End("interrupted");
        }
    }
}
