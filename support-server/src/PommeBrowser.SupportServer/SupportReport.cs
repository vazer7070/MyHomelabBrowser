using System.Text.Json.Serialization;

namespace PommeBrowser.SupportServer;

/// <summary>
/// Rapport tel qu'envoyé par le navigateur (partie « report » du formulaire).
/// Même contrat que SupportReportRequest côté application.
/// </summary>
public sealed class IncomingReport
{
    [JsonPropertyName("clientReportId")] public string? ClientReportId { get; set; }
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    [JsonPropertyName("module")] public string? Module { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("technicalInformation")] public string? TechnicalInformation { get; set; }
    [JsonPropertyName("context")] public IncomingContext? Context { get; set; }
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset? CreatedAtUtc { get; set; }
}

public sealed class IncomingContext
{
    [JsonPropertyName("browserMode")] public string? BrowserMode { get; set; }
    [JsonPropertyName("flashMode")] public string? FlashMode { get; set; }
    [JsonPropertyName("currentUrl")] public string? CurrentUrl { get; set; }
    [JsonPropertyName("pageTitle")] public string? PageTitle { get; set; }
    [JsonPropertyName("tabId")] public string? TabId { get; set; }
}

/// <summary>
/// Rapport vérifié et normalisé : c'est lui qui est conservé et transmis.
/// </summary>
public sealed record SupportReport
{
    public required string Id { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    public string? ClientReportId { get; init; }
    public required string ClientVersion { get; init; }
    public required string Module { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string TechnicalInformation { get; init; }
    public IncomingContext? Context { get; init; }

    [JsonIgnore]
    public byte[]? Log { get; init; }
}
