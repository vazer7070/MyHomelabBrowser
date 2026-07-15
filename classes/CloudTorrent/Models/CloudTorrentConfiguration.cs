using System;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public sealed class CloudTorrentConfiguration
    {
        public string ServerUrl { get; set; } = string.Empty;

        public bool AutoAnalyzePages { get; set; } = true;

        public CloudTorrentConfiguration Clone()
        {
            return new CloudTorrentConfiguration
            {
                ServerUrl = ServerUrl,
                AutoAnalyzePages = AutoAnalyzePages
            };
        }

        public bool HasServerUrl => !string.IsNullOrWhiteSpace(ServerUrl);
    }
}
