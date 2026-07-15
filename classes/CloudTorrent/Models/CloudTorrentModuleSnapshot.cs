using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public sealed class CloudTorrentModuleSnapshot
    {
        public CloudTorrentConnectionStatus Status { get; init; } = CloudTorrentConnectionStatus.NotConfigured;
        public string ServerUrl { get; init; } = string.Empty;
        public string Message { get; init; } = "CloudTorrent n’est pas configuré.";
        public string Username { get; init; } = string.Empty;
        public string ApiVersion { get; init; } = string.Empty;
        public bool AutoAnalyzePages { get; init; } = true;
        public bool HasStoredApiKey { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();
        public CloudTorrentAccount? Account { get; init; }
        public DateTimeOffset? LastValidatedAt { get; init; }

        public bool IsActive => Status == CloudTorrentConnectionStatus.Active && Account != null;

        public bool HasPermission(string permission)
        {
            if (string.IsNullOrWhiteSpace(permission))
                return false;

            foreach (var item in Permissions)
            {
                if (string.Equals(item, permission, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
