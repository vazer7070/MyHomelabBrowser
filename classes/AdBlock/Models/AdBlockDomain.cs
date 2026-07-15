using System;
using System.Collections.Generic;
using System.Linq;
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
            string input = (value ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
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

        public static bool HostMatches(string? host, string? domain)
        {
            string normalizedHost = NormalizeHost(host);
            string normalizedDomain = NormalizeHost(domain);
            if (normalizedHost.Length == 0 || normalizedDomain.Length == 0)
                return false;

            return normalizedHost.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)
                || normalizedHost.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
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

            string[] labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length <= 2)
                return normalized;

            string lastTwo = labels[^2] + "." + labels[^1];
            if (CommonTwoLevelSuffixes.Contains(lastTwo) && labels.Length >= 3)
                return labels[^3] + "." + labels[^2] + "." + labels[^1];

            return lastTwo;
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
