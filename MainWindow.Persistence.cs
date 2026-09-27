using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private DispatcherTimer? _historySaveTimer;
        private readonly SemaphoreSlim _historyWriteLock = new(1, 1);

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
        // Historique
        // ---------------------------
        void LoadHistory()
        {
            _history.Clear();

            try
            {
                if (File.Exists(HistoryPath))
                {
                    var json = File.ReadAllText(HistoryPath);
                    var items = JsonSerializer.Deserialize<List<HistoryEntry>>(json, HistoryJsonOpts);

                    // L'ordre chronologique est un invariant utilisé par l'omnibox.
                    if (items != null)
                        _history.AddRange(items.Where(h => !string.IsNullOrWhiteSpace(h.Url)).OrderBy(h => h.VisitedAt));
                }
            }
            catch
            {
                _history.Clear();
            }
            finally
            {
                _historyLoaded = true;
            }
        }

        /// <summary>
        /// Regroupe les écritures : une navigation active ne déclenche qu'une écriture
        /// toutes les quelques secondes, sur un thread de fond.
        /// </summary>
        void ScheduleHistorySave()
        {
            if (_historySaveTimer == null)
            {
                _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _historySaveTimer.Tick += (_, _) =>
                {
                    _historySaveTimer.Stop();
                    _ = SaveHistoryAsync();
                };
            }

            _historySaveTimer.Stop();
            _historySaveTimer.Start();
        }

        async Task SaveHistoryAsync()
        {
            // Ne jamais écraser le fichier avant le premier chargement.
            if (!_historyLoaded)
                return;

            // Instantané pris sur le thread UI, sérialisation et écriture en arrière-plan.
            string path = HistoryPath;
            HistoryEntry[] snapshot = _history.ToArray();

            await _historyWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    string json = JsonSerializer.Serialize(snapshot, HistoryJsonOpts);
                    AtomicFile.WriteAllText(path, json);
                }).ConfigureAwait(false);
            }
            catch
            {
                // Écriture impossible (disque plein, antivirus) : nouvel essai à la prochaine visite.
            }
            finally
            {
                _historyWriteLock.Release();
            }
        }

        void SaveHistoryNow()
        {
            _historySaveTimer?.Stop();

            if (!_historyLoaded)
                return;

            _historyWriteLock.Wait();
            try
            {
                AtomicFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history, HistoryJsonOpts));
            }
            catch
            {
            }
            finally
            {
                _historyWriteLock.Release();
            }
        }

        void RemoveHistoryEntry(HistoryEntry entry)
        {
            _history.Remove(entry);
            ScheduleHistorySave();
        }

        void RemoveHistoryEntries(IReadOnlyCollection<HistoryEntry> entries)
        {
            if (entries.Count == 0)
                return;

            var toRemove = new HashSet<HistoryEntry>(entries);
            _history.RemoveAll(toRemove.Contains);
            _lastHistoryUrl = "";
            ScheduleHistorySave();
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

            OpenViewTab(view, "Historique");
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
            SaveHistoryNow();
            SaveFavorites();
            DownloadManager.Instance.SaveHistory();
        }
    }
}
