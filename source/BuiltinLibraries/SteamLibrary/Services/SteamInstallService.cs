using Playnite.SDK;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SteamLibrary.Services
{
    internal sealed class SteamInstallLocation
    {
        public int Index { get; set; }
        public string Path { get; set; }
        public override string ToString()
        {
            try
            {
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(Path));
                return $"{Path} — 여유 공간 {drive.AvailableFreeSpace / (1024d * 1024 * 1024):N1} GB";
            }
            catch (IOException) { return Path; }
            catch (UnauthorizedAccessException) { return Path; }
        }
    }

    internal static class SteamInstallService
    {
        // Keep Steam's volume IDs: filtering unavailable drives must never renumber them.
        internal static List<SteamInstallLocation> ReadLocations(KeyValue data, string steamPath)
        {
            var locations = new List<SteamInstallLocation>();
            foreach (var entry in data.Children)
            {
                if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
                var path = string.IsNullOrWhiteSpace(entry.Value) ? entry["path"].Value : entry.Value;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path)) continue;
                if (locations.Any(location => location.Index == index))
                    throw new InvalidDataException("Steam 저장 위치 정보가 중복됨. Steam을 실행한 뒤 다시 시도해 주세요.");
                locations.Add(new SteamInstallLocation { Index = index, Path = path });
            }
            // Legacy libraryfolders.vdf lists only additional libraries.
            if (!locations.Any(location => location.Index == 0))
                locations.Add(new SteamInstallLocation { Index = 0, Path = steamPath });
            return locations.OrderBy(location => location.Index).ToList();
        }

        internal static List<SteamInstallLocation> GetLocations()
        {
            var data = new KeyValue();
            var file = System.IO.Path.Combine(Steam.InstallationPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(file))
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!data.ReadAsText(stream)) throw new InvalidDataException("Steam 저장 위치 정보를 읽지 못함.");
                }
            }
            return ReadLocations(data, Steam.InstallationPath).Where(location => Directory.Exists(location.Path)).ToList();
        }

        internal static string BuildArguments(uint appId, int volumeIndex)
        {
            if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
            if (volumeIndex < 0) throw new ArgumentOutOfRangeException(nameof(volumeIndex));
            // app_install bypasses Steam's install wizard; -silent suppresses the main window.
            return string.Format(CultureInfo.InvariantCulture, "-silent +app_install {0} {1}", appId, volumeIndex);
        }

        internal static SteamInstallLocation SelectLocation(IPlayniteAPI api, string gameName)
        {
            var locations = GetLocations();
            if (locations.Count == 0) throw new IOException("사용 가능한 Steam 저장 위치가 없음. 설치 드라이브 연결을 확인해 주세요.");
            if (locations.Count == 1) return locations[0];

            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = false });
            window.Title = "HoleInOne · 다운로드 위치";
            window.Width = 620;
            window.SizeToContent = SizeToContent.Height;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(item => item.IsActive);
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = gameName + " 다운로드 위치 선택", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            var choices = new ComboBox { ItemsSource = locations, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 20) };
            panel.Children.Add(choices);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            var download = new Button { Content = "다운로드", IsDefault = true, MinWidth = 110 };
            download.Click += (_, __) => window.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(download);
            panel.Children.Add(buttons);
            window.Content = panel;
            return window.ShowDialog() == true ? (SteamInstallLocation)choices.SelectedItem : null;
        }
    }
}
