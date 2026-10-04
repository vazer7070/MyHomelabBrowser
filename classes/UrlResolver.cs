using System;
using System.Net;
using System.Net.Sockets;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Transforme une saisie de l'omnibox en URL navigable ou en recherche.
    /// Les adresses de réseau local (localhost, IP privées, nom court avec port,
    /// .lan, .local, .home.arpa, .internal) sont ouvertes en HTTP, car les services
    /// d'un homelab n'exposent souvent pas de HTTPS. Le reste passe en HTTPS.
    /// </summary>
    public static class UrlResolver
    {
        private static readonly string[] NavigableSchemes =
        {
            "http", "https", "file", "about", "ftp", "view-source", "data", "edge"
        };

        public static string BuildSearchUrl(string query, BrowserSettings.SearchEngine engine)
        {
            string escaped = Uri.EscapeDataString(query.Trim());
            return engine switch
            {
                BrowserSettings.SearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={escaped}",
                BrowserSettings.SearchEngine.Bing => $"https://www.bing.com/search?q={escaped}",
                BrowserSettings.SearchEngine.Qwant => $"https://www.qwant.com/?q={escaped}",
                BrowserSettings.SearchEngine.Startpage => $"https://www.startpage.com/do/search?q={escaped}",
                BrowserSettings.SearchEngine.Ecosia => $"https://www.ecosia.org/search?q={escaped}",
                _ => $"https://www.google.com/search?q={escaped}"
            };
        }

        public static string GetSearchEngineName(BrowserSettings.SearchEngine engine) => engine switch
        {
            BrowserSettings.SearchEngine.DuckDuckGo => "DuckDuckGo",
            BrowserSettings.SearchEngine.Bing => "Bing",
            BrowserSettings.SearchEngine.Qwant => "Qwant",
            BrowserSettings.SearchEngine.Startpage => "Startpage",
            BrowserSettings.SearchEngine.Ecosia => "Ecosia",
            _ => "Google"
        };

        /// <summary>
        /// Retourne l'URL à ouvrir, ou null si la saisie doit être traitée comme une recherche.
        /// </summary>
        public static string? TryResolveUrl(string? input)
        {
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0 || ContainsWhitespace(text))
                return null;

            // Schéma explicite (https://…, file:///…, about:blank).
            int schemeEnd = text.IndexOf(':');
            if (schemeEnd > 0 &&
                Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) &&
                Array.IndexOf(NavigableSchemes, absolute.Scheme.ToLowerInvariant()) >= 0 &&
                (!absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) || absolute.Host.Length > 0))
            {
                return absolute.AbsoluteUri;
            }

            // Sans schéma : on analyse l'hôte comme le ferait un navigateur.
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out Uri? candidate) ||
                candidate.Host.Length == 0)
            {
                return null;
            }

            string host = candidate.Host.Trim('[', ']');
            bool hasExplicitPort = text.Contains(':') && !candidate.IsDefaultPort;

            if (IsLocalHost(host))
                return IsUsualHttpsPort(candidate.Port) && hasExplicitPort
                    ? "https://" + text
                    : candidate.AbsoluteUri;

            if (IPAddress.TryParse(host, out _))
                return "https://" + text;

            bool singleLabel = !host.Contains('.');
            if (singleLabel)
            {
                // "nas:5000" ou "routeur/admin" : un nom court n'est une adresse que
                // si l'utilisateur a précisé un port ou un chemin.
                return hasExplicitPort || text.Contains('/') ? candidate.AbsoluteUri : null;
            }

            string tld = host[(host.LastIndexOf('.') + 1)..];
            if (tld.Length < 2 || !IsAlphaOrPunycode(tld))
                return null;

            return "https://" + text;
        }

        /// <summary>
        /// Adresse saisie sans schéma (« exemple.com/page ») et ouverte en https:// par
        /// <see cref="TryResolveUrl"/>, sur le port par défaut : comme Chrome et Firefox, elle peut
        /// revenir en http:// si le site ne propose pas HTTPS. Faux pour une adresse avec son
        /// schéma (https:// tapé exprès), un port explicite ou une recherche.
        /// </summary>
        public static bool IsImplicitHttps(string? input)
        {
            string text = (input ?? string.Empty).Trim();
            if (text.Contains("://", StringComparison.Ordinal) || TryResolveUrl(text) is not { } url ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort;
        }

        public static string ResolveOrSearch(string input, BrowserSettings.SearchEngine engine)
            => TryResolveUrl(input) ?? BuildSearchUrl(input, engine);

        /// <summary>
        /// Hôtes considérés comme locaux : loopback, plages privées IPv4/IPv6,
        /// plage CGNAT utilisée par Tailscale et suffixes réservés aux réseaux domestiques.
        /// </summary>
        public static bool IsLocalHost(string? host)
        {
            string value = (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (value.Length == 0)
                return false;

            if (value == "localhost" ||
                value.EndsWith(".localhost", StringComparison.Ordinal) ||
                value.EndsWith(".lan", StringComparison.Ordinal) ||
                value.EndsWith(".local", StringComparison.Ordinal) ||
                value.EndsWith(".home.arpa", StringComparison.Ordinal) ||
                value.EndsWith(".internal", StringComparison.Ordinal))
            {
                return true;
            }

            if (!IPAddress.TryParse(value, out IPAddress? address))
                return false;

            if (IPAddress.IsLoopback(address))
                return true;

            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        // Ports d'administration qui n'écoutent qu'en HTTPS (Proxmox, Synology, Portainer…).
        private static bool IsUsualHttpsPort(int port)
            => port is 443 or 5001 or 8006 or 8443 or 9443;

        private static bool ContainsWhitespace(string value)
        {
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c))
                    return true;
            }
            return false;
        }

        private static bool IsAlphaOrPunycode(string tld)
        {
            if (tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (char c in tld)
            {
                if (!char.IsLetter(c))
                    return false;
            }
            return true;
        }
    }
}
