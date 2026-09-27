namespace MyHomelabBrowser.classes.Support.Models
{
    public enum SupportDeliveryChannel
    {
        BackendApi,
        LocalFile
    }

    public sealed class SupportSubmissionResult
    {
        public bool Success { get; init; }
        public string ReportId { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public SupportDeliveryChannel Channel { get; init; }

        /// <summary>
        /// Vrai si le service de support était configuré mais injoignable.
        /// </summary>
        public bool UsedFallback { get; init; }

        /// <summary>
        /// Archive enregistrée quand le rapport n'a pas pu être envoyé en ligne.
        /// </summary>
        public string? FilePath { get; init; }
    }
}
