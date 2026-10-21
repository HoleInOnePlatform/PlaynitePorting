using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SteamLibrary;
using System;
using System.Threading;

internal static class SteamUninstallChecks
{
    // Replace the real launch entirely: these checks never start Steam or delete a game.
    private sealed class FakeSteamController : SteamUninstallController
    {
        internal Action Request;
        internal FakeSteamController(Action request) : base(new Game()) { Request = request; }
        public override void Uninstall(UninstallActionArgs args) { Request(); }
    }

    private sealed class LegacyController : UninstallController
    {
        internal int ThreadId;
        internal LegacyController() : base(new Game()) { }
        public override void Uninstall(UninstallActionArgs args) { ThreadId = Thread.CurrentThread.ManagedThreadId; }
    }

    internal static void Run()
    {
        var caller = Thread.CurrentThread.ManagedThreadId;
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var worker = caller;
            using (var controller = new FakeSteamController(() =>
            {
                worker = Thread.CurrentThread.ManagedThreadId;
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Caller did not regain control.");
            }))
            {
                var request = controller.UninstallAsync(new UninstallActionArgs());
                try
                {
                    if (!entered.Wait(3000) || request.IsCompleted || worker == caller)
                        throw new Exception("Steam launch blocked the caller or used its thread.");
                }
                finally { release.Set(); }
                request.GetAwaiter().GetResult();
            }
        }
        using (var controller = new FakeSteamController(() => { throw new InvalidOperationException("launch failed"); }))
        {
            try
            {
                controller.UninstallAsync(new UninstallActionArgs()).GetAwaiter().GetResult();
                throw new Exception("Launch failure was swallowed.");
            }
            catch (InvalidOperationException error) when (error.Message == "launch failed") { }
        }
        using (var controller = new LegacyController())
        {
            controller.UninstallAsync(new UninstallActionArgs()).GetAwaiter().GetResult();
            if (controller.ThreadId != caller) throw new Exception("Existing controller thread changed.");
        }
        Console.WriteLine("3 uninstall checks passed (responsive caller, propagated failure, legacy thread preserved).");
    }
}
