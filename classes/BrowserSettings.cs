using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyHomelabBrowser.classes
{
    public class BrowserSettings
    {
        // --------------------
        // Général
        // --------------------
        public string StartPage { get; set; } = "https://google.com";
        public string NewTabPage { get; set; } = "https://duckduckgo.com";
        public SearchEngine Search { get; set; } = SearchEngine.Google;

        // DNS sécurisé WebView2. Le changement est appliqué au prochain démarrage.
        public SecureDnsMode DnsMode { get; set; } = SecureDnsMode.System;
        public SecureDnsProvider DnsProvider { get; set; } = SecureDnsProvider.Cloudflare;
        public string SecureDnsCustomTemplate { get; set; } = string.Empty;

        // --------------------
        // Sécurité de la navigation
        // --------------------
        // Les adresses http:// sont d'abord essayées en https:// (sauf réseau local).
        public bool HttpsUpgrade { get; set; } = true;
        public TrackingProtection TrackingPrevention { get; set; } = TrackingProtection.Balanced;

        // --------------------
        // Commandes
        // --------------------
        public bool EnableCommands { get; set; } = true;
        public List<CommandSetting> Commands { get; set; } = new();

        // --------------------
        // Suspension
        // --------------------
        public bool EnableSuspension { get; set; } = true;
        public int SuspendDelayMinutes { get; set; } = 5;

        // --------------------
        // Services du homelab (page d'accueil)
        // --------------------
        public bool ServiceMonitoring { get; set; } = true;
        public int ServiceCheckIntervalSeconds { get; set; } = 60;
        public bool ServiceAlerts { get; set; } = true;

        // --------------------
        // Mises à jour (éditions Avalonia et Linux : recherche au démarrage)
        // --------------------
        public bool AutoUpdate { get; set; } = true;

        // --------------------
        // Téléchargements
        // --------------------
        public string DownloadFolder { get; set; }
            = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

        // ====================
        // 📣 Rapport utilisateur
        // ====================
        public bool ReportIncludeLogs { get; set; } = true;
        public bool ReportIncludePcInfo { get; set; } = true;
        public bool ReportIncludeMode { get; set; } = true;

        // Coffre verrouillé après ce délai sans utilisation (0 : jamais).
        public int VaultAutoLockMinutes { get; set; } = 15;

        public StartupMode Startup { get; set; }
        public string? CustomStartupPage { get; set; }

        // ====================
        // 🔥 FLASH / LEGACY
        // ====================
        public bool EnableFlashSupport { get; set; } = true;

        // Ruffle intégré est le moteur principal. Basilisk reste un secours manuel.
        public bool PreferRuffle { get; set; } = true;

        // Chemin vers basilisk.exe
        public string BasiliskPath { get; set; } = "";

        // Logs / overlay / debug
        public bool FlashDebugEnabled { get; set; } = false;

        // --------------------
        // Clone (CRITIQUE)
        // --------------------
        public BrowserSettings Clone()
        {
            return new BrowserSettings
            {
                StartPage = StartPage,
                NewTabPage = NewTabPage,
                Search = Search,
                CustomStartupPage = CustomStartupPage,
                Startup = Startup,

                DnsMode = DnsMode,
                DnsProvider = DnsProvider,
                SecureDnsCustomTemplate = SecureDnsCustomTemplate,

                HttpsUpgrade = HttpsUpgrade,
                TrackingPrevention = TrackingPrevention,

                EnableSuspension = EnableSuspension,
                SuspendDelayMinutes = SuspendDelayMinutes,

                ServiceMonitoring = ServiceMonitoring,
                ServiceCheckIntervalSeconds = ServiceCheckIntervalSeconds,
                ServiceAlerts = ServiceAlerts,
                AutoUpdate = AutoUpdate,

                EnableCommands = EnableCommands,
                DownloadFolder = DownloadFolder,

                // 🔥 Flash
                EnableFlashSupport = EnableFlashSupport,
                PreferRuffle = PreferRuffle,
                BasiliskPath = BasiliskPath,
                FlashDebugEnabled = FlashDebugEnabled,

                // 📣 Rapport utilisateur
                ReportIncludeLogs = ReportIncludeLogs,
                ReportIncludePcInfo = ReportIncludePcInfo,
                ReportIncludeMode = ReportIncludeMode,
                VaultAutoLockMinutes = VaultAutoLockMinutes,

                Commands = Commands
                    .Select(c => new CommandSetting
                    {
                        Key = c.Key,
                        Description = c.Description,
                        Enabled = c.Enabled
                    })
                    .ToList()
            };

        }
        /// <summary>Niveaux de la protection contre le pistage de WebView2.</summary>
        public enum TrackingProtection
        {
            Off,
            Basic,
            Balanced,
            Strict
        }

        public enum SecureDnsMode
        {
            System,
            Automatic,
            Secure
        }

        public enum SecureDnsProvider
        {
            Cloudflare,
            Google,
            Quad9,
            AdGuard,
            Custom
        }

        public enum SearchEngine
        {
            Google,
            DuckDuckGo,
            Bing,
            Qwant,
            Startpage,
            Ecosia
        }

        public enum StartupMode
        {
            EmptyTab,
            CustomPage,
            RestoreSession
        }

        

    }
}
