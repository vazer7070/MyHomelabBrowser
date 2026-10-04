using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.History;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Historique du profil : base SQLite partagée avec l'édition Windows (HistoryStore),
    /// plus les visites récentes gardées en mémoire pour la barre d'adresse et la page d'accueil.
    /// </summary>
    public sealed class HistoryService : IDisposable
    {
        public const int InMemory = 5000;

        readonly List<HistoryEntry> _recent = new();
        readonly HistoryStore? _store;

        public HistoryService(string databasePath)
        {
            try
            {
                var store = new HistoryStore(databasePath);
                // Élagage des visites les plus anciennes : sans attendre, il ne touche pas aux récentes.
                store.TrimInBackground();
                _recent.AddRange(store.LoadRecent(InMemory));
                _store = store;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Base illisible : la navigation continue sans historique.
                RuntimeLogBuffer.Append("[Historique] " + ex.Message);
            }
        }

        public event Action? Changed;

        public bool IsAvailable => _store != null;

        /// <summary>Visites récentes, de la plus ancienne à la plus récente.</summary>
        public IReadOnlyList<HistoryEntry> Recent => _recent;

        /// <summary>Enregistre une visite ; une page rechargée n'est comptée qu'une fois.</summary>
        public HistoryEntry? Record(string url, string? title, DateTime? now = null)
        {
            if (!IsRecordable(url))
                return null;

            HistoryEntry? last = _recent.Count > 0 ? _recent[^1] : null;
            if (last != null && string.Equals(last.Url, url, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(title) && title != last.Title)
                    UpdateTitle(url, title);
                return last;
            }

            var entry = new HistoryEntry { Url = url, Title = title ?? string.Empty, VisitedAt = now ?? DateTime.Now };
            _recent.Add(entry);
            if (_recent.Count > InMemory)
                _recent.RemoveRange(0, _recent.Count - InMemory);

            _store?.Add(entry);
            Changed?.Invoke();
            return entry;
        }

        /// <summary>Le titre arrive souvent après l'adresse : il complète la dernière visite de la page.</summary>
        public void UpdateTitle(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return;

            for (int i = _recent.Count - 1; i >= Math.Max(0, _recent.Count - 20); i--)
            {
                HistoryEntry entry = _recent[i];
                if (!string.Equals(entry.Url, url, StringComparison.Ordinal))
                    continue;

                if (entry.Title != title)
                {
                    entry.Title = title;
                    _store?.UpdateTitle(entry);
                    Changed?.Invoke();
                }
                return;
            }
        }

        public void Remove(IReadOnlyCollection<HistoryEntry> entries)
        {
            if (entries.Count == 0)
                return;

            var set = new HashSet<HistoryEntry>(entries);
            _recent.RemoveAll(set.Contains);
            _store?.Remove(set);
            Changed?.Invoke();
        }

        /// <summary>Efface les visites depuis <paramref name="since"/> (tout si DateTime.MinValue).</summary>
        public void RemoveSince(DateTime since)
        {
            _recent.RemoveAll(h => h.VisitedAt >= since);
            _store?.RemoveSince(since);
            Changed?.Invoke();
        }

        /// <summary>
        /// Visites importées d'un autre navigateur : écrites hors du fil de l'interface, puis la
        /// liste des visites récentes est relue (à appeler depuis le fil de l'interface).
        /// </summary>
        public async System.Threading.Tasks.Task<int> ImportAsync(IReadOnlyList<HistoryEntry> entries)
        {
            if (_store is not { } store || entries.Count == 0)
                return 0;
            store.Flush();
            int added = await System.Threading.Tasks.Task.Run(() => store.Import(entries));
            if (added > 0)
            {
                List<HistoryEntry> recent = await System.Threading.Tasks.Task.Run(() => store.LoadRecent(InMemory));
                _recent.Clear();
                _recent.AddRange(recent);
                Changed?.Invoke();
            }
            return added;
        }

        public static bool IsRecordable(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile);

        public void Flush() => _store?.Flush();

        public void Dispose() => _store?.Dispose();
    }
}
