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
            _path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MyHomelabBrowser",
                "settings.json"
            );

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
                if (!File.Exists(_path))
                    return CreateDefaults();

                return JsonSerializer.Deserialize<BrowserSettings>(
                    File.ReadAllText(_path)
                ) ?? CreateDefaults();
            }
            catch
            {
                return CreateDefaults();
            }
        }

        BrowserSettings CreateDefaults()
        {
            return new BrowserSettings
            {
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
