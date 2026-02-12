using MyHomelabBrowser.classes;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace MyHomelabBrowser
{
    public class LegacyLauncher
    {
        private readonly SettingsService _settings;
        public event Action<string>? OnDebug;
        private void Dbg(string msg) => OnDebug?.Invoke(msg);

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

        public static void KillAllBasiliskProcesses()
        {
            foreach (var p in Process.GetProcessesByName("basilisk"))
            {
                try { p.Kill(true); } catch { }
            }
        }
        // =====================
        // EnumWindows
        // =====================
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

       

        private void EnsureLegacyProfileReady(string profileDir)
        {
            void Log(string s) => Dbg("[LegacyProfile] " + s);

            if (string.IsNullOrWhiteSpace(profileDir))
            {
                Log("profileDir vide -> skip");
                return;
            }

            try
            {
                Log("Init profile: " + profileDir);

                Directory.CreateDirectory(profileDir);

                // chrome/
                string chromeDir = Path.Combine(profileDir, "chrome");
                Directory.CreateDirectory(chromeDir);

                // userChrome.css (WriteIfMissing)
                string userChromePath = Path.Combine(chromeDir, "userChrome.css");
                if (!File.Exists(userChromePath))
                {
                    File.WriteAllText(userChromePath,
        @"#navigator-toolbox { visibility: collapse !important; }
#TabsToolbar { visibility: collapse !important; }
#nav-bar { visibility: collapse !important; }
#toolbar-menubar { visibility: collapse !important; }
#PersonalToolbar { visibility: collapse !important; }
");
                    Log("Créé: chrome/userChrome.css");
                }
                else
                {
                    Log("Existe déjà: chrome/userChrome.css (pas écrasé)");
                }

                // user.js (WriteIfMissing)
                string userJsPath = Path.Combine(profileDir, "user.js");
                if (!File.Exists(userJsPath))
                {
                    File.WriteAllText(userJsPath,
        @"user_pref(""toolkit.legacyUserProfileCustomizations.stylesheets"", true);
user_pref(""browser.tabs.autoHide"", true);
user_pref(""browser.fullscreen.autohide"", true);
user_pref(""app.update.auto"", false);
user_pref(""app.update.enabled"", false);
");
                    Log("Créé: user.js");
                }
                else
                {
                    Log("Existe déjà: user.js (pas écrasé)");
                }

                // prefs.js (ne pas écraser, juste garantir les prefs)
                string prefsJsPath = Path.Combine(profileDir, "prefs.js");
                if (!File.Exists(prefsJsPath))
                {
                    File.WriteAllText(prefsJsPath, "// created by MyHomelabBrowser\n");
                    Log("Créé: prefs.js");
                }
                else
                {
                    Log("Existe déjà: prefs.js");
                }

                // lire prefs.js
                string prefsContent = "";
                try { prefsContent = File.ReadAllText(prefsJsPath); }
                catch (Exception ex)
                {
                    Log("Impossible de lire prefs.js: " + ex.Message);
                    prefsContent = "";
                }

                // ✅ garantir userChrome enabled
                if (!prefsContent.Contains("toolkit.legacyUserProfileCustomizations.stylesheets", StringComparison.Ordinal))
                {
                    File.AppendAllText(prefsJsPath,
                        "user_pref(\"toolkit.legacyUserProfileCustomizations.stylesheets\", true);\n");
                    Log("Ajout pref dans prefs.js: toolkit.legacyUserProfileCustomizations.stylesheets=true");
                }
                else
                {
                    Log("Pref déjà présente dans prefs.js");
                }

                // ✅ garantir stop updates (backup dans prefs.js)
                if (!prefsContent.Contains("app.update.auto", StringComparison.Ordinal))
                {
                    File.AppendAllText(prefsJsPath, "user_pref(\"app.update.auto\", false);\n");
                    Log("Ajout pref dans prefs.js: app.update.auto=false");
                }

                if (!prefsContent.Contains("app.update.enabled", StringComparison.Ordinal))
                {
                    File.AppendAllText(prefsJsPath, "user_pref(\"app.update.enabled\", false);\n");
                    Log("Ajout pref dans prefs.js: app.update.enabled=false");
                }

                DeleteIfExists(Path.Combine(profileDir, "parent.lock"), Log);
                DeleteIfExists(Path.Combine(profileDir, "lock"), Log);
                DeleteIfExists(Path.Combine(profileDir, ".parentlock"), Log);

                Log("OK");
            }
            catch (Exception ex)
            {
                Log("FAILED: " + ex);
            }
        }

        private static void DeleteIfExists(string path, Action<string> log)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    log("Supprimé lock: " + Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                log("Impossible de supprimer " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        // ===============================
        // LANCEMENT
        // ===============================
        public Process? Launch(string url, string profileDir)
        {
            if (!CanLaunch())
                return null;

            var exe = _settings.Settings.BasiliskPath;

            if (string.IsNullOrWhiteSpace(profileDir))
                throw new ArgumentException(nameof(profileDir));

            // ✅ préparer le profil fourni par l'appelant
            EnsureLegacyProfileReady(profileDir);

            // ✅ args : PROFIL EXPLICITE
            string args = $"-new-instance -no-remote  -profile \"{profileDir}\" \"{url}\"";


            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false
            });
        }




    }
}