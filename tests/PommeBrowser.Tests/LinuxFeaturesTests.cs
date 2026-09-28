using System.Text.Json;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Localization;
using MyHomelabBrowser.classes.Profiles.Credentials;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Tests;

/// <summary>
/// Édition Linux : mise à jour de l'AppImage, Basilisk, rapport de support et scripts du coffre
/// (communs avec l'édition Windows).
/// </summary>
[Collection(nameof(LocalizationCollection))]
public sealed class LinuxFeaturesTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "pomme-features-" + Guid.NewGuid().ToString("N"));

    public LinuxFeaturesTests()
    {
        Directory.CreateDirectory(_directory);
        Loc.Initialize("fr", null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------
    // Mise à jour de l'AppImage
    // ---------------------------------------------------------------

    static string Release(string tag, bool prerelease = false, params (string Name, string Url, long Size)[] assets)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease,
            html_url = "https://github.com/vazer7070/PommeBrowser-release/releases/tag/" + tag,
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url, size = a.Size })
        });

    [Theory]
    [InlineData("0.9.9", true)]
    [InlineData("v1.0", true)]
    [InlineData("0.9.8", false)]
    [InlineData("0.9.7", false)]
    public void OnlyANewerVersionIsOffered(string tag, bool newer)
    {
        Assert.True(AppImageUpdate.TryParseVersion(tag, out Version version));
        Assert.Equal(newer, AppImageUpdate.IsNewer(version, new Version(0, 9, 8, 0)));
    }

    [Theory]
    [InlineData("0.9.9-beta.1")]
    [InlineData("nightly")]
    [InlineData("")]
    public void PreReleasesAndOddTagsAreIgnored(string tag)
        => Assert.False(AppImageUpdate.TryParseVersion(tag, out _));

    [Fact]
    public void TheAppImageOfThisArchitectureAndItsChecksumAreFound()
    {
        ReleaseInfo release = AppImageUpdate.ParseRelease(Release("0.9.9", false,
            ("PommeBrowser-0.9.9-x86_64.AppImage", "https://github.com/x/PommeBrowser-0.9.9-x86_64.AppImage", 90_000_000),
            ("PommeBrowser-0.9.9-x86_64.AppImage.sha256", "https://github.com/x/PommeBrowser-0.9.9-x86_64.AppImage.sha256", 100),
            ("PommeBrowser-0.9.9-full.nupkg", "https://github.com/x/full.nupkg", 80_000_000)))!;

        var assets = AppImageUpdate.FindAssets(release, "x86_64");
        Assert.NotNull(assets);
        Assert.Equal("PommeBrowser-0.9.9-x86_64.AppImage", assets.Value.Image.Name);
        Assert.Null(AppImageUpdate.FindAssets(release, "aarch64"));
    }

    [Fact]
    public void InsecureOrOversizedDownloadsAreRefused()
    {
        ReleaseInfo release = AppImageUpdate.ParseRelease(Release("0.9.9", false,
            ("PommeBrowser-0.9.9-x86_64.AppImage", "http://exemple.fr/PommeBrowser.AppImage", 90_000_000),
            ("PommeBrowser-0.9.9-x86_64.AppImage.sha256", "https://exemple.fr/sum", 100),
            ("PommeBrowser-0.9.9-aarch64.AppImage", "https://exemple.fr/arm.AppImage", AppImageUpdate.MaxSize + 1),
            ("PommeBrowser-0.9.9-aarch64.AppImage.sha256", "https://exemple.fr/arm.sha256", 100)))!;

        // Adresse en http:// écartée à la lecture ; fichier trop gros écarté au choix.
        Assert.Null(AppImageUpdate.FindAssets(release, "x86_64"));
        Assert.Null(AppImageUpdate.FindAssets(release, "aarch64"));
    }

    [Fact]
    public void DraftsAndPreReleasesAreNotUpdates()
    {
        Assert.Null(AppImageUpdate.ParseRelease(Release("0.9.9", prerelease: true)));
        Assert.Null(AppImageUpdate.ParseRelease("{\"tag_name\":\"1.0.0\",\"draft\":true}"));
        Assert.Null(AppImageUpdate.ParseRelease("pas du JSON"));
    }

    [Fact]
    public void TheChecksumMustNameTheDownloadedFile()
    {
        string hash = new string('a', 64);
        Assert.Equal(hash, AppImageUpdate.ParseChecksum(hash + "  PommeBrowser-0.9.9-x86_64.AppImage\n", "PommeBrowser-0.9.9-x86_64.AppImage"));
        Assert.Equal(hash, AppImageUpdate.ParseChecksum(hash.ToUpperInvariant() + " *PommeBrowser-0.9.9-x86_64.AppImage", "PommeBrowser-0.9.9-x86_64.AppImage"));
        Assert.Null(AppImageUpdate.ParseChecksum(hash + "  autre.AppImage", "PommeBrowser-0.9.9-x86_64.AppImage"));
        Assert.Null(AppImageUpdate.ParseChecksum("zz  PommeBrowser-0.9.9-x86_64.AppImage", "PommeBrowser-0.9.9-x86_64.AppImage"));
    }

    [Fact]
    public void OnlyType2AppImagesAreAccepted()
    {
        byte[] appImage = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, (byte)'A', (byte)'I', 2, 0 };
        Assert.True(AppImageUpdate.LooksLikeAppImage(appImage));

        byte[] elf = (byte[])appImage.Clone();
        elf[8] = 0;
        Assert.False(AppImageUpdate.LooksLikeAppImage(elf));
        Assert.False(AppImageUpdate.LooksLikeAppImage("<html>"u8));
    }

    [Fact]
    public void AReadOnlyFolderCannotReceiveTheUpdate()
    {
        Assert.False(AppImageUpdate.CanReplace(null));
        Assert.False(AppImageUpdate.CanReplace(Path.Combine(_directory, "absente.AppImage")));

        string image = Path.Combine(_directory, "PommeBrowser.AppImage");
        File.WriteAllText(image, "x");
        Assert.True(AppImageUpdate.CanReplace(image));
    }

    // ---------------------------------------------------------------
    // Basilisk
    // ---------------------------------------------------------------

    [LinuxFact]
    public void BasiliskIsFoundInTheUsualPlacesThenInThePath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string home = Path.Combine(_directory, "home");
        string bin = Path.Combine(_directory, "bin");
        Directory.CreateDirectory(bin);
        string inPath = Path.Combine(bin, "basilisk");
        File.WriteAllText(inPath, "#!/bin/sh\n");
        File.SetUnixFileMode(inPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal(inPath, BasiliskInstall.Detect(home, "relative/dir:" + bin));

        string local = Path.Combine(home, ".local", "share", "basilisk", "basilisk");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "#!/bin/sh\n");
        Assert.Equal(inPath, BasiliskInstall.Detect(home, bin)); // pas exécutable : ignoré

        File.SetUnixFileMode(local, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Equal(local, BasiliskInstall.Detect(home, bin));
    }

    [Fact]
    public void NameAndVersionComeFromApplicationIni()
    {
        var (name, version) = BasiliskInstall.ParseApplicationIni(new[]
        {
            "[Gecko]", "Name=Gecko", "[App]", "Vendor=Moonchild Productions", "Name=Basilisk", "Version=2025.02.20"
        });

        Assert.Equal("Basilisk", name);
        Assert.Equal("2025.02.20", version);
    }

    [Fact]
    public void BasiliskGetsItsOwnProfileAndNoCrashReporter()
    {
        IReadOnlyList<string> arguments = BasiliskInstall.Arguments("/p/site", new Uri("https://jeux.exemple.fr/a b"));
        Assert.Equal(new[] { "-new-instance", "-no-remote", "-profile", "/p/site", "https://jeux.exemple.fr/a%20b" }, arguments);

        IReadOnlyDictionary<string, string> environment = BasiliskInstall.Environment("/pomme/plugins", "/usr/lib/mozilla/plugins");
        Assert.Equal("1", environment["MOZ_CRASHREPORTER_DISABLE"]);
        Assert.Equal("/pomme/plugins:/usr/lib/mozilla/plugins", environment["MOZ_PLUGIN_PATH"]);
        Assert.Equal("x11", environment["GDK_BACKEND"]);
    }

    [Theory]
    [InlineData("https://jeux.exemple.fr/", true)]
    [InlineData("http://nas.lan:8080/flash", true)]
    [InlineData("file:///home/moi/jeu.swf", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("pas une adresse", false)]
    public void OnlyWebPagesOpenInBasilisk(string url, bool expected)
        => Assert.Equal(expected, BasiliskInstall.IsOpenable(url, out _));

    [Fact]
    public void TheFlashPluginIsLookedForInPommeBrowsersFolderFirst()
    {
        string home = Path.Combine(_directory, "home");
        string pomme = Path.Combine(_directory, "plugins");
        string mozilla = Path.Combine(home, ".mozilla", "plugins");
        Directory.CreateDirectory(pomme);
        Directory.CreateDirectory(mozilla);
        Assert.Null(BasiliskInstall.FindFlashPlugin(home, pomme));

        File.WriteAllText(Path.Combine(mozilla, BasiliskInstall.FlashPluginFile), "");
        Assert.Equal(Path.Combine(mozilla, BasiliskInstall.FlashPluginFile), BasiliskInstall.FindFlashPlugin(home, pomme));

        File.WriteAllText(Path.Combine(pomme, BasiliskInstall.FlashPluginFile), "");
        Assert.Equal(Path.Combine(pomme, BasiliskInstall.FlashPluginFile), BasiliskInstall.FindFlashPlugin(home, pomme));
    }

    [LinuxFact]
    public void ALinuxProfileIsLockedOnlyWhileItsBasiliskRuns()
    {
        string profile = Path.Combine(_directory, "profil");
        Directory.CreateDirectory(profile);
        Assert.False(LegacyProfileManager.IsLockedOnLinux(profile));

        string lockLink = Path.Combine(profile, "lock");
        File.CreateSymbolicLink(lockLink, "127.0.0.1:+" + Environment.ProcessId);
        Assert.True(LegacyProfileManager.IsLockedOnLinux(profile));

        // Basilisk arrêté brutalement : le lien reste, mais le processus n'existe plus.
        File.Delete(lockLink);
        File.CreateSymbolicLink(lockLink, "127.0.0.1:+" + int.MaxValue);
        Assert.False(LegacyProfileManager.IsLockedOnLinux(profile));
    }

    [Fact]
    public void InASeparateWindowBasiliskKeepsItsToolbars()
    {
        string profile = Path.Combine(_directory, "fenetre");
        LegacyProfilePreferences.Apply(profile, isPrivate: false, embedded: false);
        string chrome = File.ReadAllText(Path.Combine(profile, "chrome", "userChrome.css"));
        Assert.DoesNotContain("collapse", chrome);

        LegacyProfilePreferences.Apply(profile, isPrivate: false);
        Assert.Contains("collapse", File.ReadAllText(Path.Combine(profile, "chrome", "userChrome.css")));
    }

    // ---------------------------------------------------------------
    // Signaler un problème
    // ---------------------------------------------------------------

    [Fact]
    public void ReportIdsFollowTheWindowsFormat()
        => Assert.Matches(@"^PB-20260928-[0-9A-F]{4}$", SupportReport.NewId(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void TheReportedAddressHasNoCredentialsNorParameters()
        => Assert.Equal("https://nas.lan:5001/login", SupportReport.SanitizeUrl("https://moi:secret@nas.lan:5001/login?token=abc#x"));

    [Fact]
    public void TitleAndDescriptionAreRequiredAndLimited()
    {
        Assert.NotNull(SupportReport.Validate("", "Description"));
        Assert.NotNull(SupportReport.Validate("Titre", "  "));
        Assert.NotNull(SupportReport.Validate("Titre", new string('x', SupportReport.MaxDescriptionLength + 1)));
        Assert.Null(SupportReport.Validate("Titre", "Description"));
    }

    [Fact]
    public void CategoriesMatchTheSupportServer()
        => Assert.Equal(new[] { "bug", "missing_feature", "feature_request", "ui_ux", "performance", "other" },
            SupportReport.Categories.Select(c => c.Key));

    // ---------------------------------------------------------------
    // Coffre : scripts communs aux deux éditions
    // ---------------------------------------------------------------

    [Fact]
    public void TheCaptureScriptPostsThroughTheGivenChannel()
    {
        string webkit = CredentialScripts.Capture(CredentialScripts.WebKitPost);
        Assert.Contains("messageHandlers?.pommeCredentials", webkit);
        Assert.DoesNotContain("/*POST*/", webkit);
        Assert.Contains("chrome?.webview?.postMessage", CredentialScripts.Capture(CredentialScripts.WebView2Post));
    }

    [Fact]
    public void FillValuesAreEncodedAsJavaScriptStrings()
    {
        string script = CredentialScripts.Fill("moi\"</script>", "p'a\\ss", "123456", "https://nas.lan");

        Assert.Contains("\"moi\\u0022\\u003C/script\\u003E\"", script);
        Assert.Contains("const expectedOrigin = \"https://nas.lan\";", script);
        Assert.Contains("let otp = \"123456\";", script);
        Assert.Contains("let otp = null;", CredentialScripts.Fill("a", "b"));
    }

    [Fact]
    public void ASubmissionIsAcceptedOnlyFromThePagesOrigin()
    {
        const string json = "{\"type\":\"cred_submit\",\"origin\":\"https://nas.lan\",\"username\":\" admin \",\"password\":\"s3cret\",\"formAction\":\"https://nas.lan/login\"}";

        Assert.True(CredentialScripts.TryParseSubmission(json, "https://nas.lan", out CredentialCandidate? candidate));
        Assert.Equal("https://nas.lan", candidate!.Origin);
        Assert.Equal("admin", candidate.Username);

        // Message arrivé après un changement de page : origine déclarée différente.
        Assert.False(CredentialScripts.TryParseSubmission(json, "https://autre.fr", out _));
        Assert.False(CredentialScripts.TryParseSubmission("{\"type\":\"autre\",\"password\":\"x\"}", "https://nas.lan", out _));
        Assert.False(CredentialScripts.TryParseSubmission("{\"type\":\"cred_submit\",\"password\":\"\"}", "https://nas.lan", out _));
        Assert.False(CredentialScripts.TryParseSubmission("pas du JSON", "https://nas.lan", out _));
    }
}
