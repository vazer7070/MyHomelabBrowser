using Microsoft.Data.Sqlite;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;

namespace PommeBrowser.Tests;

public class BookmarkImporterTests
{
    [Fact]
    public void Chromium_bookmarks_keep_the_bar_and_first_level_folders()
    {
        const string json = """
        {
          "roots": {
            "bookmark_bar": {
              "name": "Barre de favoris", "type": "folder",
              "children": [
                { "type": "url", "name": "Proxmox", "url": "https://pve.lan:8006/" },
                { "type": "folder", "name": "Dev", "children": [
                  { "type": "url", "name": "GitHub", "url": "https://github.com/" },
                  { "type": "folder", "name": "Docker", "children": [
                    { "type": "url", "name": "Hub", "url": "https://hub.docker.com/" }
                  ]}
                ]},
                { "type": "url", "name": "Script", "url": "javascript:alert(1)" }
              ]
            },
            "other": {
              "name": "Autres favoris", "type": "folder",
              "children": [ { "type": "url", "name": "Wiki", "url": "https://fr.wikipedia.org/" } ]
            }
          }
        }
        """;

        var bookmarks = BookmarkImporter.ParseChromiumJson(json);

        Assert.Equal(4, bookmarks.Count);
        Assert.Contains(bookmarks, b => b.Title == "Proxmox" && b.Folder == null);
        Assert.Contains(bookmarks, b => b.Title == "GitHub" && b.Folder == "Dev");
        Assert.Contains(bookmarks, b => b.Title == "Hub" && b.Folder == "Dev");
        Assert.Contains(bookmarks, b => b.Title == "Wiki" && b.Folder == "Autres favoris");
        Assert.DoesNotContain(bookmarks, b => b.Url.StartsWith("javascript:"));
    }

    [Fact]
    public void Html_export_is_parsed_with_folders()
    {
        const string html = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3 ADD_DATE="1" PERSONAL_TOOLBAR_FOLDER="true">Barre de favoris</H3>
            <DL><p>
                <DT><A HREF="https://nas.lan:5001/" ADD_DATE="1">NAS &amp; fichiers</A>
                <DT><H3>Réseau</H3>
                <DL><p>
                    <DT><A HREF="http://192.168.1.1/">Routeur</A>
                </DL><p>
            </DL><p>
            <DT><H3>Lecture</H3>
            <DL><p>
                <DT><A HREF="https://lemonde.fr/">Le Monde</A>
            </DL><p>
            <DT><A HREF="https://example.com/">Racine</A>
        </DL><p>
        """;

        var bookmarks = BookmarkImporter.ParseNetscapeHtml(html);

        Assert.Equal(4, bookmarks.Count);
        Assert.Contains(bookmarks, b => b.Title == "NAS & fichiers" && b.Folder == null);
        Assert.Contains(bookmarks, b => b.Title == "Routeur" && b.Folder == "Réseau");
        Assert.Contains(bookmarks, b => b.Title == "Le Monde" && b.Folder == "Lecture");
        Assert.Contains(bookmarks, b => b.Title == "Racine" && b.Folder == BookmarkImporter.DefaultFolder);
    }

    [Fact]
    public void Firefox_places_database_is_read()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pomme-ff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string places = Path.Combine(folder, "places.sqlite");

        try
        {
            using (var connection = new SqliteConnection($"Data Source={places};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, title TEXT);
                    CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, parent INTEGER, position INTEGER, title TEXT, guid TEXT);
                    INSERT INTO moz_places VALUES (1, 'https://jellyfin.lan/', 'Jellyfin'), (2, 'https://mozilla.org/', 'Mozilla'), (3, 'place:sort=8', NULL);
                    INSERT INTO moz_bookmarks VALUES
                        (1, 2, NULL, 0, 0, '', 'root________'),
                        (2, 2, NULL, 1, 0, 'menu', 'menu________'),
                        (3, 2, NULL, 1, 1, 'toolbar', 'toolbar_____'),
                        (10, 1, 1, 3, 0, 'Jellyfin', 'a'),
                        (11, 1, 2, 2, 0, 'Mozilla', 'b'),
                        (12, 1, 3, 3, 1, 'Récents', 'c');
                    """;
                command.ExecuteNonQuery();
            }

            var bookmarks = BookmarkImporter.ReadFirefoxPlaces(places);

            Assert.Equal(2, bookmarks.Count);
            Assert.Contains(bookmarks, b => b.Title == "Jellyfin" && b.Folder == null);
            Assert.Contains(bookmarks, b => b.Title == "Mozilla" && b.Folder == "Menu des marque-pages");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Existing_and_duplicate_addresses_are_skipped()
    {
        var existing = new[] { new FavoriteItem { Title = "GitHub", Url = "https://github.com" } };
        var imported = new[]
        {
            new ImportedBookmark("GitHub", "https://github.com/", null),
            new ImportedBookmark("NAS", "https://nas.lan/", "Réseau"),
            new ImportedBookmark("NAS bis", "HTTPS://NAS.LAN", "Réseau"),
            new ImportedBookmark("", "https://sans-titre.fr/", null)
        };

        var added = BookmarkImporter.SelectNew(existing, imported);

        Assert.Equal(2, added.Count);
        Assert.Equal("Réseau", added[0].Folder);
        Assert.Equal("https://sans-titre.fr/", added[1].Title);
    }
}
