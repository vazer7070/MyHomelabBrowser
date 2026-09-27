using static MyHomelabBrowser.classes.Localization.Loc;
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
                return Tr("Aucun contenu Flash détecté");

            string source = string.IsNullOrWhiteSpace(SourceUrl)
                ? string.Empty
                : $" — {SourceUrl}";

            return Evidence switch
            {
                FlashEvidenceKind.Embed => Tr("Élément Flash <embed>") + source,
                FlashEvidenceKind.Object => Tr("Élément Flash <object>") + source,
                FlashEvidenceKind.ObjectParameter => Tr("Paramètre Flash movie/src") + source,
                FlashEvidenceKind.SwfObjectCall => Tr("Initialisation dynamique swfobject.embedSWF") + source,
                FlashEvidenceKind.NetworkRequest => Tr("Ressource SWF chargée par la page") + source,
                FlashEvidenceKind.ExistingRufflePlayer => Tr("Lecteur Ruffle déjà présent"),
                FlashEvidenceKind.DirectSwfNavigation => Tr("Fichier SWF ouvert directement") + source,
                _ => Tr("Contenu Flash détecté") + source
            };
        }
    }
}
