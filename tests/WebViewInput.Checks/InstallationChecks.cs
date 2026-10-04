using Playnite;
using Playnite.Controllers;
using Playnite.Database;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.Settings;
using System;
using System.IO;
using System.Threading;

internal static class InstallationChecks
{
    public static void Run()
    {
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var database = new GameDatabase(Path.Combine(root, "library")))
        using (var controllers = new GameControllerFactory())
        using (var editor = new GamesEditor(database, controllers, new PlayniteSettings(), null, null, null, null))
        {
            database.OpenDatabase();
            var game = new Game { Name = "Installation handoff fixture", GameId = "fixture", IsInstalling = true };
            database.Games.Add(game);
            game.IsInstalling = true;
            var observedCompletion = false;
            game.PropertyChanged += (_, __) =>
            {
                // Observe every notification, including the intermediate state seen by UI bindings.
                if (!game.IsInstalling)
                {
                    if (!game.IsInstalled || game.InstallDirectory != root)
                        throw new Exception("Installation observer saw a stopped download before completion/path was published.");
                    observedCompletion = true;
                }
            };
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            FixtureInstaller installer;
            try { installer = new FixtureInstaller(game); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            controllers.AddController(installer);
            installer.Complete(root);
            if (!observedCompletion || controllers.GetInstallController(game.Id) != null)
                throw new Exception("Installation completion was not observed or controller was not released.");
            Console.WriteLine("PASS installation completion remains valid at every property notification before a game starts");
        }
    }

    private sealed class FixtureInstaller : InstallController
    {
        public FixtureInstaller(Game game) : base(game) { }
        public override void Install(InstallActionArgs args) { }
        public void Complete(string path) => InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = path }));
    }
}
