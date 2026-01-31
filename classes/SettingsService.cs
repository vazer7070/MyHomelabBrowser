using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes
{
    public class SettingsService
    {
        readonly string _path;

        public BrowserSettings Settings { get; set; }

     
        public event Action<BrowserSettings>? SettingsChanged;

        public SettingsService()
        {
            _path = MyHomelabBrowser.classes.Profiles.AppDataContext.GetPath("settings.json");


            Settings = Load();
        }
        public void Apply(BrowserSettings settings)
        {
            Settings = settings;
            Save();
            SettingsChanged?.Invoke(Settings);
        }

        BrowserSettings Load()
        {
            try
            {
                BrowserSettings s;

                if (!File.Exists(_path))
                {
                    s = CreateDefaults();
                }
                else
                {
                    s = JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(_path))
                        ?? CreateDefaults();
                }

                // ✅ BasiliskPath par défaut (1er lancement / chemin invalide)
                try
                {
                    string defaultBasilisk = GetDefaultBasiliskPortablePath();

                    if (string.IsNullOrWhiteSpace(s.BasiliskPath) || !File.Exists(s.BasiliskPath))
                    {
                        if (File.Exists(defaultBasilisk))
                            s.BasiliskPath = defaultBasilisk;
                    }
                }
                catch { }

                return s;
            }
            catch
            {
                var s = CreateDefaults();

                // ✅ BasiliskPath par défaut même si JSON cassé
                try
                {
                    string defaultBasilisk = GetDefaultBasiliskPortablePath();
                    if (File.Exists(defaultBasilisk))
                        s.BasiliskPath = defaultBasilisk;
                }
                catch { }

                return s;
            }
        }

        BrowserSettings CreateDefaults()
        {
            return new BrowserSettings
            {
                BasiliskPath = GetDefaultBasiliskPortablePath(),

                EnableSuspension = true,
                SuspendDelayMinutes = 5,
                StartPage = "https://google.com",
                NewTabPage = "https://duckduckgo.com",
                EnableCommands = true,
                Commands = new List<CommandSetting>
        {
            new() { Key="new", Description="Nouvel onglet", Enabled=true },
            new() { Key="close", Description="Fermer onglet", Enabled=true },
            new() { Key="close others", Description="Fermer les autres", Enabled=true },
            new() { Key="reload", Description="Recharger", Enabled=true },
            new() { Key="suspend", Description="Suspendre onglet", Enabled=true },
            new() { Key="resume", Description="Réactiver onglet", Enabled=true },
        }
            };
        }
        private static string GetDefaultBasiliskPortablePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "PommeBrowser", "Basilisk", "Basilisk-Portable.exe");
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true })
            );

            SettingsChanged?.Invoke(Settings);
        }

    }
}
