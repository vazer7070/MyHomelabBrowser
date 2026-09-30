using System;

namespace PommeBrowser.Linux.Core
{
    /// <summary>Fichiers de Ruffle que le schéma pomme-ruffle:// accepte de servir.</summary>
    public static class RuffleAssets
    {
        /// <summary>Seuls les fichiers de Ruffle eux-mêmes : ni sous-dossier, ni fichier caché, ni autre type.</summary>
        public static bool IsAssetName(string? name)
            => name is { Length: > 0 and < 128 } &&
               name.IndexOfAny(new[] { '/', '\\', '\0', '%' }) < 0 &&
               !name.StartsWith('.') &&
               (name.EndsWith(".js", StringComparison.Ordinal) || name.EndsWith(".wasm", StringComparison.Ordinal));
    }
}
