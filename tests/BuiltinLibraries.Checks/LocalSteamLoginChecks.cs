using Microsoft.Win32;
using Moq;
using Playnite.HoleInOne;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SteamLibrary;
using System;
using System.Threading;

internal static class LocalSteamLoginChecks
{
    internal static void Run()
    {
        // This fixture edits only the disposable Windows CI user's registry, never a developer's PC.
        if (System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("Run the registry fixture only on disposable GitHub Windows runners.");
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam\ActiveProcess"))
        {
            var previous = key.GetValue("ActiveUser");
            var previousKind = previous == null ? RegistryValueKind.DWord : key.GetValueKind("ActiveUser");
            var previousContext = SynchronizationContext.Current;
            try
            {
                SteamLinkContext.Configure("https://fixture.invalid/", "fixture", "fixture");
                typeof(SteamLinkContext).GetProperty("Current").SetValue(null,
                    new SteamLinkInfo { State = "linked", SteamId = "76561197960265739", Version = "fixture" });
                key.SetValue("ActiveUser", 0, RegistryValueKind.DWord);
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                var api = new Mock<IPlayniteAPI>();
                var dialogs = new Mock<IDialogsFactory>();
                api.SetupGet(x => x.Dialogs).Returns(dialogs.Object);
                using (var installer = new SteamInstallController(new Game { GameId = "2868840", Name = "Fixture" }, api.Object))
                {
                    // The current install controller reports failures through the UI and cancellation,
                    // because storage preparation made Install asynchronous on the base branch.
                    installer.Install(new InstallActionArgs());
                    dialogs.Verify(x => x.ShowErrorMessage(It.Is<string>(m => m.Contains("로그인")), It.IsAny<string>()), Times.Once());
                    Console.WriteLine("PASS installation reports missing PC login before starting Steam or storage work");
                }
                var requests = 0;
                var downloading = false;
                var download = new LocalDownloadSession(() => false, () => downloading, SteamLinkContext.RequireLocalAccount,
                    () => { requests++; downloading = true; });
                download.Request();
                if (requests != 0 || !download.CanRetry || !download.Message.Contains("PC"))
                    throw new Exception("Missing PC Steam login did not become a retryable local wait.");
                key.SetValue("ActiveUser", 12, RegistryValueKind.DWord);
                download.Request();
                if (requests != 0) throw new Exception("Another Steam account could install.");
                key.SetValue("ActiveUser", 11, RegistryValueKind.DWord);
                download.Request();
                if (requests != 1 || download.CanRetry) throw new Exception("Same-account login did not resume installation.");
                Console.WriteLine("PASS real Windows registry guard: missing/mismatched PC login waits; expected login resumes the same download");
            }
            finally
            {
                SteamLinkContext.Forget();
                if (previous == null) key.DeleteValue("ActiveUser", false);
                else key.SetValue("ActiveUser", previous, previousKind);
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }
}
