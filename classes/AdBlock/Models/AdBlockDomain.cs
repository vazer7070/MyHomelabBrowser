using System;
using System.Collections.Generic;
using System.Net;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    public static class AdBlockDomain
    {
        private static readonly HashSet<string> CommonTwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "co.uk", "org.uk", "me.uk", "ac.uk", "gov.uk",
            "com.au", "net.au", "org.au", "edu.au",
            "co.nz", "net.nz", "org.nz",
            "co.jp", "ne.jp", "or.jp",
            "com.br", "net.br", "org.br",
            "com.cn", "net.cn", "org.cn",
            "com.sg", "net.sg", "org.sg",
            "com.tr", "com.mx", "com.ar", "com.tw", "com.hk",
            "co.in", "firm.in", "net.in", "org.in", "gen.in",
            "co.za", "org.za", "net.za"
        };

        public static string NormalizeHost(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            // Chemin rapide : un nom d'hôte déjà propre (cas de chaque requête réseau)
            // n'a pas besoin de passer par deux analyses d'URI.
            if (TryNormalizeSimpleHost(value, out string simple))
                return simple;

            string input = value.Trim().Trim('.').ToLowerInvariant();
            if (input.Length == 0)
                return string.Empty;

            if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
                input = uri.Host;
            else if (Uri.TryCreate("https://" + input, UriKind.Absolute, out uri))
                input = uri.Host;

            if (input.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                input = input[4..];

            return input.Trim().Trim('.').ToLowerInvariant();
        }

        private static bool TryNormalizeSimpleHost(string value, out string result)
        {
            bool hasUpper = false;
            foreach (char c in value)
            {
                if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_')
                    continue;

                if (c is >= 'A' and <= 'Z')
                {
                    hasUpper = true;
                    continue;
                }

                result = string.Empty;
                return false;
            }

            string host = (hasUpper ? value.ToLowerInvariant() : value).Trim('.');
            if (host.StartsWith("www.", StringComparison.Ordinal))
                host = host[4..].Trim('.');

            result = host;
            return true;
        }

        public static bool HostMatches(string? host, string? domain)
        {
            string normalizedHost = NormalizeHost(host);
            string normalizedDomain = NormalizeHost(domain);
            if (normalizedHost.Length == 0 || normalizedDomain.Length == 0)
                return false;

            return IsSameOrSubdomain(normalizedHost, normalizedDomain);
        }

        /// <summary>
        /// Variante de <see cref="HostMatches"/> pour deux hôtes déjà normalisés.
        /// </summary>
        internal static bool IsSameOrSubdomain(string host, string domain)
        {
            if (host.Length == domain.Length)
                return host.Equals(domain, StringComparison.OrdinalIgnoreCase);

            return host.Length > domain.Length
                && host[host.Length - domain.Length - 1] == '.'
                && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Indique si l'hôte normalisé ou l'un de ses domaines parents figure dans l'ensemble.
        /// Recherche par suffixe : coût proportionnel au nombre de labels, pas à la taille de la liste.
        /// L'ensemble doit utiliser un comparateur ordinal (insensible à la casse).
        /// </summary>
        internal static bool ContainsHostOrParent(HashSet<string> domains, string normalizedHost)
        {
            if (domains.Count == 0 || normalizedHost.Length == 0)
                return false;

            HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup = domains.GetAlternateLookup<ReadOnlySpan<char>>();
            ReadOnlySpan<char> current = normalizedHost;
            while (true)
            {
                if (lookup.Contains(current))
                    return true;

                int dot = current.IndexOf('.');
                if (dot < 0 || dot == current.Length - 1)
                    return false;

                current = current[(dot + 1)..];
            }
        }

        public static IEnumerable<string> EnumerateHostSuffixes(string? host)
        {
            string normalized = NormalizeHost(host);
            if (normalized.Length == 0)
                yield break;

            string current = normalized;
            while (current.Length > 0)
            {
                yield return current;
                int dot = current.IndexOf('.');
                if (dot < 0 || dot == current.Length - 1)
                    break;
                current = current[(dot + 1)..];
            }
        }

        public static bool IsSameSite(string? leftHost, string? rightHost)
        {
            string left = GetRegistrableDomain(leftHost);
            string right = GetRegistrableDomain(rightHost);
            return left.Length > 0 && left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetRegistrableDomain(string? host)
        {
            string normalized = NormalizeHost(host);
            if (normalized.Length == 0 || IPAddress.TryParse(normalized, out _))
                return normalized;

            // Appelé pour chaque requête : pas de Split, seulement des recherches de points.
            int last = normalized.LastIndexOf('.');
            if (last <= 0)
                return normalized;

            int second = normalized.LastIndexOf('.', last - 1);
            if (second < 0)
                return normalized;

            string lastTwo = normalized[(second + 1)..];
            if (!CommonTwoLevelSuffixes.Contains(lastTwo))
                return lastTwo;

            int third = second > 0 ? normalized.LastIndexOf('.', second - 1) : -1;
            return third < 0 ? normalized : normalized[(third + 1)..];
        }

        public static bool IsPrivateOrLocalHost(string? host)
        {
            string normalized = NormalizeHost(host);
            if (normalized.Length == 0)
                return true;

            if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                || !normalized.Contains('.'))
            {
                return true;
            }

            if (!IPAddress.TryParse(normalized, out IPAddress? address))
                return false;

            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10
                    || bytes[0] == 127
                    || (bytes[0] == 169 && bytes[1] == 254)
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168);
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IPAddress.IPv6Loopback.Equals(address);
        }
    }
}
