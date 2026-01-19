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
        private static void EnsureWebOnlyChrome(string profileDir)
        {
          /*  if (string.IsNullOrWhiteSpace(profileDir))
                return;

            Directory.CreateDirectory(profileDir);

            // ✅ force existence prefs.js (certains forks ignorent user.js sinon)
            var prefsJsPath = Path.Combine(profileDir, "prefs.js");
            if (!File.Exists(prefsJsPath))
                File.WriteAllText(prefsJsPath, "// created by MyHomelabBrowser\n");

            // ✅ user.js (préférences forcées à chaque lancement)
            var userJsPath = Path.Combine(profileDir, "user.js");
            File.WriteAllText(userJsPath,
        @"user_pref(""toolkit.legacyUserProfileCustomizations.stylesheets"", true);
user_pref(""browser.tabs.autoHide"", true);
user_pref(""browser.fullscreen.autohide"", true);
");

            // ✅ chrome/userChrome.css
            var chromeDir = Path.Combine(profileDir, "chrome");
            Directory.CreateDirectory(chromeDir);

            File.WriteAllText(Path.Combine(chromeDir, "userChrome.css"),
        @"#navigator-toolbox { visibility: collapse !important; }
#TabsToolbar { visibility: collapse !important; }
#nav-bar { visibility: collapse !important; }
#toolbar-menubar { visibility: collapse !important; }
#PersonalToolbar { visibility: collapse !important; }
");

                
           
            */
        }


        public static void KillAllBasiliskProcesses()
        {
            foreach (var p in Process.GetProcessesByName("basilisk"))
            {
                try { p.Kill(true); } catch { }
            }
        }

        // ===============================
        // LANCEMENT
        // ===============================
        public Process? Launch(string url, string profileDir)
        {
            if (!CanLaunch())
                return null;

            EnsureWebOnlyChrome(profileDir);

            var exe = _settings.Settings.BasiliskPath;

            var args =
                $"--no-remote " +
                $"--profile \"{profileDir}\" " +
                $"\"{url}\"";

            if (Directory.Exists(profileDir))
            {
                // ⚠️ garde "FlashPlayerTrust" / plugin si tu en as
                // sinon wipe tout pour un profil vierge
                foreach (var f in Directory.GetFiles(profileDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
            }

            foreach (var pr in Process.GetProcessesByName("basilisk"))
            {
                try { pr.Kill(true); } catch { }
            }

            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true
            });
        }



    }
}
