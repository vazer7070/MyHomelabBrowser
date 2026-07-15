using System;

namespace MyHomelabBrowser.classes.Support
{
    /// <summary>
    /// Point unique à modifier lorsque le backend de support sera disponible.
    /// Tant que CompiledApiBaseUrl est vide, le navigateur conserve le transport
    /// Discord historique. Une URL peut aussi être fournie temporairement via la
    /// variable POMMEBROWSER_SUPPORT_API_URL pour tester le backend sans recompiler.
    /// </summary>
    public static class SupportApiConfiguration
    {
        // À renseigner lors du déploiement du backend, par exemple :
        // public const string CompiledApiBaseUrl = "https://support.example.tld";
        public const string CompiledApiBaseUrl = "";

        public const string ReportPath = "/api/v1/support/reports";

        // Pendant la transition, le support historique reste disponible.
        // À passer à false après validation du backend et suppression des webhooks
        // du code distribué.
        public const bool AllowLegacyDiscordFallback = true;

        public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(35);

        public static Uri? ResolveApiBaseUri()
        {
            string? environmentUrl = Environment.GetEnvironmentVariable(
                "POMMEBROWSER_SUPPORT_API_URL",
                EnvironmentVariableTarget.Process);

            string candidate = string.IsNullOrWhiteSpace(environmentUrl)
                ? CompiledApiBaseUrl
                : environmentUrl.Trim();

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
                return null;

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                return null;

            return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
        }

        public static Uri? ResolveReportUri()
        {
            Uri? baseUri = ResolveApiBaseUri();
            return baseUri == null
                ? null
                : new Uri(baseUri, ReportPath.TrimStart('/'));
        }
    }
}
