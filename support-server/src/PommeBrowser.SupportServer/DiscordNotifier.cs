using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PommeBrowser.SupportServer;

/// <summary>
/// Publie un rapport sur le webhook Discord de sa catégorie (même présentation que
/// l'ancien envoi direct depuis le navigateur), journal en pièce jointe.
/// </summary>
public sealed class DiscordNotifier(HttpClient http, SupportServerOptions options, ILogger<DiscordNotifier> logger)
{
    // Accents lisibles dans le JSON envoyé (Discord le lit en UTF-8).
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    const int MaxEmbedDescription = 3400;
    const int MaxFieldValue = 1024;
    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    public async Task<bool> TrySendAsync(SupportReport report, CancellationToken cancellationToken)
    {
        string? webhook = options.WebhookFor(report.Category);
        if (webhook == null)
            return false;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using MultipartFormDataContent content = BuildMessage(report);
                using HttpResponseMessage response = await http.PostAsync(webhook, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return true;

                // Limite de débit de Discord : une seule nouvelle tentative, si l'attente est courte.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1 &&
                    RetryDelay(response) is { } delay && delay <= MaxRetryDelay)
                {
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                logger.LogWarning("Discord a refusé le rapport {ReportId} : HTTP {Status}", report.Id, (int)response.StatusCode);
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Discord injoignable pour le rapport {ReportId}", report.Id);
                return false;
            }
        }

        return false;
    }

    public static MultipartFormDataContent BuildMessage(SupportReport report)
    {
        var fields = new List<object>();
        var files = new List<(string Name, byte[] Content)>();

        if (report.Context is { } context && DescribeContext(context) is { Length: > 0 } contextText)
            fields.Add(new { name = "Contexte", value = Limit(contextText, MaxFieldValue), inline = false });

        string technical = report.TechnicalInformation.Length == 0
            ? "Aucune information technique jointe."
            : report.TechnicalInformation;
        if (technical.Length > MaxFieldValue)
        {
            files.Add(($"infos-techniques-{report.Id}.txt", Encoding.UTF8.GetBytes(technical)));
            technical = Limit(technical, MaxFieldValue - 40) + "\n(version complète en pièce jointe)";
        }
        fields.Add(new { name = "Informations techniques", value = technical, inline = false });

        if (report.Log != null)
            files.Add(($"journal-{report.Id}.txt", report.Log));

        string clientId = report.ClientReportId != null ? $" · navigateur `{report.ClientReportId}`" : string.Empty;
        string description = Limit(
            $"""
            🆔 **Rapport :** `{report.Id}`{clientId}
            🧭 **Version :** `{report.ClientVersion}`

            **{report.Title}**

            {report.Description}
            """, MaxEmbedDescription);

        var payload = new
        {
            username = "PommeBrowser Support",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = Limit($"{ReportCatalog.ModuleIcon(report.Module)} {ReportCatalog.ModuleLabel(report.Module)} — {ReportCatalog.CategoryLabel(report.Category)}", 256),
                    description,
                    color = ReportCatalog.ModuleColor(report.Module),
                    fields,
                    footer = new { text = "PommeBrowser – Support utilisateur" },
                    timestamp = report.ReceivedAtUtc.ToString("O")
                }
            }
        };

        var content = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"), "payload_json" }
        };

        for (int i = 0; i < files.Count; i++)
        {
            var file = new ByteArrayContent(files[i].Content);
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            content.Add(file, $"files[{i}]", files[i].Name);
        }

        return content;
    }

    static string DescribeContext(IncomingContext context)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(context.BrowserMode)) lines.Add($"Mode : {context.BrowserMode}");
        if (!string.IsNullOrEmpty(context.FlashMode)) lines.Add($"Flash : {context.FlashMode}");
        if (!string.IsNullOrEmpty(context.PageTitle)) lines.Add($"Page : {context.PageTitle}");
        if (!string.IsNullOrEmpty(context.CurrentUrl)) lines.Add($"Adresse : {context.CurrentUrl}");
        return string.Join('\n', lines);
    }

    static TimeSpan? RetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;

        if (response.Headers.TryGetValues("X-RateLimit-Reset-After", out var values) &&
            double.TryParse(values.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            return TimeSpan.FromSeconds(seconds);

        return null;
    }

    static string Limit(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
