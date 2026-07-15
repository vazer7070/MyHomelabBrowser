using System;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    /// <summary>
    /// Fournit l'instance CloudTorrent unique utilisée par tout le navigateur.
    /// Évite de recréer un service différent à chaque ouverture des paramètres.
    /// </summary>
    public static class CloudTorrentModuleHost
    {
        private static readonly Lazy<CloudTorrentModuleService> LazyCurrent =
            new(() => new CloudTorrentModuleService(), isThreadSafe: true);

        public static CloudTorrentModuleService Current => LazyCurrent.Value;
    }
}
