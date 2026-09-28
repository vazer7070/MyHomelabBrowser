using Microsoft.Web.WebView2.Core;
using System;
using System.IO;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Ruffle est toujours servi depuis les fichiers de l'application (téléchargés et
    /// vérifiés par SHA-256 à la compilation, voir build/Ruffle.targets), jamais
    /// depuis un CDN : aucun script tiers n'est injecté dans les pages.
    /// </summary>
    public static class RuffleAssetService
    {
        public const string VirtualHost = "ruffle.pomme.internal";

        /// <summary>Version attendue (propriété RuffleVersion de build/Ruffle.targets).</summary>
        public const string PinnedVersion = "0.3.0";

        public static string AssetDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Assets", "Ruffle");

        public static string MainScriptPath =>
            Path.Combine(AssetDirectory, "ruffle.js");

        public static bool HasLocalAssets => File.Exists(MainScriptPath);

        /// <summary>Version réellement installée (VERSION.txt écrit à la compilation).</summary>
        public static string? InstalledVersion
        {
            get
            {
                try
                {
                    string file = Path.Combine(AssetDirectory, "VERSION.txt");
                    return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static string LocalBaseUrl => $"https://{VirtualHost}/";
        public static string LocalScriptUrl => LocalBaseUrl + "ruffle.js";

        public static bool Configure(CoreWebView2? core)
        {
            if (core == null || !HasLocalAssets)
                return false;

            try
            {
                // Lecture seule : les pages peuvent charger Ruffle, rien d'autre de l'application.
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost,
                    AssetDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
