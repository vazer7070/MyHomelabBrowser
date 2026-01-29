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

        // ====================
        // 🔥 FLASH / LEGACY
        // ====================
        public bool EnableFlashSupport { get; set; } = false;

        // Si true → tenter Ruffle avant Basilisk
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

                EnableSuspension = EnableSuspension,
                SuspendDelayMinutes = SuspendDelayMinutes,

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
    }
}
