using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public sealed class CloudTorrentAccount
    {
        [JsonPropertyName("user")]
        public CloudTorrentUser User { get; set; } = new();

        [JsonPropertyName("apiKey")]
        public CloudTorrentApiKeyInfo ApiKey { get; set; } = new();

        [JsonPropertyName("quota")]
        public CloudTorrentQuota Quota { get; set; } = new();

        [JsonPropertyName("mediaProfile")]
        public CloudTorrentMediaProfile MediaProfile { get; set; } = new();

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new();

        [JsonPropertyName("counts")]
        public CloudTorrentCounts Counts { get; set; } = new();

        [JsonPropertyName("capabilities")]
        public CloudTorrentCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    public sealed class CloudTorrentUser
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("admin")]
        public bool IsAdmin { get; set; }
    }

    public sealed class CloudTorrentApiKeyInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CloudTorrentQuota
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("maxBytes")]
        public long MaxBytes { get; set; }

        [JsonPropertyName("usedBytes")]
        public long UsedBytes { get; set; }

        [JsonPropertyName("remainingBytes")]
        public long RemainingBytes { get; set; }
    }

    public sealed class CloudTorrentCounts
    {
        [JsonPropertyName("torrents")]
        public int Torrents { get; set; }

        [JsonPropertyName("mediaJobs")]
        public int MediaJobs { get; set; }

        [JsonPropertyName("hosterJobs")]
        public int HosterJobs { get; set; }

        [JsonPropertyName("activeTorrents")]
        public int ActiveTorrents { get; set; }

        [JsonPropertyName("activeMedia")]
        public int ActiveMedia { get; set; }

        [JsonPropertyName("activeHoster")]
        public int ActiveHoster { get; set; }

        [JsonIgnore]
        public int TotalActive => ActiveTorrents + ActiveMedia + ActiveHoster;
    }

    public sealed class CloudTorrentMediaProfile
    {
        [JsonPropertyName("quality")]
        public string Quality { get; set; } = "best";

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "video";

        [JsonPropertyName("includeSubtitles")]
        public bool IncludeSubtitles { get; set; }

        [JsonPropertyName("maxRetries")]
        public int MaxRetries { get; set; } = 2;

        [JsonPropertyName("queuePaused")]
        public bool QueuePaused { get; set; }
    }

    public sealed class CloudTorrentCapabilities
    {
        [JsonPropertyName("torrent")]
        public bool Torrent { get; set; }

        [JsonPropertyName("magnet")]
        public bool Magnet { get; set; }

        [JsonPropertyName("torrentUrl")]
        public bool TorrentUrl { get; set; }

        [JsonPropertyName("mediaAnalysis")]
        public bool MediaAnalysis { get; set; }

        [JsonPropertyName("mediaDownload")]
        public bool MediaDownload { get; set; }

        [JsonPropertyName("mediaFileDownload")]
        public bool MediaFileDownload { get; set; }

        [JsonPropertyName("mediaDelete")]
        public bool MediaDelete { get; set; }

        [JsonPropertyName("hosterRead")]
        public bool HosterRead { get; set; }

        [JsonPropertyName("hosterDownload")]
        public bool HosterDownload { get; set; }

        [JsonPropertyName("hosterFileDownload")]
        public bool HosterFileDownload { get; set; }

        [JsonPropertyName("hosterDelete")]
        public bool HosterDelete { get; set; }

        [JsonPropertyName("hls")]
        public bool Hls { get; set; }

        [JsonPropertyName("dash")]
        public bool Dash { get; set; }

        [JsonPropertyName("batch")]
        public bool Batch { get; set; }

        [JsonPropertyName("retry")]
        public bool Retry { get; set; }

        [JsonPropertyName("drm")]
        public bool Drm { get; set; }
    }
}
