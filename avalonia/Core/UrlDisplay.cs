using System;
using System.Globalization;
using System.Linq;

namespace PommeBrowser.Core
{
    /// <summary>Adresses telles qu'affichées (barre d'adresse, titres provisoires, historique).</summary>
    public static class UrlDisplay
    {
        static readonly IdnMapping Idn = new();

        /// <summary>
        /// Adresse lisible : domaine internationalisé décodé, chemin sans %XX quand le résultat
        /// reste sans ambiguïté (pas d'espace ni de caractère invisible).
        /// </summary>
        public static string ForDisplay(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.IsFile)
                return url;

            try
            {
                string host = uri.HostNameType == UriHostNameType.Dns ? Idn.GetUnicode(uri.IdnHost) : uri.Host;
                string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
                string rest = uri.PathAndQuery + uri.Fragment;
                string decoded = Uri.UnescapeDataString(rest);
                if (decoded.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                    decoded = rest;
                return uri.Scheme + "://" + host + port + decoded;
            }
            catch (ArgumentException)
            {
                return url;
            }
        }

        /// <summary>Adresse sans « https:// » ni barre finale, pour la barre d'adresse au repos.</summary>
        public static string Short(string url)
        {
            string display = ForDisplay(url);
            if (display.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                display = display[8..];
            return display.EndsWith('/') && display.IndexOf('/') == display.Length - 1 ? display[..^1] : display;
        }
    }
}
