using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Réglages de l'édition Linux (settings-linux.json dans le profil). Fichier distinct
    /// de celui de Windows : un profil copié d'un système à l'autre reste lisible des deux côtés.
    /// </summary>
    public sealed class LinuxSettings
    {
        static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public BrowserSettings.SearchEngine Search { get; set; } = BrowserSettings.SearchEngine.DuckDuckGo;

        /// <summary>Rouvrir les onglets de la dernière session au démarrage.</summary>
        public bool RestoreSession { get; set; } = true;

        /// <summary>Essayer https:// avant http:// (hors réseau local).</summary>
        public bool HttpsUpgrade { get; set; } = true;

        /// <summary>Protection intelligente contre le pistage de WebKit (ITP).</summary>
        public bool TrackingPrevention { get; set; } = true;

        public bool BlockThirdPartyCookies { get; set; } = true;

        public bool EnableRuffle { get; set; } = true;

        /// <summary>Exécutable de Basilisk ; vide : recherché aux emplacements habituels.</summary>
        public string? BasiliskPath { get; set; }

        public bool ServiceMonitoring { get; set; } = true;
        public int ServiceCheckIntervalSeconds { get; set; } = 60;
        public bool ServiceAlerts { get; set; } = true;

        /// <summary>Rechercher une nouvelle version au démarrage (AppImage seulement).</summary>
        public bool AutoUpdate { get; set; } = true;

        /// <summary>Vide : dossier Téléchargements de l'utilisateur.</summary>
        public string? DownloadDirectory { get; set; }

        public void Normalize()
        {
            ServiceCheckIntervalSeconds = Math.Clamp(ServiceCheckIntervalSeconds, 15, 3600);
            if (string.IsNullOrWhiteSpace(DownloadDirectory) || !Path.IsPathRooted(DownloadDirectory))
                DownloadDirectory = null;
            if (string.IsNullOrWhiteSpace(BasiliskPath) || !Path.IsPathRooted(BasiliskPath))
                BasiliskPath = null;
        }

        public static LinuxSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    LinuxSettings? settings = JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(path), JsonOptions);
                    if (settings != null)
                    {
                        settings.Normalize();
                        return settings;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fichier illisible : réglages par défaut, l'ancien est gardé de côté.
                try
                {
                    File.Move(path, path + ".invalide", overwrite: true);
                }
                catch (IOException)
                {
                }
            }

            return new LinuxSettings();
        }

        public void Save(string path)
        {
            Normalize();
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
