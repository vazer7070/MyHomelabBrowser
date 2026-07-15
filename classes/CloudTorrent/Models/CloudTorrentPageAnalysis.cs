using System;
using System.Collections.Generic;
using System.Linq;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public sealed class CloudTorrentPageAnalysis
    {
        public static CloudTorrentPageAnalysis Empty(string message = "Aucune page analysée.") => new()
        {
            Message = message
        };

        public string Title { get; init; } = string.Empty;
        public string PageUrl { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public int MediaElementCount { get; init; }
        public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.Now;
        public IReadOnlyList<CloudTorrentDetectedItem> Items { get; init; } = Array.Empty<CloudTorrentDetectedItem>();
        public bool IsCompatible => Uri.TryCreate(PageUrl, UriKind.Absolute, out Uri? uri) &&
                                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        public int DetectedCount => Items.Count(item => item.Type != CloudTorrentDetectedItemType.Page);
        public int SelectedCount => Items.Count(item => item.IsSelected);

        public string Summary
        {
            get
            {
                if (!IsCompatible)
                    return string.IsNullOrWhiteSpace(Message) ? "Page non compatible." : Message;

                int torrents = Items.Count(i => i.Type == CloudTorrentDetectedItemType.Torrent);
                int hosters = Items.Count(i => i.Type == CloudTorrentDetectedItemType.Hoster);
                int media = Items.Count(i => i.Type == CloudTorrentDetectedItemType.Media);
                var parts = new List<string>();

                if (torrents > 0) parts.Add($"{torrents} torrent{(torrents > 1 ? "s" : string.Empty)}");
                if (hosters > 0) parts.Add($"{hosters} lien{(hosters > 1 ? "s" : string.Empty)} d’hébergeur");
                if (media > 0 || MediaElementCount > 0)
                {
                    int count = Math.Max(media, MediaElementCount);
                    parts.Add($"{count} média{(count > 1 ? "s" : string.Empty)}");
                }

                return parts.Count > 0
                    ? string.Join(" · ", parts)
                    : "Aucun lien direct détecté. La page complète reste analysable.";
            }
        }
    }
}
