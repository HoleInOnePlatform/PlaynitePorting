// Compile-only host contracts. WPF/.NET Framework APIs and the production Steam integration sources are real.
// This is not a substitute for building/running the full desktop application on Windows.
using System;
namespace Playnite { public static class PlaynitePaths { public static string ConfigRootPath => "unused"; } }
namespace Playnite.Controls { public class WindowBase : System.Windows.Window { public bool ShowMinimizeButton {get;set;} public bool ShowMaximizeButton {get;set;} } }
namespace Playnite.SDK { public static class LogManager { public static Logger GetLogger()=>new Logger(); } public sealed class Logger {public void Warn(string value){} } }
namespace Playnite.HoleInOne { public static class BuiltinStoreLibrary { public static Guid SteamId=>Guid.Empty; public static Guid EpicId=>Guid.Empty; } }
namespace Playnite.DesktopApp {
 public sealed class DesktopApplication { public static DesktopApplication Current=>new DesktopApplication();public Model MainModel=>new Model(); }
 public sealed class Model { public void OpenPluginSettings(Guid id){} }
}
namespace Playnite.DesktopApp.HoleInOne { public static class InstantPlayCoordinator { public static int ActiveStreamCount=>0; } }
