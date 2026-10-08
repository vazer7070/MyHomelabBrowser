using Microsoft.Data.Sqlite;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.History;
using MyHomelabBrowser.classes.Import;
using MyHomelabBrowser.classes.Profiles.Credentials;

namespace PommeBrowser.Tests;

/// <summary>
/// Passage d'un autre navigateur à PommeBrowser : historique (Chromium, Firefox) et mots de passe
/// exportés en CSV (Chrome, Firefox, Safari, Bitwarden).
/// </summary>
public sealed class BrowserImportTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "pomme-import-test-" + Guid.NewGuid().ToString("N")[..8]);

    public BrowserImportTests() => Directory.CreateDirectory(_directory);

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

    static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Microsecondes depuis 1601 (Chromium).</summary>
    static long ChromiumTime(DateTime utc) => utc.ToFileTimeUtc() / 10;

    [Fact]
    public void Chromium_history_is_read_newest_last_without_internal_pages()
    {
        string profile = Path.Combine(_directory, "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "Bookmarks"), "{}");
        string history = Path.Combine(profile, "History");
        DateTime older = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        DateTime newer = new(2026, 9, 15, 18, 30, 0, DateTimeKind.Utc);
        Execute(history, $"""
            CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, typed_count INTEGER, last_visit_time INTEGER, hidden INTEGER);
            INSERT INTO urls (url, title, last_visit_time, hidden) VALUES
              ('https://jeu.exemple.fr/', 'Le jeu', {ChromiumTime(newer)}, 0),
              ('https://ancien.exemple.fr/page', NULL, {ChromiumTime(older)}, 0),
              ('chrome://settings/', 'Paramètres', {ChromiumTime(newer)}, 0),
              ('https://cache.exemple.fr/', 'Caché', {ChromiumTime(newer)}, 1),
              ('https://jamais.exemple.fr/', 'Jamais', 0, 0);
            """);

        var source = new BookmarkSource("Google Chrome", BookmarkSourceKind.ChromiumJson, Path.Combine(profile, "Bookmarks"));
        Assert.Equal(history, HistoryImporter.HistoryPath(source));
        List<HistoryEntry> entries = HistoryImporter.Read(source);

        Assert.Equal(new[] { "https://ancien.exemple.fr/page", "https://jeu.exemple.fr/" }, entries.Select(e => e.Url));
        Assert.Equal("Le jeu", entries[1].Title);
        Assert.Equal(string.Empty, entries[0].Title);
        Assert.Equal(newer.ToLocalTime(), entries[1].VisitedAt);
    }

    [Fact]
    public void Firefox_history_is_read_from_places()
    {
        string places = Path.Combine(_directory, "places.sqlite");
        DateTime visit = new(2026, 8, 2, 9, 15, 0, DateTimeKind.Utc);
        long microseconds = new DateTimeOffset(visit).ToUnixTimeMilliseconds() * 1000;
        Execute(places, $"""
            CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, title TEXT, last_visit_date INTEGER, hidden INTEGER DEFAULT 0, visit_count INTEGER DEFAULT 0);
            INSERT INTO moz_places (url, title, last_visit_date) VALUES
              ('https://forum.exemple.org/sujet', 'Forum', {microseconds}),
              ('place:sort=8', 'Requête', {microseconds}),
              ('https://jamais-visite.exemple.org/', 'Favori seul', NULL);
            """);

        var source = new BookmarkSource("Firefox (default)", BookmarkSourceKind.FirefoxPlaces, places);
        List<HistoryEntry> entries = HistoryImporter.Read(source);

        HistoryEntry single = Assert.Single(entries);
        Assert.Equal("https://forum.exemple.org/sujet", single.Url);
        Assert.Equal(visit.ToLocalTime(), single.VisitedAt);
    }

    [Fact]
    public void Importing_history_twice_adds_nothing_the_second_time()
    {
        using var store = new HistoryStore(Path.Combine(_directory, "history.db"));
        var entries = new[]
        {
            new HistoryEntry { Url = "https://a.exemple/", Title = "A", VisitedAt = new DateTime(2026, 1, 1, 8, 0, 0) },
            new HistoryEntry { Url = "https://b.exemple/", Title = "B", VisitedAt = new DateTime(2026, 1, 2, 8, 0, 0) }
        };

        Assert.Equal(2, store.Import(entries));
        Assert.Equal(0, store.Import(entries));
        Assert.Equal(new[] { "https://a.exemple/", "https://b.exemple/" }, store.LoadRecent(10).Select(e => e.Url));
    }

    [Fact]
    public void A_chrome_export_is_read()
    {
        const string csv = "name,url,username,password,note\r\n" +
                           "jeu.exemple.fr,https://jeu.exemple.fr/connexion,léa,\"mot, de \"\"passe\"\"\",\r\n" +
                           "app,android://abc@com.exemple.app/,léa,secret,\r\n" +
                           "vide,https://vide.exemple.fr/,léa,,\r\n" +
                           "multi,https://multi.exemple.fr/,\"ligne1\nligne2\",p2,\"note\nsur deux lignes\"\r\n";

        (List<ImportedCredential> credentials, int skipped) = PasswordCsvImporter.Parse(csv)!.Value;

        Assert.Equal(2, skipped);
        Assert.Equal(2, credentials.Count);
        Assert.Equal(new ImportedCredential("https://jeu.exemple.fr/connexion", "léa", "mot, de \"passe\"", null), credentials[0]);
        Assert.Equal("ligne1\nligne2", credentials[1].Username);
    }

    [Fact]
    public void Firefox_safari_and_bitwarden_exports_are_read()
    {
        const string firefox = "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\n" +
                               "\"https://forum.exemple.org\",\"moi\",\"p@ss\",,\"https://forum.exemple.org\",\"{x}\",\"1\",\"2\",\"3\"\n";
        Assert.Equal(new ImportedCredential("https://forum.exemple.org", "moi", "p@ss", null), Assert.Single(PasswordCsvImporter.Parse(firefox)!.Value.Credentials));

        const string safari = "﻿Title,URL,Username,Password,Notes,OTPAuth\nBanque,https://banque.exemple.fr/,client,code,,otpauth://totp/x?secret=JBSWY3DPEHPK3PXP\n";
        Assert.Equal("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP", Assert.Single(PasswordCsvImporter.Parse(safari)!.Value.Credentials).Totp);

        const string bitwarden = "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
                                 ",,login,Mail,,,0,https://mail.exemple.fr,moi@exemple.fr,s3cret,\n";
        Assert.Equal(new ImportedCredential("https://mail.exemple.fr", "moi@exemple.fr", "s3cret", null), Assert.Single(PasswordCsvImporter.Parse(bitwarden)!.Value.Credentials));
    }

    [Theory]
    [InlineData("")]
    [InlineData("titre,auteur\nLivre,Moi\n")]
    [InlineData("url,username\nhttps://a.exemple,moi\n")]
    public void Other_files_are_not_taken_for_password_exports(string csv)
        => Assert.Null(PasswordCsvImporter.Parse(csv));

    [Fact]
    public void Imported_passwords_never_replace_those_already_in_the_vault()
    {
        var vault = new CredentialVaultService(() => Path.Combine(_directory, "vault.json.enc"));
        Assert.True(vault.TryInitializeNewVault("Pâques@2026#€"));
        vault.Upsert("https://jeu.exemple.fr", "léa", "récent", formAction: null);

        (int added, int existing, int invalid) = vault.Import(new (string, string, string, string?)[]
        {
            ("https://jeu.exemple.fr/connexion", "léa", "ancien", null),
            ("https://forum.exemple.org/", "moi", "p@ss", "JBSWY3DPEHPK3PXP"),
            ("pas une adresse", "moi", "x", null)
        });

        Assert.Equal((1, 1, 1), (added, existing, invalid));
        Assert.Equal("récent", vault.FindForOrigin("https://jeu.exemple.fr", "léa")!.Password);
        CredentialEntry forum = vault.FindForOrigin("https://forum.exemple.org", "moi")!;
        Assert.Equal("p@ss", forum.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", forum.TotpSecret);

        // Enregistré : relu après verrouillage.
        vault.Lock();
        Assert.True(vault.TryUnlock("Pâques@2026#€"));
        Assert.NotNull(vault.FindForOrigin("https://forum.exemple.org", "moi"));
    }
}
