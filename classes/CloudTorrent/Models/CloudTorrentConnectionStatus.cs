namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public enum CloudTorrentConnectionStatus
    {
        NotConfigured,
        Validating,
        Active,
        InvalidConfiguration,
        InvalidApiKey,
        Forbidden,
        ServerUnavailable,
        Error
    }
}
