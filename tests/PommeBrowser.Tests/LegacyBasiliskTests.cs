using MyHomelabBrowser.classes.Flash;

namespace PommeBrowser.Tests;

public sealed class LegacyCommandLineTests
{
    [Theory]
    [InlineData(new[] { "-profile", @"C:\Users\Moi\Profil Basilisk" }, @"C:\B\basilisk.exe -profile ""C:\Users\Moi\Profil Basilisk""")]
    [InlineData(new[] { @"C:\Dossier\" }, @"C:\B\basilisk.exe C:\Dossier\")]
    [InlineData(new[] { @"C:\Dossier avec espace\" }, @"C:\B\basilisk.exe ""C:\Dossier avec espace\\""")]
    [InlineData(new[] { "" }, @"C:\B\basilisk.exe """"")]
    [InlineData(new[] { "a\"b" }, @"C:\B\basilisk.exe ""a\""b""")]
    public void Arguments_are_quoted_per_windows_rules(string[] arguments, string expected)
        => Assert.Equal(expected, WindowsCommandLine.Build(@"C:\B\basilisk.exe", arguments));

    [Fact]
    public void A_crafted_address_stays_a_single_argument()
    {
        // Une adresse contenant guillemet et option ne doit pas devenir un deuxième argument.
        string commandLine = WindowsCommandLine.Build("basilisk.exe", new[] { "https://x/\" -profile \"C:\\autre" });

        Assert.Equal("basilisk.exe \"https://x/\\\" -profile \\\"C:\\autre\"", commandLine);
    }

    [Fact]
    public void Environment_block_adds_variables_and_ends_with_two_nulls()
    {
        string block = LegacyProcess.BuildEnvironmentBlock(new Dictionary<string, string> { ["MOZ_CRASHREPORTER_DISABLE"] = "1" });

        Assert.Contains("MOZ_CRASHREPORTER_DISABLE=1\0", block);
        Assert.EndsWith("\0\0", block);
    }
}

public sealed class LegacyProfileTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "pomme-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyProfileTests() => LegacyProfileManager.RootOverride = _root;

    public void Dispose()
    {
        LegacyProfileManager.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Second_tab_on_the_same_site_gets_its_own_profile()
    {
        using LegacyProfileLease first = LegacyProfileManager.CreateLease("jeux.exemple.fr", isPrivate: false);
        using LegacyProfileLease second = LegacyProfileManager.CreateLease("jeux.exemple.fr", isPrivate: false);

        Assert.EndsWith("jeux.exemple.fr", first.ProfilePath);
        Assert.EndsWith("jeux.exemple.fr~2", second.ProfilePath);
    }

    [Fact]
    public void Released_profile_is_reused()
    {
        string path;
        using (LegacyProfileLease lease = LegacyProfileManager.CreateLease("reutilise.fr", isPrivate: false))
            path = lease.ProfilePath;

        using LegacyProfileLease again = LegacyProfileManager.CreateLease("reutilise.fr", isPrivate: false);
        Assert.Equal(path, again.ProfilePath);
    }

    [Fact]
    public void Profile_locked_by_another_basilisk_is_skipped()
    {
        string busy = Path.Combine(_root, "profiles", "verrou.fr");
        Directory.CreateDirectory(busy);
        using (new FileStream(Path.Combine(busy, "parent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using LegacyProfileLease lease = LegacyProfileManager.CreateLease("verrou.fr", isPrivate: false);
            Assert.EndsWith("verrou.fr~2", lease.ProfilePath);
        }

        // Lock libéré (Basilisk fermé) : le profil principal redevient utilisable.
        using LegacyProfileLease free = LegacyProfileManager.CreateLease("verrou.fr", isPrivate: false);
        Assert.Equal(busy, free.ProfilePath);
    }

    [Fact]
    public void Too_many_tabs_on_one_site_are_refused()
    {
        var leases = Enumerable.Range(0, LegacyProfileManager.MaxSlotsPerSite)
            .Select(_ => LegacyProfileManager.CreateLease("plein.fr", isPrivate: false))
            .ToList();
        try
        {
            Assert.Throws<InvalidOperationException>(() => LegacyProfileManager.CreateLease("plein.fr", isPrivate: false));
        }
        finally
        {
            leases.ForEach(l => l.Dispose());
        }
    }

    [Fact]
    public void Private_profiles_are_unique_and_deleted_on_release()
    {
        LegacyProfileLease a = LegacyProfileManager.CreateLease("prive.fr", isPrivate: true);
        using LegacyProfileLease b = LegacyProfileManager.CreateLease("prive.fr", isPrivate: true);

        Assert.NotEqual(a.ProfilePath, b.ProfilePath);
        a.Dispose();

        SpinWait.SpinUntil(() => !Directory.Exists(a.ProfilePath), TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(a.ProfilePath));
    }

    [Fact]
    public void Managed_user_js_is_rewritten_with_hardened_settings()
    {
        string profile = Path.Combine(_root, "durci");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "user.js"), "user_pref(\"security.tls.version.min\", 1);\n");

        LegacyProfilePreferences.Apply(profile, isPrivate: false);

        string userJs = File.ReadAllText(Path.Combine(profile, "user.js"));
        Assert.Contains("user_pref(\"security.tls.version.min\", 3);", userJs);
        Assert.DoesNotContain("\"security.tls.version.min\", 1", userJs);
        Assert.Contains("user_pref(\"media.peerconnection.enabled\", false);", userJs);
        Assert.Contains("user_pref(\"browser.sessionstore.resume_from_crash\", false);", userJs);
        Assert.Contains("user_pref(\"plugin.default.state\", 0);", userJs);
        Assert.Contains("user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");", userJs);
        Assert.DoesNotContain("privatebrowsing", userJs);
        Assert.True(File.Exists(Path.Combine(profile, "chrome", "userChrome.css")));

        Assert.Contains("browser.privatebrowsing.autostart\", true", LegacyProfilePreferences.BuildUserJs(isPrivate: true));
    }
}
