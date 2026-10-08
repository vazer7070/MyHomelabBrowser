using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace MyHomelabBrowser.classes.Import
{
    /// <summary>
    /// Historique d'un autre navigateur, à côté de ses favoris : base History des navigateurs
    /// Chromium (Chrome, Edge, Brave, Vivaldi, Opera), places.sqlite de Firefox. La base est
    /// copiée avant lecture (le navigateur la verrouille tant qu'il est ouvert). Une entrée par
    /// adresse, avec sa dernière visite : de quoi retrouver ses sites dans la barre d'adresse.
    /// </summary>
    public static class HistoryImporter
    {
        /// <summary>Adresses importées au plus (les plus récentes).</summary>
        public const int MaxEntries = 5000;

        /// <summary>Base d'historique du profil d'où viennent ces favoris ; null s'il n'en a pas.</summary>
        public static string? HistoryPath(BookmarkSource source)
        {
            string? path = source.Kind switch
            {
                BookmarkSourceKind.ChromiumJson => Path.Combine(Path.GetDirectoryName(source.Path) ?? string.Empty, "History"),
                BookmarkSourceKind.FirefoxPlaces => source.Path,
                _ => null
            };
            return path != null && File.Exists(path) ? path : null;
        }

        public static List<HistoryEntry> Read(BookmarkSource source, int limit = MaxEntries)
        {
            string? path = HistoryPath(source);
            if (path == null)
                return new List<HistoryEntry>();
            return source.Kind == BookmarkSourceKind.FirefoxPlaces ? ReadFirefox(path, limit) : ReadChromium(path, limit);
        }

        /// <summary>Table urls de Chromium : dernière visite en microsecondes depuis le 1er janvier 1601 (UTC).</summary>
        public static List<HistoryEntry> ReadChromium(string historyPath, int limit = MaxEntries)
            => WithCopy(historyPath, connection => Query(connection,
                "SELECT url, IFNULL(title, ''), last_visit_time FROM urls WHERE hidden = 0 AND last_visit_time > 0 " +
                "ORDER BY last_visit_time DESC LIMIT $limit", limit,
                microseconds => DateTime.FromFileTimeUtc(checked(microseconds * 10)).ToLocalTime()));

        /// <summary>Table moz_places de Firefox : dernière visite en microsecondes depuis 1970 (UTC).</summary>
        public static List<HistoryEntry> ReadFirefox(string placesPath, int limit = MaxEntries)
            => WithCopy(placesPath, connection => Query(connection,
                "SELECT url, IFNULL(title, ''), last_visit_date FROM moz_places WHERE hidden = 0 AND last_visit_date IS NOT NULL " +
                "ORDER BY last_visit_date DESC LIMIT $limit", limit,
                microseconds => DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).LocalDateTime));

        static List<HistoryEntry> Query(SqliteConnection connection, string sql, int limit, Func<long, DateTime> time)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$limit", limit);
            var entries = new List<HistoryEntry>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string url = reader.GetString(0);
                if (!IsImportable(url))
                    continue;
                DateTime visited;
                try
                {
                    visited = time(reader.GetInt64(2));
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
                {
                    continue;
                }
                entries.Add(new HistoryEntry { Url = url, Title = reader.GetString(1), VisitedAt = visited });
            }
            // De la plus ancienne à la plus récente, comme l'historique de PommeBrowser.
            entries.Reverse();
            return entries;
        }

        /// <summary>Pages web et fichiers seulement (pas les pages internes du navigateur d'origine).</summary>
        static bool IsImportable(string url)
            => url.Length <= 8192 && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile);

        static T WithCopy<T>(string databasePath, Func<SqliteConnection, T> read)
        {
            string folder = Path.Combine(Path.GetTempPath(), "pomme-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string copy = Path.Combine(folder, Path.GetFileName(databasePath));
                File.Copy(databasePath, copy);
                foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    if (File.Exists(databasePath + suffix))
                        File.Copy(databasePath + suffix, copy + suffix);
                }
                var builder = new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                return read(connection);
            }
            finally
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
