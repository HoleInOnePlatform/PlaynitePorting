using System;

namespace Playnite
{
    // OS-visible identity is independent of the retained Playnite SDK namespaces.
    public static class ApplicationIdentity
    {
        public const string Name = "HoleInOne";
        public const string DesktopProcessName = "HoleInOne";
        public const string FullscreenProcessName = "HoleInOne.Fullscreen";
        public const string BrowserProcessName = "HoleInOne.BrowserProcess";
        public const string InstanceMutex = "HoleInOneInstanceMutex";
        public const string PipeEndpoint = "net.pipe://localhost/HoleInOnePipe";
        public const string PipeService = "HoleInOneService";
        public const string AppUserModelId = "HoleInOne.Launcher";
        public const string UriScheme = "holeinone";
        public const string ExtensionProgId = "HoleInOne.ext";

        public static bool IsApplicationProcess(string processName)
        {
            return string.Equals(processName, DesktopProcessName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, FullscreenProcessName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
