using Playnite.Controllers;
using Playnite.Database;
using Playnite.DesktopApp.ViewModels;
using Playnite.DesktopApp.Windows;
using Playnite.Plugins;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Settings;
using Playnite.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Playnite.DesktopApp
{
    public class DesktopGamesEditor : GamesEditor
    {
        public DesktopGamesEditor(
            GameDatabase database,
            GameControllerFactory controllerFactory,
            PlayniteSettings appSettings,
            IDialogsFactory dialogs,
            ExtensionFactory extensions,
            PlayniteApplication app,
            IActionSelector actionSelector) : base(
                database,
                controllerFactory,
                appSettings,
                dialogs,
                extensions,
                app,
                actionSelector)
        {
        }

        private HoleInOne.InstantPlayCoordinator instantPlay;
        public override void StartContextAction(Game game) => PlayGame(game, true);
        public override void PlayGame(Game game, bool launchedFromUI, int actionIndex = -1)
        {
            if (instantPlay == null) instantPlay = new HoleInOne.InstantPlayCoordinator(this,
                (local) => base.PlayGame(local, true));
            if (instantPlay.Focus(game.Id)) return;
            if (game.IsInstalled) { base.PlayGame(game, launchedFromUI, actionIndex); return; }
            instantPlay.Start(game);
        }

        public bool? SetGameCategories(Game game)
        {
            var model = new CategoryConfigViewModel(new CategoryConfigWindowFactory(), Database, game);
            return model.OpenView();
        }

        public bool? SetGamesCategories(List<Game> games)
        {
            var model = new CategoryConfigViewModel(new CategoryConfigWindowFactory(), Database, games);
            return model.OpenView();
        }

        public bool? EditGame(Game game)
        {
            var model = new GameEditViewModel(
                            game,
                            Database,
                            new GameEditWindowFactory(),
                            Dialogs,
                            new ResourceProvider(),
                            Extensions,
                            AppSettings);
            return model.OpenView();
        }

        public bool? EditGames(List<Game> games)
        {
            var model = new GameEditViewModel(
                            games,
                            Database,
                            new GameEditWindowFactory(),
                            Dialogs,
                            new ResourceProvider(),
                            Extensions,
                            AppSettings);
            return model.OpenView();
        }
    }
}
