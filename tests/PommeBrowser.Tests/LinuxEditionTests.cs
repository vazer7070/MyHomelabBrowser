using Microsoft.Data.Sqlite;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Import;
using MyHomelabBrowser.classes.Localization;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Tests;

/// <summary>Logique de l'édition Linux (hors interface GTK).</summary>
[Collection(nameof(LocalizationCollection))]
public sealed class LinuxEditionTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "pomme-linux-" + Guid.NewGuid().ToString("N"));

    public LinuxEditionTests()
    {
        Directory.CreateDirectory(_directory);
        Loc.Initialize("fr", null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------
    // Barre d'adresse
    // ---------------------------------------------------------------

    static readonly HomelabService Nas = new() { Name = "NAS Synology", Url = "http://nas.lan:5000/" };

    [Fact]
    public void SuggestionsStartWithTheTypedAddressOrSearch()
    {
        IReadOnlyList<Suggestion> address = OmniboxSuggestions.Build("exemple.fr", BrowserSettings.SearchEngine.DuckDuckGo, [], [], []);
        Assert.Equal(SuggestionKind.Address, address[0].Kind);
        Assert.Equal("https://exemple.fr", address[0].Url);

        IReadOnlyList<Suggestion> search = OmniboxSuggestions.Build("recette crêpes", BrowserSettings.SearchEngine.DuckDuckGo, [], [], []);
        Assert.Equal(SuggestionKind.Search, search[0].Kind);
        Assert.Equal("Rechercher « recette crêpes » avec DuckDuckGo", search[0].Title);
        Assert.StartsWith("https://duckduckgo.com/?q=", search[0].Url);

        Assert.Empty(OmniboxSuggestions.Build("   ", BrowserSettings.SearchEngine.Google, [], [], []));
    }

    [Fact]
    public void SuggestionsListServicesThenFavoritesThenMostVisitedPages()
    {
        var favorites = new[] { new FavoriteItem { Title = "NAS — partages", Url = "http://nas.lan:5000/shares" } };
        var history = new List<HistoryEntry>
        {
            new() { Title = "NAS — journal", Url = "http://nas.lan:5000/logs", VisitedAt = DateTime.Now.AddHours(-3) },
            new() { Title = "NAS — stockage", Url = "http://nas.lan:5000/storage", VisitedAt = DateTime.Now.AddHours(-2) },
            new() { Title = "NAS — stockage", Url = "http://nas.lan:5000/storage", VisitedAt = DateTime.Now.AddHours(-1) },
            new() { Title = "NAS — partages", Url = "http://nas.lan:5000/shares", VisitedAt = DateTime.Now }
        };

        IReadOnlyList<Suggestion> suggestions = OmniboxSuggestions.Build("nas", BrowserSettings.SearchEngine.Google, new[] { Nas }, favorites, history);

        Assert.Equal(
            new[] { SuggestionKind.Search, SuggestionKind.Service, SuggestionKind.Favorite, SuggestionKind.History, SuggestionKind.History },
            suggestions.Select(s => s.Kind));
        // Page visitée deux fois avant celle visitée une fois ; le favori n'est pas répété.
        Assert.Equal("http://nas.lan:5000/storage", suggestions[3].Url);
        Assert.Equal("http://nas.lan:5000/logs", suggestions[4].Url);
    }

    [Fact]
    public void SuggestionsMatchEveryWordAndStopAtTheLimit()
    {
        var history = Enumerable.Range(0, 30)
            .Select(i => new HistoryEntry { Title = "Wiki page " + i, Url = "https://wiki.lan/page/" + i, VisitedAt = DateTime.Now.AddMinutes(i) })
            .ToList();

        Assert.Equal(8, OmniboxSuggestions.Build("wiki page", BrowserSettings.SearchEngine.Google, [], [], history).Count);
        Assert.Single(OmniboxSuggestions.Build("wiki absent", BrowserSettings.SearchEngine.Google, [], [], history));
    }

    // ---------------------------------------------------------------
    // Téléchargements
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData(".bashrc", "bashrc")]
    [InlineData("", "telechargement")]
    [InlineData(null, "telechargement")]
    [InlineData("rapport\u0000.pdf", "rapport.pdf")]
    [InlineData("photo.jpg", "photo.jpg")]
    public void DownloadNamesCannotEscapeTheFolderOrHideThemselves(string? suggested, string expected)
        => Assert.Equal(expected, DownloadNames.Sanitize(suggested));

    [Fact]
    public void DownloadsNeverOverwriteAnExistingFile()
    {
        // Aussi utilisé par l'édition Avalonia sous Windows : chemins construits pour le système.
        static string In(string name) => Path.Combine("/d", name);
        var existing = new HashSet<string> { In("notes.txt"), In("notes (1).txt"), In("archive.tar.gz") };
        Func<string, bool> exists = existing.Contains;

        Assert.Equal(In("notes (2).txt"), DownloadNames.UniquePath("/d", "notes.txt", exists));
        Assert.Equal(In("archive (1).tar.gz"), DownloadNames.UniquePath("/d", "archive.tar.gz", exists));
        Assert.Equal(In("neuf.txt"), DownloadNames.UniquePath("/d", "neuf.txt", exists));
    }

    // ---------------------------------------------------------------
    // Emplacements, réglages, session
    // ---------------------------------------------------------------

    [Fact]
    public void UserDirsFileGivesTheLocalizedDownloadFolder()
    {
        string[] lines =
        {
            "# Fichier écrit par xdg-user-dirs-update",
            "XDG_DESKTOP_DIR=\"$HOME/Bureau\"",
            "XDG_DOWNLOAD_DIR=\"$HOME/Téléchargements\"",
            "XDG_TEMPLATES_DIR=\"$HOME/\"",
            "XDG_MUSIC_DIR=\"/srv/musique\""
        };

        Dictionary<string, string> dirs = LinuxPaths.ParseUserDirs(lines, "/home/marie");

        Assert.Equal("/home/marie/Téléchargements", dirs["XDG_DOWNLOAD_DIR"]);
        Assert.Equal("/srv/musique", dirs["XDG_MUSIC_DIR"]);
        Assert.False(dirs.ContainsKey("XDG_TEMPLATES_DIR")); // le dossier personnel lui-même
    }

    [Fact]
    public void LinuxSettingsRoundTripAndAreNormalized()
    {
        string path = Path.Combine(_directory, "settings-linux.json");
        new LinuxSettings { Search = BrowserSettings.SearchEngine.Qwant, ServiceCheckIntervalSeconds = 2, HttpsUpgrade = false, DownloadDirectory = "relatif" }.Save(path);

        LinuxSettings loaded = LinuxSettings.Load(path);
        Assert.Equal(BrowserSettings.SearchEngine.Qwant, loaded.Search);
        Assert.False(loaded.HttpsUpgrade);
        Assert.Equal(15, loaded.ServiceCheckIntervalSeconds);
        Assert.Null(loaded.DownloadDirectory);
        Assert.Contains("\"Qwant\"", File.ReadAllText(path));
    }

    [Fact]
    public void UnreadableLinuxSettingsFallBackToDefaultsAndAreKeptAside()
    {
        string path = Path.Combine(_directory, "settings-linux.json");
        File.WriteAllText(path, "{ pas du json");

        LinuxSettings loaded = LinuxSettings.Load(path);

        Assert.True(loaded.HttpsUpgrade);
        Assert.Equal(BrowserSettings.SearchEngine.DuckDuckGo, loaded.Search);
        Assert.True(File.Exists(path + ".invalide"));
    }

    [Fact]
    public void SessionKeepsOnlyWebPages()
    {
        string path = Path.Combine(_directory, "session.json");
        SessionStore.Save(path, new SessionState
        {
            Tabs =
            {
                new SessionTab { Url = "https://a.fr/", Title = "A" },
                new SessionTab { Url = "about:blank" },
                new SessionTab { Url = "pomme-ruffle://ruffle/ruffle.js" },
                new SessionTab { Url = "http://nas.lan:5000/" }
            },
            Selected = 3
        });

        SessionState loaded = SessionStore.Load(path);

        Assert.Equal(new[] { "https://a.fr/", "http://nas.lan:5000/" }, loaded.Tabs.Select(t => t.Url));
        Assert.Equal(1, loaded.Selected);
        Assert.Empty(SessionStore.Load(Path.Combine(_directory, "absent.json")).Tabs);
    }

    // ---------------------------------------------------------------
    // Favoris et historique
    // ---------------------------------------------------------------

    [Fact]
    public void FavoritesAreSharedWithTheWindowsFormatAndNotDuplicated()
    {
        string path = Path.Combine(_directory, "favorites.json");
        var store = new FavoritesStore(path);
        int changes = 0;
        store.Changed += () => changes++;

        FavoriteItem first = store.Add("Proxmox", "https://pve.lan:8006/", "Serveurs");
        FavoriteItem again = store.Add("Proxmox (bis)", "https://PVE.lan:8006", null);
        store.Add("Wiki", "https://wiki.lan/");

        Assert.Same(first, again);
        Assert.Equal(2, store.All.Count);
        Assert.Equal(new[] { "Serveurs" }, store.Folders);
        Assert.Equal(2, changes);

        store.Update(first.Id, "  PVE  ", "https://pve.lan:8006/", "  ");
        var reloaded = new FavoritesStore(path);
        FavoriteItem pve = reloaded.Find("https://pve.lan:8006/#resume")!;
        Assert.Equal("PVE", pve.Title);
        Assert.Null(pve.Folder);

        Assert.True(reloaded.Remove(pve.Id));
        Assert.Single(new FavoritesStore(path).All);
    }

    [Fact]
    public void UnreadableFavoritesAreNeverOverwritten()
    {
        string path = Path.Combine(_directory, "favorites.json");
        File.WriteAllText(path, "[{ cassé");

        var store = new FavoritesStore(path);
        store.Add("Nouveau", "https://nouveau.fr/");

        Assert.Equal("[{ cassé", File.ReadAllText(path));
    }

    [Fact]
    public void ImportedBookmarksSkipKnownAddresses()
    {
        var store = new FavoritesStore(Path.Combine(_directory, "favorites.json"));
        store.Add("Déjà là", "https://deja.fr/");

        int added = store.Import(new[]
        {
            new ImportedBookmark("Déjà là", "https://deja.fr/", "Barre"),
            new ImportedBookmark("Nouveau", "https://nouveau.fr/", "Barre")
        });

        Assert.Equal(1, added);
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public void HistoryRecordsVisitsOnceAndCompletesTitles()
    {
        string database = Path.Combine(_directory, "history.db");
        using (var history = new HistoryService(database))
        {
            Assert.True(history.IsAvailable);
            history.Record("https://a.fr/", "");
            history.Record("https://a.fr/", "Page A");     // rechargement : même visite, titre complété
            history.Record("https://b.fr/", "Page B");
            history.Record("pomme-ruffle://ruffle/x.js", "ignoré");
            history.UpdateTitle("https://b.fr/", "Page B bis");

            Assert.Equal(new[] { "Page A", "Page B bis" }, history.Recent.Select(h => h.Title));
            history.Flush();
        }

        using (var reopened = new HistoryService(database))
        {
            Assert.Equal(new[] { "https://a.fr/", "https://b.fr/" }, reopened.Recent.Select(h => h.Url));
            reopened.RemoveSince(DateTime.MinValue);
            Assert.Empty(reopened.Recent);
        }
    }

    // ---------------------------------------------------------------
    // Système, Ruffle, import
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian\n", "sudo apt install")]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", "sudo apt install")]
    [InlineData("ID=fedora\n", "sudo dnf install webkitgtk6.0")]
    [InlineData("ID=endeavouros\nID_LIKE=arch\n", "sudo pacman -S")]
    [InlineData("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", "sudo zypper install")]
    [InlineData("", "sudo apt install")]
    public void InstallCommandMatchesTheDistribution(string osRelease, string expected)
        => Assert.StartsWith(expected, SystemRequirements.InstallCommand(osRelease));

    [Fact]
    public void MissingLibrariesMessageExplainsWhatToInstall()
    {
        string message = SystemRequirements.BuildMessage(new[] { "WebKitGTK 6.0 est absent" }, "ID=fedora\n");
        Assert.Contains("• WebKitGTK 6.0 est absent", message);
        Assert.Contains("sudo dnf install webkitgtk6.0", message);
    }

    [Theory]
    [InlineData("ruffle.js", true)]
    [InlineData("core.ruffle.8700c6b0144208de9d1b.js", true)]
    [InlineData("a92f6442b0f55013a937.wasm", true)]
    [InlineData("../ruffle.js", false)]
    [InlineData("sub/ruffle.js", false)]
    [InlineData("..%2Fsettings.json", false)]
    [InlineData(".hidden.js", false)]
    [InlineData("LICENSE_MIT", false)]
    [InlineData("VERSION.txt", false)]
    [InlineData("", false)]
    public void RuffleSchemeOnlyServesRuffleFiles(string name, bool allowed)
        => Assert.Equal(allowed, RuffleAssets.IsAssetName(name));

    [Fact]
    public void LinuxBrowsersAreDetectedForBookmarkImport()
    {
        string home = Path.Combine(_directory, "home");
        string chromium = Path.Combine(home, ".config", "chromium", "Default");
        string firefox = Path.Combine(home, ".mozilla", "firefox", "k3j2.default-release");
        string snapFirefox = Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox", "x1y2.default");
        foreach (string dir in new[] { chromium, firefox, snapFirefox })
            Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(chromium, "Bookmarks"), "{}");
        File.WriteAllText(Path.Combine(firefox, "places.sqlite"), string.Empty);
        File.WriteAllText(Path.Combine(snapFirefox, "places.sqlite"), string.Empty);

        IReadOnlyList<BookmarkSource> sources = BookmarkImporter.DetectLinuxSources(home);

        Assert.Equal(new[] { "Chromium", "Firefox (default-release)", "Firefox (Snap) (default)" }, sources.Select(s => s.Name));
        Assert.Equal(BookmarkSourceKind.ChromiumJson, sources[0].Kind);
        Assert.Equal(BookmarkSourceKind.FirefoxPlaces, sources[1].Kind);
    }
}
