using System;
using System.Collections.Generic;
using System.IO;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Adresses passées au lancement (lien ouvert depuis une autre application, fichier ouvert
    /// avec PommeBrowser, ligne de commande). Un chemin de fichier existant devient une adresse
    /// file:// complète, résolue depuis le dossier du lancement : l'instance déjà ouverte, qui
    /// la reçoit, ne travaille pas dans le même dossier. Les options (« --… ») sont ignorées.
    /// </summary>
    public static class LaunchTargets
    {
        public static IReadOnlyList<string> Resolve(IEnumerable<string> arguments, string workingDirectory)
        {
            var targets = new List<string>();
            foreach (string raw in arguments)
            {
                string argument = raw.Trim();
                if (argument.Length == 0 || argument.StartsWith("--", StringComparison.Ordinal))
                    continue;
                targets.Add(ExistingFile(argument, workingDirectory) is { } file ? new Uri(file).AbsoluteUri : argument);
            }
            return targets;
        }

        /// <summary>Chemin complet d'un fichier existant ; null pour une adresse (https://…, exemple.fr…).</summary>
        static string? ExistingFile(string argument, string workingDirectory)
        {
            if (argument.Contains("://", StringComparison.Ordinal) || argument.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return null;
            try
            {
                string path = Path.GetFullPath(argument, workingDirectory);
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
