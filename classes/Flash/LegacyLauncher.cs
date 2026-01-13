using MyHomelabBrowser.classes;
using System.Diagnostics;
using System.IO;

namespace MyHomelabBrowser
{
    public class LegacyLauncher
    {
        private readonly SettingsService _settings;

        public LegacyLauncher(SettingsService settings)
        {
            _settings = settings;
        }

        // ===============================
        // VERIFICATION
        // ===============================
        public bool CanLaunch()
        {
            var s = _settings.Settings;

            if (!s.EnableFlashSupport)
                return false;

            if (string.IsNullOrWhiteSpace(s.BasiliskPath))
                return false;

            return File.Exists(s.BasiliskPath);
        }

        // ===============================
        // LANCEMENT
        // ===============================
        public Process? Launch(string url, string profileDir)
        {
            if (!CanLaunch())
                return null;

            var exe = _settings.Settings.BasiliskPath;

            var args =
                $"--profile \"{profileDir}\" " +
                $"\"{url}\"";

            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true
            });
        }
    }
}
