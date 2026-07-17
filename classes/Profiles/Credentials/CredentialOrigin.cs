using System.Net;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public static class CredentialOrigin
    {
        public static bool TryCreateTrusted(Uri? uri, out string origin)
        {
            origin = string.Empty;
            if (uri == null || !uri.IsAbsoluteUri || string.IsNullOrWhiteSpace(uri.Host))
                return false;

            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme == Uri.UriSchemeHttps)
            {
                origin = BuildCanonical(uri);
                return true;
            }

            if (scheme != Uri.UriSchemeHttp || !IsAllowedInsecureHost(uri.Host))
                return false;

            origin = BuildCanonical(uri);
            return true;
        }

        public static bool TryCreateTrusted(string? value, out string origin)
        {
            origin = string.Empty;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && TryCreateTrusted(uri, out origin);
        }

        public static bool SameOrigin(Uri? left, Uri? right)
        {
            return TryCreateTrusted(left, out var leftOrigin)
                   && TryCreateTrusted(right, out var rightOrigin)
                   && string.Equals(leftOrigin, rightOrigin, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeStoredValue(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (TryCreateTrusted(trimmed, out var origin))
                return origin;

            // Migration des anciens coffres qui stockaient uniquement un hôte.
            if (trimmed.Length > 0
                && !trimmed.Contains('/')
                && Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var legacyUri)
                && TryCreateTrusted(legacyUri, out origin))
            {
                return origin;
            }

            return string.Empty;
        }

        public static string DisplayName(string? origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return origin ?? string.Empty;

            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }

        private static string BuildCanonical(Uri uri)
        {
            var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.IdnHost.ToLowerInvariant())
            {
                Port = uri.IsDefaultPort ? -1 : uri.Port,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static bool IsAllowedInsecureHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IPAddress.TryParse(host, out var address))
                return false;

            if (IPAddress.IsLoopback(address))
                return true;

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }
    }
}
