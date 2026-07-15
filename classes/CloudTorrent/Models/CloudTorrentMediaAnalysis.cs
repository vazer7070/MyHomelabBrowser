using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public sealed class CloudTorrentMediaAnalysisResponse
    {
        [JsonPropertyName("analysis")]
        public CloudTorrentMediaAnalysis Analysis { get; set; } = new();
    }

    public sealed class CloudTorrentMediaAnalysis
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("thumbnail")]
        public string Thumbnail { get; set; } = string.Empty;

        [JsonPropertyName("extractor")]
        public string Extractor { get; set; } = string.Empty;

        [JsonPropertyName("webpageUrl")]
        public string WebpageUrl { get; set; } = string.Empty;

        [JsonPropertyName("live")]
        public bool IsLive { get; set; }

        [JsonPropertyName("presets")]
        public List<CloudTorrentMediaPreset> Presets { get; set; } = new();

        [JsonPropertyName("formats")]
        public List<CloudTorrentMediaFormat> Formats { get; set; } = new();
    }

    public sealed class CloudTorrentMediaPreset
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;
    }

    public sealed class CloudTorrentMediaFormat
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("extension")]
        public string Extension { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("fps")]
        public double? Fps { get; set; }

        [JsonPropertyName("videoCodec")]
        public string VideoCodec { get; set; } = string.Empty;

        [JsonPropertyName("audioCodec")]
        public string AudioCodec { get; set; } = string.Empty;

        [JsonPropertyName("filesize")]
        public long? Filesize { get; set; }
    }

    public sealed class CloudTorrentQualityChoice
    {
        public string Kind { get; init; } = "preset";
        public string Id { get; init; } = "best";
        public string Label { get; init; } = "Meilleure qualité";
        public bool IsAudio { get; init; }

        public override string ToString() => Label;
    }
}
