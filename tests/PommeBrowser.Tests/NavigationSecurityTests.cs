using MyHomelabBrowser.classes.Security;

namespace PommeBrowser.Tests;

public sealed class HttpsUpgradePolicyTests
{
    static bool Upgrades(string url, params string[] allowedOverHttp)
        => HttpsUpgradePolicy.ShouldUpgrade(new Uri(url), host => allowedOverHttp.Contains(host));

    [Theory]
    [InlineData("http://exemple.fr/page?x=1")]
    [InlineData("http://www.exemple.com/")]
    [InlineData("http://jeux.exemple.fr:80/")]
    public void Public_http_sites_are_tried_in_https(string url) => Assert.True(Upgrades(url));

    [Theory]
    [InlineData("https://exemple.fr/")]
    [InlineData("http://192.168.1.20/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://203.0.113.9/")]
    [InlineData("http://[2001:db8::1]/")]
    [InlineData("http://localhost/")]
    [InlineData("http://nas/")]
    [InlineData("http://pve.lan/")]
    [InlineData("http://imprimante.local/")]
    [InlineData("http://routeur.home.arpa/")]
    [InlineData("http://exemple.fr:8080/")]
    [InlineData("ftp://exemple.fr/")]
    public void Local_network_ip_addresses_and_explicit_ports_stay_as_typed(string url) => Assert.False(Upgrades(url));

    [Fact]
    public void Site_allowed_over_http_is_not_upgraded()
        => Assert.False(Upgrades("http://ancien-site.fr/", "ancien-site.fr"));

    [Fact]
    public void Upgrade_keeps_path_and_query()
        => Assert.Equal("https://exemple.fr/a/b?c=1#d",
            HttpsUpgradePolicy.Upgrade(new Uri("http://exemple.fr:80/a/b?c=1#d")).AbsoluteUri);
}

/// <summary>Site passé en HTTPS qui repart de lui-même en HTTP (koramgame.com) : plus de boucle.</summary>
public sealed class HttpsReturnGuardTests
{
    [Fact]
    public void A_site_that_goes_back_to_http_by_itself_right_after_opening_in_https_is_left_in_http()
    {
        var guard = new HttpsReturnGuard();
        Assert.False(guard.UpgradedPageOpened("www.koramgame.com", 1_000));

        // Autre site, ou page déjà en HTTPS : rien.
        Assert.False(guard.ReturnsToHttp(new Uri("http://autre.fr/"), 1_500));
        Assert.False(guard.ReturnsToHttp(new Uri("https://www.koramgame.com/"), 1_500));
        // Retour en http:// du même site peu après : il veut HTTP (une fois).
        Assert.True(guard.ReturnsToHttp(new Uri("http://www.koramgame.com/index.html"), 1_800));
        Assert.False(guard.ReturnsToHttp(new Uri("http://www.koramgame.com/"), 1_900));

        // Bien plus tard (lien suivi par l'utilisateur) : pas un retour de la page.
        guard.UpgradedPageOpened("exemple.fr", 10_000);
        Assert.False(guard.ReturnsToHttp(new Uri("http://exemple.fr/"), 10_000 + HttpsReturnGuard.ReturnDelay));
    }

    [Fact]
    public void The_same_site_opened_in_https_again_and_again_is_a_loop()
    {
        var guard = new HttpsReturnGuard();
        Assert.False(guard.UpgradedPageOpened("www.koramgame.com", 0));
        Assert.False(guard.UpgradedPageOpened("www.koramgame.com", 400));
        Assert.True(guard.UpgradedPageOpened("www.koramgame.com", 800));

        // Ouvertures espacées, ou de sites différents : pas une boucle.
        var other = new HttpsReturnGuard();
        Assert.False(other.UpgradedPageOpened("a.fr", 0));
        Assert.False(other.UpgradedPageOpened("b.fr", 100));
        Assert.False(other.UpgradedPageOpened("a.fr", 200));
        Assert.False(other.UpgradedPageOpened("a.fr", 200 + HttpsReturnGuard.LoopPeriod + 1));
        Assert.False(other.UpgradedPageOpened("a.fr", 300 + HttpsReturnGuard.LoopPeriod + 1));
    }
}

public sealed class SiteSecurityStoreTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), "pomme-sites-" + Guid.NewGuid().ToString("N"), "site-permissions.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch { }
    }

    [Fact]
    public void Decisions_are_remembered_per_site_and_kind()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var store = new SiteSecurityStore(() => _path);
        store.Set("https://visio.exemple.fr", "Camera", true);
        store.Set("https://visio.exemple.fr", "Geolocation", false);
        store.Set("ancien-site.fr", SiteSecurityStore.InsecureHttp, true);

        var reloaded = new SiteSecurityStore(() => _path);
        Assert.True(reloaded.Get("https://visio.exemple.fr", "Camera"));
        Assert.False(reloaded.Get("https://VISIO.exemple.fr", "Geolocation"));
        Assert.Null(reloaded.Get("https://visio.exemple.fr", "Microphone"));
        Assert.True(reloaded.IsHttpAllowed("ANCIEN-SITE.fr"));
        Assert.False(reloaded.IsHttpAllowed("autre.fr"));
    }

    [Fact]
    public void Changing_a_decision_replaces_it_and_removal_forgets_it()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var store = new SiteSecurityStore(() => _path);
        store.Set("https://a.fr", "Camera", true);
        store.Set("https://a.fr", "Camera", false);

        SiteDecision decision = Assert.Single(store.All);
        Assert.False(decision.Allowed);

        store.Remove(decision);
        Assert.Empty(new SiteSecurityStore(() => _path).All);

        store.Set("https://b.fr", "Notifications", true);
        store.Clear();
        Assert.Empty(new SiteSecurityStore(() => _path).All);
    }
}
