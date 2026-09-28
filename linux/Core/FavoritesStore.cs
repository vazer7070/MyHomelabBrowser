using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Favoris du profil : même fichier et même format (favorites.json) que l'édition Windows.
    /// </summary>
    public sealed class FavoritesStore
    {
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        readonly string _path;
        readonly List<FavoriteItem> _items = new();
        bool _loaded;

        public FavoritesStore(string path)
        {
            _path = path;
            Load();
        }

        public event Action? Changed;

        public IReadOnlyList<FavoriteItem> All => _items;

        /// <summary>Dossiers utilisés, triés (null = racine, non inclus).</summary>
        public IReadOnlyList<string> Folders => _items
            .Select(f => f.Folder)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        public FavoriteItem? Find(string? url)
        {
            string key = Key(url);
            return key.Length == 0 ? null : _items.FirstOrDefault(f => Key(f.Url) == key);
        }

        public FavoriteItem Add(string title, string url, string? folder = null)
        {
            FavoriteItem? existing = Find(url);
            if (existing != null)
                return existing;

            var item = new FavoriteItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? url : title.Trim(),
                Url = url.Trim(),
                Folder = NormalizeFolder(folder)
            };
            _items.Add(item);
            SaveAndNotify();
            return item;
        }

        public void Update(Guid id, string title, string url, string? folder)
        {
            FavoriteItem? item = _items.FirstOrDefault(f => f.Id == id);
            if (item == null)
                return;

            item.Title = string.IsNullOrWhiteSpace(title) ? url.Trim() : title.Trim();
            item.Url = url.Trim();
            item.Folder = NormalizeFolder(folder);
            SaveAndNotify();
        }

        public bool Remove(Guid id)
        {
            if (_items.RemoveAll(f => f.Id == id) == 0)
                return false;
            SaveAndNotify();
            return true;
        }

        /// <summary>Ajoute les favoris importés absents ; renvoie le nombre d'ajouts.</summary>
        public int Import(IEnumerable<ImportedBookmark> bookmarks)
        {
            List<FavoriteItem> added = BookmarkImporter.SelectNew(_items, bookmarks);
            if (added.Count == 0)
                return 0;

            _items.AddRange(added);
            SaveAndNotify();
            return added.Count;
        }

        /// <summary>Clé de comparaison : même page malgré la casse de l'hôte ou une barre finale.</summary>
        public static string Key(string? url)
        {
            string value = (url ?? string.Empty).Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                value = uri.GetLeftPart(UriPartial.Query);
            return value.TrimEnd('/').ToLowerInvariant();
        }

        static string? NormalizeFolder(string? folder)
            => string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    List<FavoriteItem>? items = JsonSerializer.Deserialize<List<FavoriteItem>>(File.ReadAllText(_path), JsonOptions);
                    if (items != null)
                        _items.AddRange(items.Where(f => !string.IsNullOrWhiteSpace(f.Url)));
                }
                _loaded = true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fichier illisible : il n'est jamais écrasé (aucun enregistrement tant que _loaded est faux).
                RuntimeLogBuffer.Append("[Favoris] " + ex.Message);
            }
        }

        void SaveAndNotify()
        {
            if (_loaded)
            {
                try
                {
                    AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOptions));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogBuffer.Append("[Favoris] " + ex.Message);
                }
            }
            Changed?.Invoke();
        }
    }
}
