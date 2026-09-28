using System;
using System.IO;
using System.Linq;

namespace PommeBrowser.Linux.Core
{
    /// <summary>Nom de fichier d'un téléchargement : sûr, et jamais celui d'un fichier existant.</summary>
    public static class DownloadNames
    {
        public static string UniquePath(string directory, string? suggestedName, Func<string, bool>? exists = null)
        {
            exists ??= p => File.Exists(p) || Directory.Exists(p);
            string name = Sanitize(suggestedName);

            string candidate = Path.Combine(directory, name);
            if (!exists(candidate))
                return candidate;

            string extension = Path.GetExtension(name);
            string stem = Path.GetFileNameWithoutExtension(name);

            // « archive.tar.gz » devient « archive (1).tar.gz ».
            if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".tar" + extension;
                stem = stem[..^4];
            }

            for (int i = 1; i < 10_000; i++)
            {
                candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
                if (!exists(candidate))
                    return candidate;
            }

            return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
        }

        /// <summary>Retire les chemins et caractères interdits ; un nom vide devient « telechargement ».</summary>
        public static string Sanitize(string? suggestedName)
        {
            string name = Path.GetFileName((suggestedName ?? string.Empty).Replace('\\', '/'));
            name = new string(name.Where(c => c != '\0' && c != '/' && !char.IsControl(c)).ToArray()).Trim();

            // Un fichier caché ou « .. » ne doit pas apparaître par surprise.
            name = name.TrimStart('.');
            if (name.Length == 0)
                name = "telechargement";

            if (name.Length > 200)
            {
                string extension = Path.GetExtension(name);
                name = name[..(200 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
            }
            return name;
        }
    }
}
