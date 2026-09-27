using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Profiles
{
    /// <summary>
    /// Données WebView2 d'un profil (cookies, cache, stockage des sites), rangées à part
    /// dans LocalAppData. Quand un profil est renommé ou supprimé, le dossier est souvent
    /// verrouillé par le processus du navigateur : l'opération est alors mémorisée et
    /// appliquée au démarrage suivant, avant la création des environnements WebView2.
    /// </summary>
    public static class WebViewProfileData
    {
        private const string PendingFileName = "pending-operations.json";

        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// Racine alternative (tests).
        /// </summary>
        internal static string? RootOverride { get; set; }

        public static string Root => RootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PommeBrowser",
            "Profiles");

        public static string NormalizeId(string? profileName)
        {
            string id = (profileName ?? string.Empty).Trim().ToLowerInvariant();
            return id.Length == 0 ? "default" : id;
        }

        public static string GetProfileFolder(string? profileName)
            => Path.Combine(Root, NormalizeId(profileName));

        public static string GetUserDataFolder(string? profileName)
            => Path.Combine(GetProfileFolder(profileName), "WebView2");

        /// <summary>
        /// Déplace immédiatement les données si le dossier est libre ; sinon, au prochain démarrage.
        /// </summary>
        public static void MoveOrSchedule(string oldName, string newName, bool folderInUse)
        {
            string from = NormalizeId(oldName);
            string to = NormalizeId(newName);
            if (from == to)
                return;

            lock (Gate)
            {
                List<PendingOperation> pending = LoadPending();

                // Données encore à leur emplacement d'origine (renommage précédent en attente) :
                // le déplacement en attente vise directement le nouveau nom.
                PendingOperation? chained = pending.FirstOrDefault(op => op.Kind == PendingKind.Move && op.To == from);
                if (chained != null)
                {
                    chained.To = to;
                    SavePending(pending);
                    return;
                }

                if (!folderInUse && TryMove(from, to))
                    return;

                pending.Add(new PendingOperation { Kind = PendingKind.Move, From = from, To = to });
                SavePending(pending);
            }
        }

        /// <summary>
        /// Supprime immédiatement les données si possible ; sinon, au prochain démarrage.
        /// </summary>
        public static void DeleteOrSchedule(string profileName, bool folderInUse)
        {
            string id = NormalizeId(profileName);

            lock (Gate)
            {
                List<PendingOperation> pending = LoadPending();

                // Un renommage en attente vers ce profil : ce sont les données d'origine qu'il faut effacer.
                PendingOperation? chained = pending.FirstOrDefault(op => op.Kind == PendingKind.Move && op.To == id);
                if (chained != null)
                {
                    pending.Remove(chained);
                    id = chained.From;
                }

                if (!folderInUse && TryDelete(id))
                {
                    SavePending(pending);
                    return;
                }

                if (!pending.Any(op => op.Kind == PendingKind.Delete && op.From == id))
                    pending.Add(new PendingOperation { Kind = PendingKind.Delete, From = id });

                SavePending(pending);
            }
        }

        /// <summary>
        /// À appeler au démarrage, avant toute création d'environnement WebView2.
        /// Les opérations qui échouent encore (autre instance ouverte) sont conservées.
        /// </summary>
        public static void ApplyPendingOperations()
        {
            lock (Gate)
            {
                List<PendingOperation> pending = LoadPending();
                if (pending.Count == 0)
                    return;

                var remaining = new List<PendingOperation>();
                foreach (PendingOperation operation in pending)
                {
                    bool done = operation.Kind == PendingKind.Move
                        ? TryMove(operation.From, operation.To)
                        : TryDelete(operation.From);

                    if (!done)
                        remaining.Add(operation);
                }

                SavePending(remaining);
            }
        }

        internal static IReadOnlyList<PendingOperation> GetPendingOperations()
        {
            lock (Gate)
                return LoadPending();
        }

        private static bool TryMove(string from, string to)
        {
            string source = Path.Combine(Root, from);
            string destination = Path.Combine(Root, to);

            try
            {
                if (!Directory.Exists(source))
                    return true;

                if (Directory.Exists(destination))
                {
                    // Un dossier vide peut avoir été créé entre-temps : il ne contient rien à préserver.
                    if (Directory.EnumerateFileSystemEntries(destination).Any())
                        return true;

                    Directory.Delete(destination);
                }

                Directory.Move(source, destination);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryDelete(string id)
        {
            string folder = Path.Combine(Root, id);

            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string PendingPath => Path.Combine(Root, PendingFileName);

        private static List<PendingOperation> LoadPending()
        {
            try
            {
                if (!File.Exists(PendingPath))
                    return new List<PendingOperation>();

                return JsonSerializer.Deserialize<List<PendingOperation>>(File.ReadAllText(PendingPath), JsonOptions)
                    ?? new List<PendingOperation>();
            }
            catch
            {
                return new List<PendingOperation>();
            }
        }

        private static void SavePending(List<PendingOperation> pending)
        {
            try
            {
                if (pending.Count == 0)
                {
                    if (File.Exists(PendingPath))
                        File.Delete(PendingPath);
                    return;
                }

                Directory.CreateDirectory(Root);
                AtomicFile.WriteAllText(PendingPath, JsonSerializer.Serialize(pending, JsonOptions));
            }
            catch
            {
                // Au pire, les données restent sous l'ancien nom : rien n'est perdu.
            }
        }

        internal enum PendingKind
        {
            Move,
            Delete
        }

        internal sealed class PendingOperation
        {
            public PendingKind Kind { get; set; }
            public string From { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
        }
    }
}
