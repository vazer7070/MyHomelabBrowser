using System;
using System.Text.RegularExpressions;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    public static partial class CloudTorrentUrl
    {
        [GeneratedRegex("^ctk_[A-Za-z0-9_-]+_[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
        private static partial Regex ApiKeyPattern();

        public static string NormalizeServerUrl(string? value)
        {
            string input = (value ?? string.Empty).Trim();
            if (input.Length == 0)
                throw new ArgumentException("Saisissez l’adresse du site CloudTorrent.", nameof(value));

            if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("L’adresse doit commencer par http:// ou https://.", nameof(value));
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Ne placez pas d’identifiant dans l’adresse du site.", nameof(value));

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.AbsoluteUri.TrimEnd('/');
        }

        public static bool LooksLikeApiKey(string? value)
        {
            return ApiKeyPattern().IsMatch((value ?? string.Empty).Trim());
        }

        public static Uri BuildApiUri(string serverUrl, string relativePath)
        {
            string normalized = NormalizeServerUrl(serverUrl);
            string path = (relativePath ?? string.Empty).Trim();
            if (!path.StartsWith('/'))
                path = "/" + path;

            return new Uri(normalized + "/api/v1" + path, UriKind.Absolute);
        }
    }
}
