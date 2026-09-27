using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyHomelabBrowser.classes.Import
{
    public enum BookmarkSourceKind
    {
        ChromiumJson,
        FirefoxPlaces,
        NetscapeHtml
    }

    public sealed record BookmarkSource(string Name, BookmarkSourceKind Kind, string Path);

    /// <summary>
    /// Favori lu dans un autre navigateur. <see cref="Folder"/> vaut null pour la barre
    /// de favoris (comme dans le navigateur d'origine), sinon le dossier de premier niveau.
    /// </summary>
    public sealed record ImportedBookmark(string Title, string Url, string? Folder);

    /// <summary>
    /// Import des favoris depuis les navigateurs Chromium (Chrome, Edge, Brave, Vivaldi,
    /// Opera), Firefox ou un fichier HTML exporté (format commun à tous les navigateurs).
    /// </summary>
    public static class BookmarkImporter
    {
        public const string DefaultFolder = "Importés";

        // =========================
        // Détection
        // =========================

        public static IReadOnlyList<BookmarkSource> DetectSources()
        {
            var sources = new List<BookmarkSource>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            AddChromiumProfiles(sources, "Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data"));
            AddChromiumProfiles(sources, "Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"));
            AddChromiumProfiles(sources, "Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
            AddChromiumProfiles(sources, "Vivaldi", Path.Combine(local, "Vivaldi", "User Data"));

            string opera = Path.Combine(roaming, "Opera Software", "Opera Stable", "Bookmarks");
            if (File.Exists(opera))
                sources.Add(new BookmarkSource("Opera", BookmarkSourceKind.ChromiumJson, opera));

            string firefoxProfiles = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(firefoxProfiles))
            {
                foreach (string profile in SafeEnumerateDirectories(firefoxProfiles))
                {
                    string places = Path.Combine(profile, "places.sqlite");
                    if (!File.Exists(places))
                        continue;

                    string folderName = Path.GetFileName(profile);
                    int dot = folderName.IndexOf('.');
                    string label = dot >= 0 && dot < folderName.Length - 1 ? folderName[(dot + 1)..] : folderName;
                    sources.Add(new BookmarkSource($"Firefox ({label})", BookmarkSourceKind.FirefoxPlaces, places));
                }
            }

            return sources;
        }

        private static void AddChromiumProfiles(List<BookmarkSource> sources, string browserName, string userDataFolder)
        {
            if (!Directory.Exists(userDataFolder))
                return;

            Dictionary<string, string> names = ReadChromiumProfileNames(userDataFolder);
            var found = new List<(string Folder, string File)>();

            foreach (string directory in SafeEnumerateDirectories(userDataFolder))
            {
                string bookmarks = Path.Combine(directory, "Bookmarks");
                if (File.Exists(bookmarks))
                    found.Add((Path.GetFileName(directory), bookmarks));
            }

            foreach (var (folder, file) in found.OrderBy(f => f.Folder == "Default" ? 0 : 1).ThenBy(f => f.Folder))
            {
                string label = found.Count == 1
                    ? browserName
                    : $"{browserName} ({(names.TryGetValue(folder, out string? name) ? name : folder)})";
                sources.Add(new BookmarkSource(label, BookmarkSourceKind.ChromiumJson, file));
            }
        }

        private static Dictionary<string, string> ReadChromiumProfileNames(string userDataFolder)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string localState = Path.Combine(userDataFolder, "Local State");
                if (!File.Exists(localState))
                    return names;

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localState));
                if (document.RootElement.TryGetProperty("profile", out JsonElement profile) &&
                    profile.TryGetProperty("info_cache", out JsonElement cache) &&
                    cache.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty entry in cache.EnumerateObject())
                    {
                        if (entry.Value.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } value)
                            names[entry.Name] = value;
                    }
                }
            }
            catch
            {
                // Noms de profils facultatifs.
            }
            return names;
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try
            {
                return Directory.EnumerateDirectories(path).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // =========================
        // Lecture
        // =========================

        public static IReadOnlyList<ImportedBookmark> Read(BookmarkSource source) => source.Kind switch
        {
            BookmarkSourceKind.ChromiumJson => ParseChromiumJson(File.ReadAllText(source.Path)),
            BookmarkSourceKind.FirefoxPlaces => ReadFirefoxPlaces(source.Path),
            _ => ParseNetscapeHtml(File.ReadAllText(source.Path))
        };

        /// <summary>
        /// Fichier « Bookmarks » des navigateurs Chromium.
        /// </summary>
        public static List<ImportedBookmark> ParseChromiumJson(string json)
        {
            var result = new List<ImportedBookmark>();
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("roots", out JsonElement roots))
                return result;

            foreach (JsonProperty root in roots.EnumerateObject())
            {
                if (root.Value.ValueKind != JsonValueKind.Object)
                    continue;

                bool isBar = root.Name == "bookmark_bar";
                string rootName = GetString(root.Value, "name") ?? (root.Name == "synced" ? "Favoris mobiles" : "Autres favoris");

                if (root.Value.TryGetProperty("children", out JsonElement children))
                    WalkChromium(children, isBar ? null : rootName, isBar, result);
            }

            return result;
        }

        private static void WalkChromium(JsonElement children, string? folder, bool atBarRoot, List<ImportedBookmark> result)
        {
            if (children.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement node in children.EnumerateArray())
            {
                string? type = GetString(node, "type");
                string title = GetString(node, "name") ?? string.Empty;

                if (type == "url")
                {
                    AddIfValid(result, title, GetString(node, "url"), folder);
                }
                else if (type == "folder" && node.TryGetProperty("children", out JsonElement nested))
                {
                    // Un seul niveau de dossier : les sous-dossiers rejoignent leur dossier de premier niveau.
                    string? childFolder = atBarRoot ? NonEmpty(title) ?? DefaultFolder : folder;
                    WalkChromium(nested, childFolder, atBarRoot: false, result);
                }
            }
        }

        /// <summary>
        /// Base places.sqlite de Firefox. Elle est copiée avant lecture : Firefox la
        /// verrouille tant qu'il est ouvert.
        /// </summary>
        public static List<ImportedBookmark> ReadFirefoxPlaces(string placesPath)
        {
            string tempFolder = Path.Combine(Path.GetTempPath(), "pomme-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFolder);

            try
            {
                string copy = Path.Combine(tempFolder, "places.sqlite");
                File.Copy(placesPath, copy);
                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    if (File.Exists(placesPath + suffix))
                        File.Copy(placesPath + suffix, copy + suffix);
                }

                var rows = new List<(long Id, long Parent, int Type, string Title, string? Url, string? Guid)>();
                var builder = new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false };

                using (var connection = new SqliteConnection(builder.ToString()))
                {
                    connection.Open();
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT b.id, b.parent, b.type, IFNULL(b.title, ''), p.url, b.guid " +
                        "FROM moz_bookmarks b LEFT JOIN moz_places p ON p.id = b.fk " +
                        "ORDER BY b.parent, b.position";

                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((
                            reader.GetInt64(0),
                            reader.GetInt64(1),
                            reader.GetInt32(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5)));
                    }
                }

                return BuildFirefoxTree(rows);
            }
            finally
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(tempFolder, recursive: true);
                }
                catch
                {
                }
            }
        }

        internal static List<ImportedBookmark> BuildFirefoxTree(
            IReadOnlyList<(long Id, long Parent, int Type, string Title, string? Url, string? Guid)> rows)
        {
            const int BookmarkType = 1;
            const int FolderType = 2;

            var byId = rows.ToDictionary(r => r.Id);
            var result = new List<ImportedBookmark>();

            foreach (var row in rows.Where(r => r.Type == BookmarkType))
            {
                // Remonte jusqu'à la racine pour connaître le dossier de premier niveau.
                var chain = new List<(long Id, long Parent, int Type, string Title, string? Url, string? Guid)>();
                long parent = row.Parent;
                int guard = 0;
                while (byId.TryGetValue(parent, out var folder) && folder.Type == FolderType && guard++ < 64)
                {
                    chain.Insert(0, folder);
                    if (folder.Parent == folder.Id)
                        break;
                    parent = folder.Parent;
                }

                // chain[0] = racine technique, chain[1] = barre / menu / autres / mobile.
                if (chain.Count < 2)
                    continue;

                // Les racines portent des titres techniques (« menu », « toolbar ») : on les nomme d'après leur GUID.
                var top = chain[1];
                string? folderName = top.Guid == "toolbar_____"
                    ? chain.Count > 2 ? NonEmpty(chain[2].Title) ?? DefaultFolder : null
                    : FirefoxRootName(top.Guid);

                AddIfValid(result, row.Title, row.Url, folderName);
            }

            return result;
        }

        private static string FirefoxRootName(string? guid) => guid switch
        {
            "menu________" => "Menu des marque-pages",
            "mobile______" => "Marque-pages mobiles",
            _ => "Autres marque-pages"
        };

        private static readonly Regex HtmlTokenRegex = new(
            @"<DL\b|</DL\s*>|<H3\b(?<attrs>[^>]*)>(?<folder>.*?)</H3\s*>|<A\b(?<attrs>[^>]*)>(?<title>.*?)</A\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex HrefRegex = new(
            "HREF\\s*=\\s*(\"(?<v>[^\"]*)\"|'(?<v>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Fichier HTML d'export des favoris (format Netscape, commun à tous les navigateurs).
        /// </summary>
        public static List<ImportedBookmark> ParseNetscapeHtml(string html)
        {
            var result = new List<ImportedBookmark>();
            var stack = new List<(string Name, bool IsToolbar)>();
            (string Name, bool IsToolbar)? pendingFolder = null;
            bool openedRootList = false;

            foreach (Match match in HtmlTokenRegex.Matches(html))
            {
                string token = match.Value;

                if (token.StartsWith("<DL", StringComparison.OrdinalIgnoreCase))
                {
                    // Le premier <DL> est la liste racine du document.
                    if (!openedRootList && pendingFolder == null)
                    {
                        openedRootList = true;
                        continue;
                    }

                    stack.Add(pendingFolder ?? (string.Empty, false));
                    pendingFolder = null;
                }
                else if (token.StartsWith("</DL", StringComparison.OrdinalIgnoreCase))
                {
                    if (stack.Count > 0)
                        stack.RemoveAt(stack.Count - 1);
                }
                else if (match.Groups["folder"].Success)
                {
                    string attrs = match.Groups["attrs"].Value;
                    pendingFolder = (
                        WebUtility.HtmlDecode(StripTags(match.Groups["folder"].Value)).Trim(),
                        attrs.Contains("PERSONAL_TOOLBAR_FOLDER", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    Match href = HrefRegex.Match(match.Groups["attrs"].Value);
                    if (!href.Success)
                        continue;

                    string? folder;
                    if (stack.Count == 0)
                        folder = DefaultFolder;
                    else if (stack[0].IsToolbar)
                        folder = stack.Count > 1 ? NonEmpty(stack[1].Name) ?? DefaultFolder : null;
                    else
                        folder = NonEmpty(stack[0].Name) ?? DefaultFolder;

                    AddIfValid(
                        result,
                        WebUtility.HtmlDecode(StripTags(match.Groups["title"].Value)).Trim(),
                        WebUtility.HtmlDecode(href.Groups["v"].Value),
                        folder);
                }
            }

            return result;
        }

        // =========================
        // Fusion
        // =========================

        /// <summary>
        /// Favoris à ajouter : les adresses déjà présentes (ou en double dans l'import) sont ignorées.
        /// </summary>
        public static List<FavoriteItem> SelectNew(IEnumerable<FavoriteItem> existing, IEnumerable<ImportedBookmark> imported)
        {
            var known = new HashSet<string>(existing.Select(f => NormalizeUrl(f.Url)), StringComparer.Ordinal);
            var added = new List<FavoriteItem>();

            foreach (ImportedBookmark bookmark in imported)
            {
                if (!known.Add(NormalizeUrl(bookmark.Url)))
                    continue;

                added.Add(new FavoriteItem
                {
                    Title = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title,
                    Url = bookmark.Url,
                    Folder = bookmark.Folder,
                    CreatedAt = DateTime.Now
                });
            }

            return added;
        }

        internal static string NormalizeUrl(string url)
        {
            string value = (url ?? string.Empty).Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                value = uri.AbsoluteUri;
            return value.TrimEnd('/').ToLowerInvariant();
        }

        private static void AddIfValid(List<ImportedBookmark> result, string title, string? url, string? folder)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
                !(uri.Scheme is "http" or "https" or "ftp" or "file"))
            {
                return;
            }

            result.Add(new ImportedBookmark(title.Trim(), uri.AbsoluteUri, folder));
        }

        private static string? GetString(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string? NonEmpty(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string StripTags(string value) => Regex.Replace(value, "<[^>]+>", string.Empty);
    }
}
