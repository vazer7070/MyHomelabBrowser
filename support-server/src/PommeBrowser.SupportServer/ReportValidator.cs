using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PommeBrowser.SupportServer;

/// <summary>
/// Vérifie un rapport reçu et le ramène à des tailles sûres pour Discord et le disque.
/// Les limites suivent celles du formulaire du navigateur, avec un peu de marge.
/// </summary>
public static class ReportValidator
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 4000;
    public const int MaxTechnicalInformation = 20_000;
    public const int MaxLogBytes = 1024 * 1024;

    static readonly Regex ClientId = new(@"^PB-\d{8}-[A-Z0-9]{4,8}$", RegexOptions.CultureInvariant);
    static readonly Regex Version = new(@"^[0-9A-Za-z.+-]{1,40}$", RegexOptions.CultureInvariant);

    public static SupportReport Validate(IncomingReport? incoming, byte[]? log, DateTimeOffset now, string language)
    {
        if (incoming == null)
            throw new ReportRejectedException(Text.Get(language, "Rapport illisible.", "Unreadable report."));

        string title = Clean(incoming.Title, MaxTitle, singleLine: true);
        string description = Clean(incoming.Description, MaxDescription, singleLine: false);

        if (title.Length == 0 || description.Length == 0)
            throw new ReportRejectedException(Text.Get(language,
                "Le titre et la description sont obligatoires.",
                "The title and the description are required."));

        if (log is { Length: > MaxLogBytes })
            throw new ReportRejectedException(Text.Get(language, "Le journal joint est trop volumineux.", "The attached log is too large."));

        string? clientId = incoming.ClientReportId?.Trim();
        string? version = incoming.ClientVersion?.Trim();

        return new SupportReport
        {
            Id = NewId(now),
            ReceivedAtUtc = now,
            ClientReportId = clientId != null && ClientId.IsMatch(clientId) ? clientId : null,
            ClientVersion = version != null && Version.IsMatch(version) ? version : "inconnue",
            Module = ReportCatalog.NormalizeModule(incoming.Module),
            Category = ReportCatalog.NormalizeCategory(incoming.Category),
            Title = title,
            Description = description,
            TechnicalInformation = Clean(incoming.TechnicalInformation, MaxTechnicalInformation, singleLine: false),
            Context = incoming.Context == null ? null : new IncomingContext
            {
                BrowserMode = Clean(incoming.Context.BrowserMode, 40, singleLine: true),
                FlashMode = Clean(incoming.Context.FlashMode, 40, singleLine: true),
                CurrentUrl = Clean(incoming.Context.CurrentUrl, 500, singleLine: true),
                PageTitle = Clean(incoming.Context.PageTitle, 200, singleLine: true),
                TabId = Clean(incoming.Context.TabId, 80, singleLine: true)
            },
            Log = log is { Length: > 0 } ? log : null
        };
    }

    /// <summary>Identifiant communiqué à l'utilisateur : PB-AAAAMMJJ-XXXXXX.</summary>
    public static string NewId(DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> suffix = stackalloc char[6];
        for (int i = 0; i < suffix.Length; i++)
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"PB-{now.UtcDateTime:yyyyMMdd}-{suffix}";
    }

    // Retire les caractères de contrôle (sauf retours à la ligne), normalise les fins de ligne et tronque.
    static string Clean(string? value, int maxLength, bool singleLine)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength + 1));
        foreach (char c in value.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n')
                builder.Append(singleLine ? ' ' : '\n');
            else if (c == '\t')
                builder.Append(' ');
            else if (!char.IsControl(c))
                builder.Append(c);
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..(maxLength - 1)].TrimEnd() + "…";
    }
}

public sealed class ReportRejectedException(string message) : Exception(message);

/// <summary>Messages renvoyés au navigateur, dans sa langue (en-tête Accept-Language).</summary>
public static class Text
{
    public static string LanguageOf(HttpRequest request)
        => request.Headers.AcceptLanguage.ToString().TrimStart().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "fr";

    public static string Get(string language, string french, string english)
        => language == "en" ? english : french;
}
