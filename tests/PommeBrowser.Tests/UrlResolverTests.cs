using MyHomelabBrowser.classes;

namespace PommeBrowser.Tests;

public class UrlResolverTests
{
    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("github.com/vazer7070", "https://github.com/vazer7070")]
    [InlineData("about:blank", "about:blank")]
    public void Public_addresses_open_in_https(string input, string expected)
        => Assert.Equal(expected, UrlResolver.TryResolveUrl(input)?.TrimEnd('/'));

    [Theory]
    [InlineData("nas:5000", "http://nas:5000")]
    [InlineData("192.168.1.10", "http://192.168.1.10")]
    [InlineData("192.168.1.10:8080", "http://192.168.1.10:8080")]
    [InlineData("jellyfin.lan", "http://jellyfin.lan")]
    [InlineData("pihole.home.arpa/admin", "http://pihole.home.arpa/admin")]
    [InlineData("localhost:3000", "http://localhost:3000")]
    public void Local_addresses_open_in_http(string input, string expected)
        => Assert.Equal(expected, UrlResolver.TryResolveUrl(input)?.TrimEnd('/'));

    [Theory]
    [InlineData("pve.lan:8006", "https://pve.lan:8006")]
    [InlineData("192.168.1.2:5001", "https://192.168.1.2:5001")]
    [InlineData("10.0.0.5:9443", "https://10.0.0.5:9443")]
    public void Local_admin_ports_open_in_https(string input, string expected)
        => Assert.Equal(expected, UrlResolver.TryResolveUrl(input)?.TrimEnd('/'));

    [Theory]
    [InlineData("comment configurer proxmox")]
    [InlineData("nas")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fichier.txt2")]
    public void Plain_words_are_searches(string input)
        => Assert.Null(UrlResolver.TryResolveUrl(input));

    [Fact]
    public void Search_uses_the_chosen_engine_and_escapes_the_query()
    {
        string url = UrlResolver.ResolveOrSearch("docker & compose", BrowserSettings.SearchEngine.DuckDuckGo);
        Assert.Equal("https://duckduckgo.com/?q=docker%20%26%20compose", url);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.100.1.1", true)] // Tailscale (CGNAT)
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("nas.local", true)]
    [InlineData("example.com", false)]
    public void Local_host_detection(string host, bool expected)
        => Assert.Equal(expected, UrlResolver.IsLocalHost(host));

    [Theory]
    [InlineData("na62.evony.com/s.html?loginid=74&adv=index", true)]
    [InlineData("exemple.fr", true)]
    [InlineData("https://exemple.fr", false)]   // https tapé exprès : pas de retour en HTTP
    [InlineData("http://exemple.fr", false)]    // déjà en HTTP (le passage en HTTPS a son propre suivi)
    [InlineData("exemple.fr:8443/admin", false)] // port explicite
    [InlineData("nas:5000", false)]             // réseau local, ouvert en HTTP
    [InlineData("chat noir", false)]            // recherche
    public void An_address_typed_without_scheme_may_fall_back_to_http(string input, bool expected)
        => Assert.Equal(expected, UrlResolver.IsImplicitHttps(input));
}
