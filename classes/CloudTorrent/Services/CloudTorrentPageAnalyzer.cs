using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.CloudTorrent.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    public sealed class CloudTorrentPageAnalyzer
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private const string ScanScript = """
            (() => {
                const unique = values => [...new Set(values.filter(Boolean))];
                const isHttp = value => /^https?:\/\//i.test(String(value || ''));
                const absolute = value => {
                    const text = String(value ?? '').trim();
                    if (!text || text === 'null' || text === 'undefined' || text === 'about:blank' || text.startsWith('blob:')) {
                        return '';
                    }

                    try {
                        const resolved = new URL(text, document.baseURI).href;
                        const path = new URL(resolved).pathname.toLowerCase();
                        if (path === '/null' || path === '/undefined') return '';
                        return resolved;
                    } catch (_) {
                        return '';
                    }
                };

                const isKnownUiSound = value => {
                    try {
                        const url = new URL(value);
                        const host = url.hostname.toLowerCase().replace(/^www\./, '');
                        const path = url.pathname.toLowerCase();
                        return (host === 'youtube.com' || host.endsWith('.youtube.com')) &&
                               (path.startsWith('/s/search/audio/') || path.startsWith('/s/player/') && path.endsWith('.mp3'));
                    } catch (_) {
                        return false;
                    }
                };

                const hrefs = [...document.querySelectorAll('a[href]')]
                    .map(node => absolute(node.getAttribute('href')))
                    .filter(Boolean);

                const magnets = unique(hrefs.filter(href => href.toLowerCase().startsWith('magnet:?'))).slice(0, 50);
                const torrentLinks = unique(hrefs.filter(href => isHttp(href) && /\.torrent(?:$|[?#])/i.test(href))).slice(0, 50);
                const hosterLinks = unique(hrefs.filter(href => {
                    try {
                        const host = new URL(href).hostname.toLowerCase().replace(/^www\./, '');
                        return host === '1fichier.com' || host.endsWith('.1fichier.com');
                    } catch (_) { return false; }
                })).slice(0, 100);

                const mediaNodes = [...document.querySelectorAll('video, audio')];
                const nodeMedia = mediaNodes.flatMap(node => [
                    absolute(node.currentSrc),
                    absolute(node.getAttribute('src')),
                    absolute(node.getAttribute('data-src'))
                ]);
                const sourceMedia = [...document.querySelectorAll('source[src]')]
                    .map(node => absolute(node.getAttribute('src')));
                const linkedMedia = hrefs.filter(href =>
                    /\.(?:mp4|mkv|webm|m3u8|mpd|mov|m4v|mp3|m4a|flac|aac|ts)(?:$|[?#])/i.test(href));

                const directMedia = unique([
                    ...nodeMedia,
                    ...sourceMedia,
                    ...linkedMedia
                ].filter(value => isHttp(value) && !isKnownUiSound(value))).slice(0, 100);

                return {
                    title: document.title || location.hostname || 'Page',
                    url: location.href,
                    magnets,
                    torrentLinks,
                    hosterLinks,
                    directMedia,
                    videoElements: mediaNodes.length
                };
            })();
            """;

        public async Task<CloudTorrentPageAnalysis> AnalyzeAsync(
            WebView2 webView,
            IEnumerable<string>? observedNetworkUrls = null,
            CancellationToken cancellationToken = default)
        {
            if (webView == null)
                throw new ArgumentNullException(nameof(webView));

            cancellationToken.ThrowIfCancellationRequested();

            string currentUrl = webView.Source?.AbsoluteUri ?? string.Empty;
            if (!IsHttpUrl(currentUrl) || webView.CoreWebView2 == null)
                return CloudTorrentPageAnalysis.Empty("Les pages internes et privées du navigateur ne sont pas analysées.");

            string rawResult = await webView.ExecuteScriptAsync(ScanScript);
            cancellationToken.ThrowIfCancellationRequested();

            DomScanResult? result;
            try
            {
                result = JsonSerializer.Deserialize<DomScanResult>(rawResult, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("La page n’a pas pu être interprétée par le module CloudTorrent.", ex);
            }

            if (result == null)
                return CloudTorrentPageAnalysis.Empty("La page n’a renvoyé aucun contenu analysable.");

            var items = new List<CloudTorrentDetectedItem>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddMany(items, known, result.Magnets, CloudTorrentDetectedItemType.Torrent, "Magnet", "Page", selected: true);
            AddMany(items, known, result.TorrentLinks, CloudTorrentDetectedItemType.Torrent, "Fichier .torrent", "Page", selected: true);
            AddMany(items, known, result.HosterLinks, CloudTorrentDetectedItemType.Hoster, "Lien 1fichier", "Page", selected: true);
            AddMany(items, known, result.DirectMedia, CloudTorrentDetectedItemType.Media, "Média direct", "Page", selected: true);

            if (observedNetworkUrls != null)
            {
                foreach (string candidate in observedNetworkUrls)
                {
                    CloudTorrentDetectedItemType? type = ClassifyNetworkUrl(candidate);
                    if (type == null || !known.Add(candidate))
                        continue;

                    items.Add(new CloudTorrentDetectedItem
                    {
                        Type = type.Value,
                        Label = type == CloudTorrentDetectedItemType.Torrent
                            ? "Torrent détecté sur le réseau"
                            : "Flux vidéo détecté sur le réseau",
                        Url = candidate,
                        Source = "Réseau",
                        IsSelected = true
                    });
                }
            }

            string pageUrl = IsHttpUrl(result.Url) ? result.Url : currentUrl;
            if (known.Add(pageUrl))
            {
                bool hasVideoPlayer = result.VideoElements > 0;
                bool hasDirectMedia = items.Any(item => item.Type == CloudTorrentDetectedItemType.Media);

                items.Add(new CloudTorrentDetectedItem
                {
                    Type = CloudTorrentDetectedItemType.Page,
                    Label = hasVideoPlayer ? "Vidéo de la page" : "Analyser la page complète",
                    Url = pageUrl,
                    Source = hasVideoPlayer ? "Lecteur de la page" : "Page",
                    IsSelected = !hasDirectMedia
                });
            }

            return new CloudTorrentPageAnalysis
            {
                Title = string.IsNullOrWhiteSpace(result.Title) ? new Uri(pageUrl).Host : result.Title,
                PageUrl = pageUrl,
                MediaElementCount = result.VideoElements,
                Items = items,
                AnalyzedAt = DateTimeOffset.Now
            };
        }

        public static CloudTorrentDetectedItem CreateContextItem(string url, string label)
        {
            CloudTorrentDetectedItemType type = ClassifyUserSource(url);
            return new CloudTorrentDetectedItem
            {
                Type = type,
                Label = label,
                Url = url.Trim(),
                Source = "Clic droit",
                IsSelected = true
            };
        }

        public static CloudTorrentDetectedItemType ClassifyUserSource(string value)
        {
            string url = (value ?? string.Empty).Trim();
            if (url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
                UriPathEndsWith(url, ".torrent"))
                return CloudTorrentDetectedItemType.Torrent;

            if (IsOneFichier(url))
                return CloudTorrentDetectedItemType.Hoster;

            return CloudTorrentDetectedItemType.Media;
        }

        public static bool IsNetworkCandidate(string value) => ClassifyNetworkUrl(value) != null;

        private static CloudTorrentDetectedItemType? ClassifyNetworkUrl(string value)
        {
            string url = (value ?? string.Empty).Trim();
            if (!IsHttpUrl(url) || IsKnownNoiseUrl(url))
                return null;

            if (UriPathEndsWith(url, ".torrent"))
                return CloudTorrentDetectedItemType.Torrent;

            if (IsOneFichier(url))
                return CloudTorrentDetectedItemType.Hoster;

            // Les sons d’interface (.mp3, .m4a, etc.) ne sont volontairement pas
            // collectés depuis le trafic réseau. Les véritables sources audio restent
            // détectées lorsqu’elles sont présentes dans <audio> ou <source>.
            string[] videoExtensions =
            {
                ".m3u8", ".mpd", ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".ts"
            };

            return videoExtensions.Any(extension => UriPathEndsWith(url, extension))
                ? CloudTorrentDetectedItemType.Media
                : null;
        }

        private static bool IsKnownNoiseUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return true;

            string host = uri.Host.TrimStart('.').ToLowerInvariant();
            string path = uri.AbsolutePath.ToLowerInvariant();

            if (path is "/null" or "/undefined")
                return true;

            if ((host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)) &&
                path.StartsWith("/s/search/audio/", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static void AddMany(
            ICollection<CloudTorrentDetectedItem> destination,
            ISet<string> known,
            IEnumerable<string>? values,
            CloudTorrentDetectedItemType type,
            string label,
            string source,
            bool selected)
        {
            int index = 0;
            foreach (string value in values ?? Array.Empty<string>())
            {
                string url = (value ?? string.Empty).Trim();
                if (url.Length == 0 ||
                    (IsHttpUrl(url) && IsKnownNoiseUrl(url)) ||
                    !known.Add(url))
                    continue;

                index++;
                destination.Add(new CloudTorrentDetectedItem
                {
                    Type = type,
                    Label = $"{label} {index}",
                    Url = url,
                    Source = source,
                    IsSelected = selected
                });
            }
        }

        private static bool IsHttpUrl(string value)
            => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static bool IsOneFichier(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return false;

            string host = uri.Host.TrimStart('.').ToLowerInvariant();
            return host == "1fichier.com" || host.EndsWith(".1fichier.com", StringComparison.Ordinal);
        }

        private static bool UriPathEndsWith(string value, string extension)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return false;

            return uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class DomScanResult
        {
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public List<string> Magnets { get; set; } = new();
            public List<string> TorrentLinks { get; set; } = new();
            public List<string> HosterLinks { get; set; } = new();
            public List<string> DirectMedia { get; set; } = new();
            public int VideoElements { get; set; }
        }
    }
}
