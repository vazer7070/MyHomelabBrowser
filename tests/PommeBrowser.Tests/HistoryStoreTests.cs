using System.Text.Json;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.History;

namespace PommeBrowser.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "pomme-history-" + Guid.NewGuid().ToString("N"));
    string DatabasePath => Path.Combine(_folder, "history.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    static HistoryEntry Visit(string url, int minutesAgo, string title = "")
        => new() { Url = url, Title = title, VisitedAt = DateTime.Now.AddMinutes(-minutesAgo) };

    [Fact]
    public void Visits_are_reloaded_in_chronological_order()
    {
        using (var store = new HistoryStore(DatabasePath))
        {
            store.Add(Visit("https://b.fr/", 5));
            store.Add(Visit("https://a.fr/", 10));
            store.Add(Visit("https://c.fr/", 1));
        }

        using var reopened = new HistoryStore(DatabasePath);
        Assert.Equal(new[] { "https://a.fr/", "https://b.fr/", "https://c.fr/" }, reopened.LoadRecent(10).Select(h => h.Url));
        Assert.Equal(new[] { "https://b.fr/", "https://c.fr/" }, reopened.LoadRecent(2).Select(h => h.Url));
    }

    [Fact]
    public void Title_found_after_the_visit_is_saved()
    {
        var visit = Visit("https://exemple.fr/", 0, "exemple.fr");
        using (var store = new HistoryStore(DatabasePath))
        {
            store.Add(visit);
            visit.Title = "Exemple — Accueil";
            store.UpdateTitle(visit);
        }

        using var reopened = new HistoryStore(DatabasePath);
        Assert.Equal("Exemple — Accueil", Assert.Single(reopened.LoadRecent(10)).Title);
    }

    [Fact]
    public void Removed_and_cleared_visits_disappear()
    {
        var keep = Visit("https://garder.fr/", 120);
        var drop = Visit("https://supprimer.fr/", 60);
        var recent = Visit("https://recent.fr/", 1);

        using (var store = new HistoryStore(DatabasePath))
        {
            store.Add(keep);
            store.Add(drop);
            store.Add(recent);
            store.Remove(new[] { drop });
            store.RemoveSince(DateTime.Now.AddMinutes(-30));
        }

        using (var reopened = new HistoryStore(DatabasePath))
        {
            Assert.Equal("https://garder.fr/", Assert.Single(reopened.LoadRecent(10)).Url);
            reopened.Clear();
        }

        using var cleared = new HistoryStore(DatabasePath);
        Assert.Equal(0, cleared.Count());
    }

    [Fact]
    public void Old_json_history_is_imported_once_and_kept_as_backup()
    {
        Directory.CreateDirectory(_folder);
        string json = Path.Combine(_folder, "history.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new[]
        {
            Visit("https://ancien.fr/", 30, "Ancien"),
            Visit("", 20),
            Visit("https://recent.fr/", 10, "Récent")
        }));

        using var store = new HistoryStore(DatabasePath);
        Assert.Equal(2, store.ImportLegacyJson(json));
        Assert.False(File.Exists(json));
        Assert.True(File.Exists(json + ".bak"));
        Assert.Equal(new[] { "Ancien", "Récent" }, store.LoadRecent(10).Select(h => h.Title));

        // Un second history.json (ancienne version relancée) n'écrase pas la base.
        File.WriteAllText(json, "[]");
        Assert.Equal(0, store.ImportLegacyJson(json));
    }

    [Fact]
    public void Trim_keeps_the_most_recent_visits()
    {
        using var store = new HistoryStore(DatabasePath);
        for (int i = 0; i < 20; i++)
            store.Add(Visit($"https://site{i}.fr/", 100 - i));
        store.Flush();

        store.Trim(maxEntries: 5);

        Assert.Equal(Enumerable.Range(15, 5).Select(i => $"https://site{i}.fr/"), store.LoadRecent(50).Select(h => h.Url));
    }
}
