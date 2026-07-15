namespace MyHomelabBrowser.classes.Support.Models
{
    public enum SupportDeliveryChannel
    {
        BackendApi,
        LegacyDiscord
    }

    public sealed class SupportSubmissionResult
    {
        public bool Success { get; init; }
        public string ReportId { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public SupportDeliveryChannel Channel { get; init; }
        public bool UsedFallback { get; init; }
    }
}
