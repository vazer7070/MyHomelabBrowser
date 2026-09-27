using System;
using System.IO;
using System.Text;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Écriture « tout ou rien » : le contenu est écrit dans un fichier temporaire
    /// puis substitué au fichier final. Un arrêt brutal pendant l'écriture ne laisse
    /// donc jamais un JSON tronqué (profils, historique, favoris, session…).
    /// </summary>
    public static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        public static void WriteAllText(string path, string content)
            => WriteAllBytes(path, Utf8NoBom.GetBytes(content ?? string.Empty));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           64 * 1024,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }
}
