using Playnite.Controls;
using Playnite.DesktopApp.ViewModels;
using System.Windows;

namespace Playnite.DesktopApp.Windows
{
    public partial class GameDetailsWindow : WindowBase
    {
        private readonly GamesCollectionViewEntry entry;
        private readonly DesktopAppViewModel mainModel;

        public GameDetailsWindow(GamesCollectionViewEntry entry, DesktopAppViewModel mainModel)
        {
            InitializeComponent();
            this.entry = entry;
            this.mainModel = mainModel;
            DataContext = entry;
            Title = entry.DisplayName;
            PlayButton.IsEnabled = entry.IsInstalled && !entry.IsRunning && !entry.IsLaunching;
            InstallButton.IsEnabled = !entry.IsInstalled && !entry.IsInstalling;
            if (entry.IsCustomGame && !entry.IsInstalled)
            {
                InstallButton.Content = TryFindResource("LOCEditGame") ?? InstallButton.Content;
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            mainModel.StartGameCommand.Execute(entry.Game);
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            if (entry.IsCustomGame)
            {
                mainModel.EditGameCommand.Execute(entry.Game);
            }
            else
            {
                mainModel.InstallGameCommand.Execute(entry.Game);
            }
        }
    }
}
