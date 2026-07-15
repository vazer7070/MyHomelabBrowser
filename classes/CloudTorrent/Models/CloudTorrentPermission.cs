using System.Collections.Generic;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public static class CloudTorrentPermission
    {
        public const string AccountRead = "account.read";
        public const string TorrentsRead = "torrents.read";
        public const string TorrentsWrite = "torrents.write";
        public const string MediaRead = "media.read";
        public const string MediaAnalyze = "media.analyze";
        public const string MediaWrite = "media.write";
        public const string MediaDownload = "media.download";
        public const string MediaDelete = "media.delete";
        public const string FilesRead = "files.read";
        public const string FilesWrite = "files.write";
        public const string FilesDownload = "files.download";
        public const string FilesDelete = "files.delete";

        public static readonly IReadOnlyDictionary<string, string> Labels =
            new Dictionary<string, string>
            {
                [AccountRead] = "Compte et quota",
                [TorrentsRead] = "Consulter les torrents",
                [TorrentsWrite] = "Ajouter et piloter les torrents",
                [MediaRead] = "Consulter les vidéos",
                [MediaAnalyze] = "Analyser les pages et médias",
                [MediaWrite] = "Ajouter et piloter les vidéos",
                [MediaDownload] = "Télécharger les vidéos terminées",
                [MediaDelete] = "Supprimer les vidéos",
                [FilesRead] = "Consulter les fichiers hébergés",
                [FilesWrite] = "Ajouter et piloter les fichiers",
                [FilesDownload] = "Télécharger les fichiers terminés",
                [FilesDelete] = "Supprimer les fichiers"
            };
    }
}
