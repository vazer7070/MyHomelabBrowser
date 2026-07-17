namespace MyHomelabBrowser.classes.Flash
{
    public enum FlashEvidenceKind
    {
        None,
        Embed,
        Object,
        ObjectParameter,
        SwfObjectCall,
        NetworkRequest,
        ExistingRufflePlayer,
        DirectSwfNavigation
    }

    public sealed record FlashDetectionResult(
        bool Detected,
        FlashEvidenceKind Evidence,
        string? SourceUrl,
        string? TargetElement,
        double Confidence,
        FlashRuntimeHints? RuntimeHints = null)
    {
        public static FlashDetectionResult None { get; } =
            new(false, FlashEvidenceKind.None, null, null, 0d, FlashRuntimeHints.Empty);

        public static FlashDetectionResult FromNetwork(string url) =>
            new(true, FlashEvidenceKind.NetworkRequest, url, null, 0.96d, FlashRuntimeHints.Empty);

        public FlashRuntimeHints Hints => RuntimeHints ?? FlashRuntimeHints.Empty;

        public string Describe()
        {
            if (!Detected)
                return "Aucun contenu Flash détecté";

            string source = string.IsNullOrWhiteSpace(SourceUrl)
                ? string.Empty
                : $" — {SourceUrl}";

            return Evidence switch
            {
                FlashEvidenceKind.Embed => "Élément Flash <embed>" + source,
                FlashEvidenceKind.Object => "Élément Flash <object>" + source,
                FlashEvidenceKind.ObjectParameter => "Paramètre Flash movie/src" + source,
                FlashEvidenceKind.SwfObjectCall => "Initialisation dynamique swfobject.embedSWF" + source,
                FlashEvidenceKind.NetworkRequest => "Ressource SWF chargée par la page" + source,
                FlashEvidenceKind.ExistingRufflePlayer => "Lecteur Ruffle déjà présent",
                FlashEvidenceKind.DirectSwfNavigation => "Fichier SWF ouvert directement" + source,
                _ => "Contenu Flash détecté" + source
            };
        }
    }
}
