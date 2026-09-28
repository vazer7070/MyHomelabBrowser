using Microsoft.Data.Sqlite;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.History;
using MyHomelabBrowser.controles;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        /// <summary>Visites gardées en mémoire (omnibox, page d'accueil, vue Historique).</summary>
        const int HistoryInMemory = 5000;

        HistoryStore? _historyStore;

        string GetProfileDataDir()
        {
            var profileId = (_profileService.Current?.Username ?? "default").Trim();

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MyHomelabBrowser",
                "profiles",
                profileId
            );

            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------------------------
        // Historique (base SQLite du profil)
        // ---------------------------
        void LoadHistory()
        {
            _history.Clear();
            _historyStore?.Dispose();
            _historyStore = null;

            try
            {
                var store = new HistoryStore(Path.Combine(GetProfileDataDir(), "history.db"));

                // Ancien format : history.json est repris une fois, puis gardé en .bak.
                store.ImportLegacyJson(HistoryPath);
                store.Trim();

                // L'ordre chronologique est un invariant utilisé par l'omnibox.
                _history.AddRange(store.LoadRecent(HistoryInMemory));
                _historyStore = store;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Base illisible : la navigation continue, sans enregistrement de l'historique.
                RuntimeLogBuffer.Append("[Historique] " + ex.Message);
            }
        }

        void AddHistoryEntry(HistoryEntry entry)
        {
            _history.Add(entry);
            _historyStore?.Add(entry);

            if (_history.Count > HistoryInMemory)
                _history.RemoveRange(0, _history.Count - HistoryInMemory);
        }

        void RemoveHistoryEntry(HistoryEntry entry)
        {
            _history.Remove(entry);
            _historyStore?.Remove(new[] { entry });
        }

        void RemoveHistoryEntries(IReadOnlyCollection<HistoryEntry> entries)
        {
            if (entries.Count == 0)
                return;

            var toRemove = new HashSet<HistoryEntry>(entries);
            bool everything = _history.All(toRemove.Contains);

            _history.RemoveAll(toRemove.Contains);
            _lastHistoryUrl = "";

            // Tout ce qui est affiché est effacé : les visites plus anciennes de la base aussi.
            if (everything)
                _historyStore?.Clear();
            else
                _historyStore?.Remove(toRemove);
        }

        /// <summary>Efface les visites depuis <paramref name="since"/> (tout si DateTime.MinValue).</summary>
        void RemoveHistorySince(DateTime since)
        {
            _history.RemoveAll(h => h.VisitedAt >= since);
            _lastHistoryUrl = "";
            _historyStore?.RemoveSince(since);
        }

        void OpenHistory()
        {
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent v && v.View is HistoryView)
                {
                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            var view = new HistoryView(
                _history,
                (url, _) => Navigate(url),
                RemoveHistoryEntry,
                RemoveHistoryEntries);

            OpenViewTab(view, Tr("Historique"));
        }

        // ---------------------------
        // Favoris
        // ---------------------------
        void LoadFavorites()
        {
            _favorites.Clear();

            try
            {
                if (File.Exists(FavoritesPath))
                {
                    var json = File.ReadAllText(FavoritesPath);
                    var items = JsonSerializer.Deserialize<List<FavoriteItem>>(json, JsonOpts);

                    if (items != null)
                        _favorites.AddRange(items.Where(f => !string.IsNullOrWhiteSpace(f.Url)));
                }
            }
            catch
            {
                _favorites.Clear();
            }
            finally
            {
                _favoritesLoaded = true;
            }
        }

        void SaveFavorites()
        {
            // Ne jamais écraser le fichier avant le premier chargement.
            if (!_favoritesLoaded)
                return;

            try
            {
                AtomicFile.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favorites, JsonOpts));
            }
            catch
            {
            }
        }

        /// <summary>
        /// Termine les écritures différées. Appelé avant un changement de profil
        /// (les chemins dépendent du profil courant) et à la fermeture.
        /// </summary>
        void FlushPersistentState()
        {
            _historyStore?.Flush();
            SaveFavorites();
            DownloadManager.Instance.SaveHistory();
        }
    }
}
