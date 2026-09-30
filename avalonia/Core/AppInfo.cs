using System;

namespace PommeBrowser.Core
{
    /// <summary>Version et système, pour l'affichage (menu, diagnostic, rapports).</summary>
    public static class AppInfo
    {
        public static string DisplayVersion => MyHomelabBrowser.AppVersion.Current;

        public static string Platform =>
            OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsMacOS() ? "macOS" :
            OperatingSystem.IsLinux() ? "Linux" : Environment.OSVersion.Platform.ToString();
    }
}
