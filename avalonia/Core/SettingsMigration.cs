using System;
using System.IO;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Premier lancement sous Linux avec un profil de l'édition GTK : ses réglages
    /// (settings-linux.json) sont repris dans le format commun (settings.json).
    /// </summary>
    public static class SettingsMigration
    {
        /// <summary>
        /// Réglages communs avec l'édition Windows : hors Windows, le chemin par défaut de
        /// Basilisk-Portable.exe n'a pas de sens (recherche automatique à la place).
        /// </summary>
        public static void AdaptToPlatform(SettingsService service)
        {
            BrowserSettings settings = service.Settings;
            if (!OperatingSystem.IsWindows() && settings.BasiliskPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                settings.BasiliskPath = string.Empty;
                service.Save();
            }
        }

        public static void ImportLinuxSettings(SettingsService service)
        {
            string target = AppPaths.Profile("settings.json");
            string source = AppPaths.Profile("settings-linux.json");
            if (!OperatingSystem.IsLinux() || File.Exists(target) || !File.Exists(source))
                return;

            LinuxSettings old = LinuxSettings.Load(source);
            BrowserSettings settings = service.Settings;
            settings.Search = old.Search;
            settings.Startup = old.RestoreSession ? BrowserSettings.StartupMode.RestoreSession : BrowserSettings.StartupMode.EmptyTab;
            settings.HttpsUpgrade = old.HttpsUpgrade;
            settings.TrackingPrevention = !old.TrackingPrevention
                ? BrowserSettings.TrackingProtection.Off
                : old.BlockThirdPartyCookies ? BrowserSettings.TrackingProtection.Balanced : BrowserSettings.TrackingProtection.Basic;
            settings.EnableFlashSupport = old.EnableRuffle;
            settings.BasiliskPath = old.BasiliskPath ?? string.Empty;
            settings.ServiceMonitoring = old.ServiceMonitoring;
            settings.ServiceCheckIntervalSeconds = old.ServiceCheckIntervalSeconds;
            settings.ServiceAlerts = old.ServiceAlerts;
            settings.DownloadFolder = old.DownloadDirectory ?? string.Empty;
            settings.AutoUpdate = old.AutoUpdate;
            // L'édition GTK ouvrait la page d'accueil de PommeBrowser dans les nouveaux onglets.
            settings.NewTabPage = string.Empty;
            service.Save();
        }
    }
}
