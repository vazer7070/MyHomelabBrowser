using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Dossiers de données et de cache d'un profil créé (~/.local/share/pommebrowser/profiles/&lt;nom&gt;
    /// et son équivalent dans ~/.cache). Ils suivent le profil quand il est renommé ou supprimé.
    /// </summary>
    public static class ProfileData
    {
        static IEnumerable<string> Roots => new[] { LinuxPaths.BaseDataDirectory, LinuxPaths.BaseCacheDirectory };

        static string PendingMovesPath => Path.Combine(LinuxPaths.BaseDataDirectory, "profile-moves.json");

        /// <summary>
        /// Renommage : le moteur web garde les fichiers du profil ouverts jusqu'à la fin du processus.
        /// Le déplacement est fait au démarrage suivant, avant tout accès aux données.
        /// </summary>
        public static void ScheduleMove(string oldName, string newName)
        {
            var moves = LoadPendingMoves();
            moves.Add(new[] { oldName, newName });
            Directory.CreateDirectory(LinuxPaths.BaseDataDirectory);
            AtomicFile.WriteAllText(PendingMovesPath, JsonSerializer.Serialize(moves));
        }

        public static void ApplyPendingMoves()
        {
            List<string[]> moves = LoadPendingMoves();
            if (moves.Count == 0)
                return;

            foreach (string[] move in moves.Where(m => m.Length == 2))
            {
                try
                {
                    Move(move[0], move[1]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogBuffer.Append("[Profils] " + ex.Message);
                }
            }

            try
            {
                File.Delete(PendingMovesPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        static List<string[]> LoadPendingMoves()
        {
            try
            {
                if (File.Exists(PendingMovesPath))
                    return JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(PendingMovesPath)) ?? new();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
            return new();
        }

        /// <summary>Déplace les données d'un profil renommé. Un dossier de destination non vide n'est jamais fusionné.</summary>
        public static void Move(string oldName, string newName)
        {
            foreach (string root in Roots)
            {
                string source = LinuxPaths.ProfileDirectory(root, oldName);
                string destination = LinuxPaths.ProfileDirectory(root, newName);
                if (source == destination || !Directory.Exists(source))
                    continue;

                if (Directory.Exists(destination))
                {
                    if (Directory.EnumerateFileSystemEntries(destination).Any())
                        continue;
                    Directory.Delete(destination);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(source, destination);
            }
        }

        /// <summary>
        /// Données laissées par un profil supprimé ou renommé pendant que le moteur web s'en servait
        /// (il peut recréer un dossier avant de s'arrêter). Un dossier n'est orphelin que si le profil
        /// n'existe plus, ni dans la liste des profils ni dans la configuration.
        /// </summary>
        public static IReadOnlyList<string> FindOrphans(string root, Func<string, bool> profileExists)
        {
            string profiles = Path.Combine(root, "profiles");
            if (!Directory.Exists(profiles))
                return Array.Empty<string>();

            return Directory.EnumerateDirectories(profiles)
                .Where(dir => !profileExists(Path.GetFileName(dir)))
                .ToList();
        }

        public static void RemoveOrphans(Func<string, bool> profileExists)
        {
            foreach (string root in Roots)
            {
                foreach (string orphan in FindOrphans(root, profileExists))
                    TryDelete(orphan);
            }
        }

        static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nouvel essai au prochain démarrage (RemoveOrphans).
            }
        }
    }
}
