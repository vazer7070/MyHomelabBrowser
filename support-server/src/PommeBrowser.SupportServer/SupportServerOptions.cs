using System.Text.RegularExpressions;

namespace PommeBrowser.SupportServer;

/// <summary>
/// Réglages du serveur, lus dans les variables d'environnement (voir .env.example).
/// </summary>
public sealed class SupportServerOptions
{
    static readonly Regex DiscordWebhook = new(
        @"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/\d+/[\w-]+$",
        RegexOptions.CultureInvariant);

    /// <summary>Webhook utilisé quand la catégorie n'a pas le sien.</summary>
    public string? DefaultWebhook { get; init; }

    /// <summary>Webhook propre à une catégorie (clé : bug, ui_ux…).</summary>
    public IReadOnlyDictionary<string, string> CategoryWebhooks { get; init; } = new Dictionary<string, string>();

    /// <summary>Dossier où chaque rapport est conservé ; null si la copie sur disque est désactivée.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Durée de conservation des rapports sur disque (0 : illimitée).</summary>
    public int RetentionDays { get; init; } = 90;

    public int ReportsPerAddress { get; init; } = 5;
    public TimeSpan AddressWindow { get; init; } = TimeSpan.FromMinutes(10);
    public int ReportsPerHour { get; init; } = 60;

    /// <summary>Nombre de reverse proxies devant le serveur (0 : exposé directement).</summary>
    public int ProxyHops { get; init; } = 1;

    /// <summary>Adresse du client lue dans CF-Connecting-IP (serveur derrière Cloudflare).</summary>
    public bool BehindCloudflare { get; init; }

    public bool DeliversToDiscord => DefaultWebhook != null || CategoryWebhooks.Count > 0;

    public string? WebhookFor(string category)
        => CategoryWebhooks.TryGetValue(category, out string? webhook) ? webhook : DefaultWebhook;

    public static SupportServerOptions FromConfiguration(IConfiguration configuration)
    {
        var categoryWebhooks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string category in ReportCatalog.Categories)
        {
            if (ReadWebhook(configuration, "DISCORD_WEBHOOK_URL_" + category.ToUpperInvariant()) is { } webhook)
                categoryWebhooks[category] = webhook;
        }

        string? dataDirectory = configuration["SUPPORT_DATA_DIR"];
        if (string.Equals(configuration["SUPPORT_STORE_REPORTS"], "false", StringComparison.OrdinalIgnoreCase))
            dataDirectory = null;
        else if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");

        return new SupportServerOptions
        {
            DefaultWebhook = ReadWebhook(configuration, "DISCORD_WEBHOOK_URL"),
            CategoryWebhooks = categoryWebhooks,
            DataDirectory = dataDirectory,
            RetentionDays = ReadInt(configuration, "SUPPORT_RETENTION_DAYS", 90, min: 0),
            ReportsPerAddress = ReadInt(configuration, "SUPPORT_REPORTS_PER_ADDRESS", 5, min: 1),
            AddressWindow = TimeSpan.FromMinutes(ReadInt(configuration, "SUPPORT_ADDRESS_WINDOW_MINUTES", 10, min: 1)),
            ReportsPerHour = ReadInt(configuration, "SUPPORT_REPORTS_PER_HOUR", 60, min: 1),
            ProxyHops = ReadInt(configuration, "SUPPORT_PROXY_HOPS", 1, min: 0),
            BehindCloudflare = string.Equals(configuration["SUPPORT_BEHIND_CLOUDFLARE"], "true", StringComparison.OrdinalIgnoreCase)
        };
    }

    static string? ReadWebhook(IConfiguration configuration, string key)
    {
        string? value = configuration[key]?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        return DiscordWebhook.IsMatch(value)
            ? value
            : throw new InvalidOperationException($"{key} n'est pas une adresse de webhook Discord valide.");
    }

    static int ReadInt(IConfiguration configuration, string key, int fallback, int min)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return int.TryParse(value, out int parsed) && parsed >= min
            ? parsed
            : throw new InvalidOperationException($"{key} doit être un nombre entier supérieur ou égal à {min}.");
    }
}
