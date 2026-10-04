using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes
{
    public class SettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public BrowserSettings Settings { get; private set; }

        public event Action<BrowserSettings>? SettingsChanged;

        public SettingsService()
        {
            Settings = LoadFromCurrentProfile();
        }

        private string SettingsPath => AppDataContext.GetPath("settings.json");

        public void Apply(BrowserSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            Settings = settings;
            SaveToCurrentProfile();
            SettingsChanged?.Invoke(Settings);
        }

        public void ReloadForCurrentProfile()
        {
            Settings = LoadFromCurrentProfile();
            SettingsChanged?.Invoke(Settings);
        }

        public void Save()
        {
            SaveToCurrentProfile();
            SettingsChanged?.Invoke(Settings);
        }

        private BrowserSettings LoadFromCurrentProfile()
        {
            string path = SettingsPath;

            try
            {
                BrowserSettings settings;

                if (!File.Exists(path))
                {
                    settings = CreateDefaults();
                }
                else
                {
                    settings = JsonSerializer.Deserialize<BrowserSettings>(
                        File.ReadAllText(path),
                        JsonOptions) ?? CreateDefaults();
                }

                ApplyRuntimeDefaults(settings);
                return settings;
            }
            catch
            {
                TryBackupBrokenSettings(path);

                BrowserSettings settings = CreateDefaults();
                ApplyRuntimeDefaults(settings);
                return settings;
            }
        }

        private void SaveToCurrentProfile()
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }

        private static void ApplyRuntimeDefaults(BrowserSettings settings)
        {
            MergeMissingCommands(settings);
            settings.ResolveBasiliskChoice();

            if (settings.SuspendDelayMinutes < 1)
                settings.SuspendDelayMinutes = 5;

            try
            {
                string defaultBasilisk = GetDefaultBasiliskPortablePath();

                if ((string.IsNullOrWhiteSpace(settings.BasiliskPath) ||
                     !File.Exists(settings.BasiliskPath)) &&
                    File.Exists(defaultBasilisk))
                {
                    settings.BasiliskPath = defaultBasilisk;
                }
            }
            catch
            {
            }
        }

        private BrowserSettings CreateDefaults()
        {
            return new BrowserSettings
            {
                BasiliskPath = GetDefaultBasiliskPortablePath(),
                EnableSuspension = true,
                SuspendDelayMinutes = 5,
                StartPage = "https://google.com",
                NewTabPage = "https://duckduckgo.com",
                EnableCommands = true,
                Commands = CreateDefaultCommands()
            };
        }

        private static List<CommandSetting> CreateDefaultCommands() => new()
        {
            new() { Key = "new", Description = "Nouvel onglet", Enabled = true },
            new() { Key = "close", Description = "Fermer l’onglet", Enabled = true },
            new() { Key = "close others", Description = "Fermer les autres onglets", Enabled = true },
            new() { Key = "reload", Description = "Recharger la page", Enabled = true },
            new() { Key = "suspend", Description = "Suspendre l’onglet", Enabled = true },
            new() { Key = "resume", Description = "Réactiver l’onglet", Enabled = true },
            new() { Key = "suspend inactive", Description = "Suspendre les onglets inactifs", Enabled = true },
            new() { Key = "history", Description = "Ouvrir l’historique", Enabled = true },
            new() { Key = "diagnostic", Description = "Ouvrir la page de diagnostic", Enabled = true }
        };

        /// <summary>
        /// Ajoute les commandes apparues dans une version plus récente sans toucher
        /// aux choix (activé/désactivé) déjà enregistrés par l'utilisateur.
        /// </summary>
        private static void MergeMissingCommands(BrowserSettings settings)
        {
            settings.Commands ??= new List<CommandSetting>();

            foreach (CommandSetting command in CreateDefaultCommands())
            {
                bool exists = settings.Commands.Exists(existing =>
                    string.Equals(existing.Key, command.Key, StringComparison.OrdinalIgnoreCase));

                if (!exists)
                    settings.Commands.Add(command);
            }
        }

        private static string GetDefaultBasiliskPortablePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "PommeBrowser", "Basilisk", "Basilisk-Portable.exe");
        }

        private static void TryBackupBrokenSettings(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                string backup = path + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, backup, overwrite: false);
            }
            catch
            {
            }
        }
    }
}
