using System;

namespace MyHomelabBrowser.classes.Security
{
    /// <summary>
    /// Passage automatique en HTTPS : une adresse http:// est d'abord essayée en
    /// https://. Ne sont jamais concernés : le réseau local (homelab, adresses IP,
    /// noms sans domaine) et les ports explicites (un service qui écoute en HTTP sur
    /// :8080 ne répondrait pas en HTTPS sur ce même port).
    /// </summary>
    public static class HttpsUpgradePolicy
    {
        public static bool ShouldUpgrade(Uri uri, Func<string, bool> isAllowedOverHttp)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!uri.IsDefaultPort)
                return false;

            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                return false;

            string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (host.Length == 0 || !host.Contains('.') || UrlResolver.IsLocalHost(host))
                return false;

            return !isAllowedOverHttp(host);
        }

        public static Uri Upgrade(Uri uri)
            => new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }
}
