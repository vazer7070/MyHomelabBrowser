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
