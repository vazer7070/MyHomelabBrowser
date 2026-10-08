using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.History
{
    /// <summary>
    /// Historique de navigation en base SQLite (history.db dans le dossier du profil).
    /// Chaque visite est une ligne : une navigation n'écrit plus que sa propre entrée,
    /// au lieu de réécrire tout le fichier. Les écritures passent par une file unique
    /// en arrière-plan, dans l'ordre où elles sont demandées.
    /// </summary>
    public sealed class HistoryStore : IDisposable
    {
        /// <summary>Entrées conservées au maximum (les plus anciennes sont supprimées au démarrage).</summary>
        public const int MaxStoredEntries = 50_000;

        readonly string _connectionString;
        readonly object _queueLock = new();
        Task _queue = Task.CompletedTask;
        bool _disposed;

        public HistoryStore(string databasePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using SqliteConnection connection = Open();
            Execute(connection, """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS visits (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    visited_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS visits_visited_at ON visits (visited_at);
                """);
        }

        /// <summary>
        /// Reprend l'ancien history.json si la base est vide, puis le renomme en
        /// history.json.bak. Renvoie le nombre d'entrées importées.
        /// </summary>
        public int ImportLegacyJson(string jsonPath)
        {
            if (!File.Exists(jsonPath) || Count() > 0)
                return 0;

            List<HistoryEntry>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(jsonPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return 0;
            }

            var valid = (items ?? new List<HistoryEntry>())
                .Where(h => !string.IsNullOrWhiteSpace(h.Url))
                .OrderBy(h => h.VisitedAt)
                .ToList();

            using (SqliteConnection connection = Open())
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                foreach (HistoryEntry entry in valid)
                    Insert(connection, transaction, entry);
                transaction.Commit();
            }

            try
            {
                File.Move(jsonPath, jsonPath + ".bak", overwrite: true);
            }
            catch (IOException)
            {
            }

            return valid.Count;
        }

        /// <summary>
        /// Visites importées d'un autre navigateur, en une transaction ; une visite déjà présente
        /// (même adresse, même moment) n'est pas dupliquée. Renvoie le nombre de visites ajoutées.
        /// </summary>
        public int Import(IEnumerable<HistoryEntry> entries)
        {
            int added = 0;
            using (SqliteConnection connection = Open())
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO visits (url, title, visited_at) SELECT $url, $title, $at " +
                    "WHERE NOT EXISTS (SELECT 1 FROM visits WHERE url = $url AND visited_at = $at)";
                SqliteParameter url = command.Parameters.Add("$url", SqliteType.Text);
                SqliteParameter title = command.Parameters.Add("$title", SqliteType.Text);
                SqliteParameter at = command.Parameters.Add("$at", SqliteType.Integer);
                foreach (HistoryEntry entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Url))
                        continue;
                    url.Value = entry.Url;
                    title.Value = entry.Title ?? string.Empty;
                    at.Value = entry.VisitedAt.Ticks;
                    added += command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            Trim();
            return added;
        }

        /// <summary>Entrées les plus récentes, dans l'ordre chronologique.</summary>
        public List<HistoryEntry> LoadRecent(int limit)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, url, title, visited_at FROM visits ORDER BY visited_at DESC, id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);

            var entries = new List<HistoryEntry>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new HistoryEntry
                {
                    Id = reader.GetInt64(0),
                    Url = reader.GetString(1),
                    Title = reader.GetString(2),
                    VisitedAt = new DateTime(reader.GetInt64(3), DateTimeKind.Local)
                });
            }

            entries.Reverse();
            return entries;
        }

        public int Count()
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM visits";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>Garde les <paramref name="maxEntries"/> visites les plus récentes.</summary>
        public void Trim(int maxEntries = MaxStoredEntries)
        {
            using SqliteConnection connection = Open();
            Trim(connection, maxEntries);
        }

        /// <summary>Même élagage, avec les autres écritures (hors du fil de l'interface, au démarrage).</summary>
        public void TrimInBackground(int maxEntries = MaxStoredEntries)
            => Enqueue(connection => Trim(connection, maxEntries));

        static void Trim(SqliteConnection connection, int maxEntries)
        {
            // Cas courant : rien à retirer. Un comptage suffit, sans trier toute la table.
            using (SqliteCommand count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM visits";
                if ((long)count.ExecuteScalar()! <= maxEntries)
                    return;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM visits WHERE id NOT IN (
                    SELECT id FROM visits ORDER BY visited_at DESC, id DESC LIMIT $max)
                """;
            command.Parameters.AddWithValue("$max", maxEntries);
            command.ExecuteNonQuery();
        }

        public void Add(HistoryEntry entry)
            => Enqueue(connection => entry.Id = Insert(connection, null, entry));

        public void UpdateTitle(HistoryEntry entry)
        {
            string title = entry.Title;
            Enqueue(connection =>
            {
                // L'entrée vient d'être ajoutée : son Id est connu une fois l'insertion passée.
                if (entry.Id == 0)
                    return;

                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE visits SET title = $title WHERE id = $id";
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$id", entry.Id);
                command.ExecuteNonQuery();
            });
        }

        public void Remove(IEnumerable<HistoryEntry> entries)
        {
            HistoryEntry[] toRemove = entries.ToArray();
            Enqueue(connection =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM visits WHERE id = $id";
                SqliteParameter id = command.Parameters.Add("$id", SqliteType.Integer);

                foreach (HistoryEntry entry in toRemove.Where(e => e.Id != 0))
                {
                    id.Value = entry.Id;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            });
        }

        /// <summary>Supprime les visites à partir de <paramref name="since"/> (toutes si DateTime.MinValue).</summary>
        public void RemoveSince(DateTime since)
        {
            long ticks = since.Ticks;
            Enqueue(connection =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "DELETE FROM visits WHERE visited_at >= $since";
                command.Parameters.AddWithValue("$since", ticks);
                command.ExecuteNonQuery();
            });
        }

        public void Clear() => RemoveSince(DateTime.MinValue);

        /// <summary>Attend la fin des écritures en attente (changement de profil, fermeture).</summary>
        public void Flush()
        {
            Task pending;
            lock (_queueLock)
                pending = _queue;

            try
            {
                pending.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Flush();
            _disposed = true;
        }

        void Enqueue(Action<SqliteConnection> write)
        {
            lock (_queueLock)
            {
                if (_disposed)
                    return;

                _queue = _queue.ContinueWith(_ =>
                {
                    try
                    {
                        using SqliteConnection connection = Open();
                        write(connection);
                    }
                    catch (SqliteException ex)
                    {
                        // Disque plein, base verrouillée par l'antivirus… : l'entrée reste en mémoire.
                        System.Diagnostics.Debug.WriteLine("[Historique] " + ex.Message);
                    }
                }, TaskScheduler.Default);
            }
        }

        static long Insert(SqliteConnection connection, SqliteTransaction? transaction, HistoryEntry entry)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO visits (url, title, visited_at) VALUES ($url, $title, $at); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$url", entry.Url);
            command.Parameters.AddWithValue("$title", entry.Title ?? string.Empty);
            command.Parameters.AddWithValue("$at", entry.VisitedAt.Ticks);
            return (long)command.ExecuteScalar()!;
        }

        SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            Execute(connection, "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 3000;");
            return connection;
        }

        static void Execute(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
