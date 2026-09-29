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
            PlayButton.IsEnabled = !entry.IsRunning && !entry.IsLaunching && !entry.IsInstalling;
            LocalInstallButton.Visibility = entry.IsInstalled || entry.IsCustomGame ? Visibility.Collapsed : Visibility.Visible;
            LocalInstallButton.IsEnabled = !entry.IsInstalling && !entry.IsUninstalling;
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            mainModel.StartGameCommand.Execute(entry.Game);
        }

        private void LocalInstallButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            mainModel.InstallGameCommand.Execute(entry.Game);
        }

    }
}
