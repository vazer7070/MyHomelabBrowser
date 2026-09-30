using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes
{
    public enum AppTheme
    {
        System,
        Dark,
        Light
    }

    /// <summary>
    /// Réglages d'apparence communs à tous les profils (lus avant l'ouverture de la
    /// fenêtre, donc avant le choix du profil). Appliqués au démarrage.
    /// </summary>
    public sealed class AppearanceSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Sombre par défaut : c'est l'apparence historique du navigateur.
        /// </summary>
        public AppTheme Theme { get; set; } = AppTheme.Dark;

        /// <summary>
        /// Code de langue de l'interface (« fr » ou « en »).
        /// </summary>
        public string Language { get; set; } = "fr";

        public static string DefaultPath => Path.Combine(Profiles.AppDataContext.GlobalRoot, "appearance.json");

        public static AppearanceSettings Load(string? path = null)
        {
            try
            {
                string file = path ?? DefaultPath;
                if (File.Exists(file))
                {
                    var settings = JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(file), JsonOptions);
                    if (settings != null)
                    {
                        settings.Language = NormalizeLanguage(settings.Language);
                        return settings;
                    }
                }
            }
            catch
            {
                // Fichier illisible : apparence par défaut.
            }

            return new AppearanceSettings();
        }

        public void Save(string? path = null)
        {
            Language = NormalizeLanguage(Language);
            AtomicFile.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(this, JsonOptions));
        }

        public static string NormalizeLanguage(string? language)
            => string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "fr";

        /// <summary>
        /// Thème effectif : « Système » suit le mode clair ou sombre des applications Windows.
        /// </summary>
        public bool ResolveIsDark(Func<bool?>? systemPrefersLight = null)
            => Theme switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                _ => (systemPrefersLight ?? ReadSystemPrefersLight)() != true
            };

        private static bool? ReadSystemPrefersLight()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
