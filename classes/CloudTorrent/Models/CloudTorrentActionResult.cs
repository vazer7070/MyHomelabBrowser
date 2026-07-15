using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public sealed class CloudTorrentQueueResult
    {
        public bool Success { get; init; }
        public bool Duplicate { get; init; }
        public string Message { get; init; } = string.Empty;
        public CloudTorrentDetectedItem? Item { get; init; }
    }

    public sealed class CloudTorrentActionResult
    {
        [JsonPropertyName("duplicate")]
        public bool Duplicate { get; set; }

        [JsonPropertyName("expanded")]
        public bool Expanded { get; set; }

        [JsonPropertyName("job")]
        public JsonElement Job { get; set; }

        [JsonPropertyName("jobs")]
        public JsonElement Jobs { get; set; }

        [JsonPropertyName("duplicates")]
        public JsonElement Duplicates { get; set; }

        [JsonPropertyName("errors")]
        public JsonElement Errors { get; set; }

        public string GetTitle(string fallback)
        {
            if (Job.ValueKind == JsonValueKind.Object)
            {
                if (Job.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String)
                    return title.GetString() ?? fallback;
                if (Job.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    return name.GetString() ?? fallback;
            }

            return fallback;
        }
    }
}
