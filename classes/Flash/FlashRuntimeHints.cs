using System;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Paramètres récupérés sur l'intégration Flash d'origine. Ils servent à reproduire
    /// le comportement du plug-in sans maintenir de liste de domaines ou de jeux.
    /// </summary>
    public sealed record FlashRuntimeHints
    {
        public static FlashRuntimeHints Empty { get; } = new();

        public string? FlashVars { get; init; }
        public string? BaseUrl { get; init; }
        public string? AllowScriptAccess { get; init; }
        public bool AllowFullscreen { get; init; }
        public string? WindowMode { get; init; }
        public string? Quality { get; init; }
        public string? Scale { get; init; }
        public string? Align { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public bool IsDynamicEmbed { get; init; }
        public bool IsCrossOrigin { get; init; }
        public string? PageOrigin { get; init; }
        public string? SwfOrigin { get; init; }

        public bool RequestsScriptAccess =>
            string.Equals(AllowScriptAccess, "always", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(AllowScriptAccess, "samedomain", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(AllowScriptAccess, "true", StringComparison.OrdinalIgnoreCase);

        public bool CanGrantScriptAccess
        {
            get
            {
                if (string.Equals(AllowScriptAccess, "always", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(AllowScriptAccess, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return RequestsScriptAccess && !IsCrossOrigin;
            }
        }
    }
}
