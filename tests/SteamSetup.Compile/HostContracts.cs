// Compile-only host contracts. WPF/.NET Framework APIs and the production Steam integration sources are real.
// This is not a substitute for building/running the full desktop application on Windows.
using System;
namespace Playnite { public static class PlaynitePaths { public static string ConfigRootPath => "unused"; } }
namespace Playnite.Controls { public class WindowBase : System.Windows.Window { public bool ShowMinimizeButton {get;set;} public bool ShowMaximizeButton {get;set;} } }
namespace Playnite.SDK { public static class LogManager { public static Logger GetLogger()=>new Logger(); } public sealed class Logger {public void Warn(string value){} public void Info(string value){} } }
namespace Playnite.HoleInOne { public static class BuiltinStoreLibrary { public static Guid SteamId=>Guid.Empty; public static Guid EpicId=>Guid.Empty; } }
namespace Playnite.DesktopApp {
 public sealed class DesktopApplication { public static DesktopApplication Current=>new DesktopApplication();public Model MainModel=>new Model(); public Database Database=>new Database(); }
 public sealed class Database { public System.Collections.Generic.List<Game> Games=>new System.Collections.Generic.List<Game>(); }
 public sealed class Game { public Guid Id {get;set;} public string Name {get;set;} public string InstallDirectory {get;set;} public bool IsInstalled {get;set;} }
 public sealed class Model { public void OpenPluginSettings(Guid id){} }
}
namespace Playnite.DesktopApp.HoleInOne { public static class InstantPlayCoordinator { public static int ActiveStreamCount=>0; } }
namespace Playnite.DesktopApp.HoleInOne { public sealed class SaveRecoveryWindow : System.Windows.Window { } }
namespace Playnite.DesktopApp.HoleInOne {
 public sealed class ExportReceipts { public ExportReceipts(Playnite.HoleInOne.LauncherSettings settings){} public void Start(){} }
}
namespace Playnite.HoleInOne {
 public static class StorageManagement {
  public static bool IsBusy=>false;
  public static ulong Used(LauncherSettings settings, System.Collections.Generic.IEnumerable<Playnite.DesktopApp.Game> games)=>0;
 }
}
namespace Playnite.DesktopApp.Windows {
 public sealed class MessageBoxWindow {
  public System.Windows.MessageBoxResult Show(System.Windows.Window owner, string text, string title,
   System.Windows.MessageBoxButton buttons, System.Windows.MessageBoxImage image,
   System.Windows.MessageBoxResult result, System.Windows.MessageBoxOptions options)=>result;
 }
}
