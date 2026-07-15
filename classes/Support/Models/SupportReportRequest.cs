using System;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.Support.Models
{
    /// <summary>
    /// Contrat neutre du rapport de support. Cette classe ne contient aucune
    /// notion Discord afin que le backend puisse choisir lui-même son routage.
    /// </summary>
    public sealed class SupportReportRequest
    {
        [JsonPropertyName("clientReportId")]
        public string ClientReportId { get; init; } = string.Empty;

        [JsonPropertyName("client")]
        public string Client { get; init; } = "PommeBrowser";

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; init; } = string.Empty;

        [JsonPropertyName("module")]
        public string Module { get; init; } = "browser";

        [JsonPropertyName("moduleLabel")]
        public string ModuleLabel { get; init; } = "PommeBrowser";

        [JsonPropertyName("category")]
        public string Category { get; init; } = "other";

        [JsonPropertyName("categoryLabel")]
        public string CategoryLabel { get; init; } = "Autre";

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("technicalInformation")]
        public string TechnicalInformation { get; init; } = string.Empty;

        [JsonPropertyName("context")]
        public SupportReportContext? Context { get; init; }

        [JsonPropertyName("createdAtUtc")]
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    public sealed class SupportReportContext
    {
        [JsonPropertyName("browserMode")]
        public string? BrowserMode { get; init; }

        [JsonPropertyName("flashMode")]
        public string? FlashMode { get; init; }

        [JsonPropertyName("currentUrl")]
        public string? CurrentUrl { get; init; }

        [JsonPropertyName("pageTitle")]
        public string? PageTitle { get; init; }

        [JsonPropertyName("tabId")]
        public string? TabId { get; init; }
    }
}
