using System;
using System.Linq;
using System.Reflection;

namespace MyHomelabBrowser.classes.Support
{
    /// <summary>
    /// Adresse du serveur de support (dossier support-server du dépôt). Elle est fixée à la
    /// compilation par la propriété SupportApiUrl de MyHomelabBrowser.csproj ; tant qu'elle
    /// est vide, les rapports sont enregistrés dans une archive locale. La variable
    /// POMMEBROWSER_SUPPORT_API_URL permet de viser un autre serveur sans recompiler.
    /// </summary>
    public static class SupportApiConfiguration
    {
        public static string CompiledApiBaseUrl { get; } =
            typeof(SupportApiConfiguration).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SupportApiUrl")?.Value ?? string.Empty;

        public const string ReportPath = "/api/v1/support/reports";

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
