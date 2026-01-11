using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        public string DownloadFolder { get; set; }
    = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Downloads");


        // --------------------
        // FUTUR
        // --------------------
        // public bool EnableProfiles { get; set; }
        // public bool EnableWorkspaces { get; set; }

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
