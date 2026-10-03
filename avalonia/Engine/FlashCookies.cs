using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using MyHomelabBrowser.classes.AdBlock.Models;

namespace PommeBrowser.Engine
{
    /// <summary>Cookie à enregistrer dans le profil de la page (lu dans un en-tête Set-Cookie).</summary>
    /// <param name="Domain">Domaine du cookie ; avec <paramref name="HostOnly"/>, l'hôte exact qui l'a déposé.</param>
    /// <param name="Expires">Fin de validité ; null : cookie de session.</param>
    /// <param name="SameSite">« Strict », « Lax » ou « None » ; null : non précisé.</param>
    public sealed record PageCookie(string Name, string Value, string Domain, bool HostOnly, string Path,
        DateTimeOffset? Expires, bool Secure, bool HttpOnly, string? SameSite)
    {
        /// <summary>Déjà expiré : le cookie de même nom est retiré.</summary>
        public bool IsExpired(DateTimeOffset now) => Expires is { } expires && expires <= now;
    }

    /// <summary>
    /// Cookies partagés entre la page et le moteur Flash intégré, comme dans un navigateur : les
    /// chargements du lecteur portent les cookies de la page, et ceux que les réponses déposent
    /// vont dans la page. Le lecteur exécute un module tiers (Flash) : il n'a accès qu'aux cookies
    /// du site de la page, et chaque cookie reçu est vérifié (règles du RFC 6265).
    /// </summary>
    public static class FlashCookies
    {
        const int MaxLength = 4096;

        /// <summary>Adresse du même site que la page (même domaine enregistrable), en http ou https.</summary>
        public static bool IsShared(Uri page, Uri url)
            => page.Scheme is "http" or "https" && url.Scheme is "http" or "https" &&
               AdBlockDomain.IsSameSite(page.Host, url.Host);

        /// <summary>En-tête Cookie : « nom=valeur; nom=valeur ».</summary>
        public static string Header(IEnumerable<(string Name, string Value)> cookies)
            => string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));

        /// <summary>
        /// Cookie d'un en-tête Set-Cookie reçu pour <paramref name="url"/>
        /// (<paramref name="fromHttp"/>), ou posé comme par un script (sans HttpOnly). Null s'il est
        /// refusé : malformé, domaine étranger à l'adresse, Secure hors https, préfixes non respectés.
        /// </summary>
        public static PageCookie? Parse(Uri url, string header, bool fromHttp, DateTimeOffset now)
        {
            if (url.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(header) || header.Length > MaxLength)
                return null;

            string[] parts = header.Split(';');
            int equals = parts[0].IndexOf('=');
            if (equals <= 0)
                return null;
            string name = parts[0][..equals].Trim();
            string value = parts[0][(equals + 1)..].Trim();
            if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is ' ' or '\t') || value.Any(char.IsControl))
                return null;

            string host = url.IdnHost.ToLowerInvariant();
            string? domain = null;
            string? path = null;
            DateTimeOffset? expires = null;
            DateTimeOffset? maxAge = null;
            bool secure = false, httpOnly = false;
            string? sameSite = null;

            foreach (string part in parts.Skip(1))
            {
                int split = part.IndexOf('=');
                string key = (split < 0 ? part : part[..split]).Trim().ToLowerInvariant();
                string argument = split < 0 ? string.Empty : part[(split + 1)..].Trim();
                switch (key)
                {
                    case "domain" when argument.Length > 0:
                        domain = argument.TrimStart('.').ToLowerInvariant();
                        break;
                    case "path" when argument.StartsWith('/'):
                        path = argument;
                        break;
                    case "expires" when ParseDate(argument) is { } date:
                        expires = date;
                        break;
                    case "max-age" when long.TryParse(argument, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds):
                        maxAge = seconds <= 0 ? DateTimeOffset.MinValue : now.AddSeconds(Math.Min(seconds, 400L * 24 * 3600));
                        break;
                    case "secure":
                        secure = true;
                        break;
                    case "httponly":
                        httpOnly = true;
                        break;
                    case "samesite":
                        sameSite = argument.ToLowerInvariant() switch
                        {
                            "strict" => "Strict",
                            "lax" => "Lax",
                            "none" => "None",
                            _ => null
                        };
                        break;
                }
            }

            // Un script (ou le module à sa place) ne pose pas de cookie HttpOnly.
            if (httpOnly && !fromHttp)
                return null;
            // Secure seulement depuis https ; SameSite=None exige Secure (comme Chromium).
            if (secure && url.Scheme != "https")
                return null;
            if (sameSite == "None" && !secure)
                return null;

            bool hostOnly = domain == null;
            if (domain != null)
            {
                // Domaine de l'adresse ou l'un de ses parents, jamais un suffixe public ni une adresse IP étrangère.
                if (!AdBlockDomain.IsSameOrSubdomain(host, domain) || !domain.Contains('.') ||
                    (IPAddress.TryParse(host, out _) && domain != host) ||
                    AdBlockDomain.GetRegistrableDomain(domain) != AdBlockDomain.GetRegistrableDomain(host) ||
                    domain.Length < AdBlockDomain.GetRegistrableDomain(host).Length)
                {
                    return null;
                }
            }
            path ??= DefaultPath(url);

            // Préfixes : __Secure- exige Secure ; __Host- exige Secure, l'hôte exact et le chemin /.
            if (name.StartsWith("__Secure-", StringComparison.Ordinal) && !secure)
                return null;
            if (name.StartsWith("__Host-", StringComparison.Ordinal) && (!secure || !hostOnly || path != "/"))
                return null;

            return new PageCookie(name, value, domain ?? host, hostOnly, path, maxAge ?? expires, secure, httpOnly, sameSite);
        }

        /// <summary>Chemin par défaut : celui de l'adresse jusqu'à son dernier « / » (RFC 6265, 5.1.4).</summary>
        static string DefaultPath(Uri url)
        {
            string path = url.AbsolutePath;
            int last = path.LastIndexOf('/');
            return last <= 0 ? "/" : path[..last];
        }

        /// <summary>Date d'un attribut Expires : « Wed, 21 Oct 2015 07:28:00 GMT », « Wed, 21-Oct-15 07:28:00 GMT »…</summary>
        static DateTimeOffset? ParseDate(string text)
        {
            string normalized = text.Replace('-', ' ').Trim();
            if (normalized.EndsWith(" GMT", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^4];
            return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset date)
                ? date
                : null;
        }
    }
}
