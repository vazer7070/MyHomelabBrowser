using Microsoft.Web.WebView2.Core;
using System;
using System.IO;

namespace MyHomelabBrowser.classes.Flash
{
    public static class RuffleAssetService
    {
        public const string VirtualHost = "ruffle.pomme.internal";
        public const string PinnedVersion = "0.3.0";
        public const string PinnedCdnScriptUrl = "https://unpkg.com/@ruffle-rs/ruffle@0.3.0";

        public static string AssetDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Assets", "Ruffle");

        public static string MainScriptPath =>
            Path.Combine(AssetDirectory, "ruffle.js");

        public static bool HasLocalAssets =>
            File.Exists(MainScriptPath) &&
            Directory.Exists(AssetDirectory);

        public static string LocalBaseUrl => $"https://{VirtualHost}/";
        public static string LocalScriptUrl => LocalBaseUrl + "ruffle.js";

        public static bool Configure(CoreWebView2? core)
        {
            if (core == null || !HasLocalAssets)
                return false;

            try
            {
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

        public static string GetPreferredScriptUrl() =>
            HasLocalAssets ? LocalScriptUrl : PinnedCdnScriptUrl;

        public static string GetPreferredPublicPath() =>
            HasLocalAssets ? LocalBaseUrl : "https://unpkg.com/@ruffle-rs/ruffle@0.3.0/";
    }
}
