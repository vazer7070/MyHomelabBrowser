using System;
using System.IO;

namespace MyHomelabBrowser.classes.Profiles
{
    /// <summary>
    /// Supprime les fichiers laissés par des modules retirés du navigateur.
    /// CloudTorrent conservait une configuration et une clé API chiffrée par profil :
    /// on ne laisse pas un secret orphelin sur le disque après la suppression du module.
    /// </summary>
    internal static class RemovedFeatureCleanup
    {
        private static readonly string[] CloudTorrentFiles =
        {
            "cloudtorrent.json",
            "cloudtorrent.json.tmp",
            "cloudtorrent.key.dpapi",
            "cloudtorrent.key.dpapi.tmp"
        };

        public static void CleanCurrentRoot()
        {
            Clean(AppDataContext.Root);

            if (!string.Equals(AppDataContext.Root, AppDataContext.GlobalRoot, StringComparison.OrdinalIgnoreCase))
                Clean(AppDataContext.GlobalRoot);
        }

        private static void Clean(string root)
        {
            foreach (string fileName in CloudTorrentFiles)
                TryDelete(Path.Combine(root, fileName));

            try
            {
                foreach (string broken in Directory.EnumerateFiles(root, "cloudtorrent.json.invalid-*"))
                    TryDelete(broken);
            }
            catch
            {
                // Le dossier peut ne pas exister encore : rien à nettoyer.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // Fichier verrouillé : il sera retenté au prochain chargement du profil.
            }
        }
    }
}
