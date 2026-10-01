using PommeBrowser.Engine;

namespace PommeBrowser.Tests;

/// <summary>Cookies partagés entre la page et le moteur Flash intégré : périmètre et règles du RFC 6265.</summary>
public sealed class FlashCookiesTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly Uri Game = new("http://play13.ministryofwar.com/Source/XMLSource/text.cfg");

    static PageCookie? Parse(string header, Uri? url = null, bool fromHttp = true)
        => FlashCookies.Parse(url ?? Game, header, fromHttp, Now);

    [Theory]
    [InlineData("http://play13.ministryofwar.com/", "http://play13.ministryofwar.com/version.cfg", true)]
    [InlineData("http://play13.ministryofwar.com/", "https://api.ministryofwar.com/login", true)]
    [InlineData("http://www.jeu.fr/", "http://cdn.jeu.fr/a.swf", true)]
    [InlineData("http://play13.ministryofwar.com/", "http://autre-site.com/", false)]
    [InlineData("http://jeu.example.co.uk/", "http://banque.co.uk/", false)]
    [InlineData("http://jeu.example.co.uk/", "http://cdn.example.co.uk/", true)]
    [InlineData("file:///C:/jeux/page.html", "file:///C:/jeux/a.swf", false)]
    [InlineData("http://play13.ministryofwar.com/", "ftp://play13.ministryofwar.com/", false)]
    public void Only_the_site_of_the_page_shares_its_cookies(string page, string url, bool shared)
        => Assert.Equal(shared, FlashCookies.IsShared(new Uri(page), new Uri(url)));

    [Fact]
    public void The_cookie_header_joins_names_and_values()
    {
        Assert.Equal("session=abc; prefs=fr", FlashCookies.Header(new[] { ("session", "abc"), ("prefs", "fr") }));
        Assert.Equal(string.Empty, FlashCookies.Header(Array.Empty<(string, string)>()));
    }

    [Fact]
    public void A_simple_cookie_belongs_to_the_host_and_the_directory_of_the_address()
    {
        PageCookie cookie = Assert.IsType<PageCookie>(Parse("jeton=xyz"));

        Assert.Equal("jeton", cookie.Name);
        Assert.Equal("xyz", cookie.Value);
        Assert.Equal("play13.ministryofwar.com", cookie.Domain);
        Assert.True(cookie.HostOnly);
        Assert.Equal("/Source/XMLSource", cookie.Path);
        Assert.Null(cookie.Expires);
        Assert.False(cookie.IsExpired(Now));
    }

    [Fact]
    public void Attributes_are_read()
    {
        Uri secure = new("https://play13.ministryofwar.com/login");
        PageCookie cookie = Assert.IsType<PageCookie>(Parse(
            " session = abc=def ; Domain=.MinistryOfWar.com; Path=/; Secure; HttpOnly; SameSite=strict; Expires=Wed, 21 Oct 2026 07:28:00 GMT", secure));

        Assert.Equal("session", cookie.Name);
        Assert.Equal("abc=def", cookie.Value);
        Assert.Equal("ministryofwar.com", cookie.Domain);
        Assert.False(cookie.HostOnly);
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 7, 28, 0, TimeSpan.Zero), cookie.Expires);
    }

    [Theory]
    [InlineData("a=1; Expires=Wed, 21-Oct-26 07:28:00 GMT")]
    [InlineData("a=1; expires=Wednesday, 21-Oct-2026 07:28:00 GMT")]
    [InlineData("a=1; Expires=21 Oct 2026 07:28:00 UTC")]
    public void Old_date_formats_are_understood(string header)
        => Assert.Equal(new DateTimeOffset(2026, 10, 21, 7, 28, 0, TimeSpan.Zero), Assert.IsType<PageCookie>(Parse(header)).Expires);

    [Fact]
    public void Max_age_wins_and_zero_removes_the_cookie()
    {
        Assert.Equal(Now.AddHours(1), Parse("a=1; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Max-Age=3600")!.Expires);
        Assert.True(Parse("a=1; Max-Age=0")!.IsExpired(Now));
        Assert.True(Parse("a=1; Expires=Thu, 01 Jan 1970 00:00:00 GMT")!.IsExpired(Now));
    }

    [Theory]
    // Domaine étranger, suffixe public, simple étiquette.
    [InlineData("a=1; Domain=autre-site.com")]
    [InlineData("a=1; Domain=com")]
    [InlineData("a=1; Domain=play14.ministryofwar.com")]
    // Secure hors https, SameSite=None sans Secure.
    [InlineData("a=1; Secure")]
    [InlineData("a=1; SameSite=None")]
    // Préfixes non respectés (adresse http : jamais Secure).
    [InlineData("__Secure-a=1")]
    [InlineData("__Host-a=1; Path=/")]
    // Mal formés.
    [InlineData("sans-egal")]
    [InlineData("=valeur")]
    [InlineData("nom avec espace=1")]
    [InlineData("")]
    public void Invalid_or_foreign_cookies_are_refused(string header)
        => Assert.Null(Parse(header));

    [Fact]
    public void Prefixed_cookies_follow_their_rules_over_https()
    {
        Uri secure = new("https://jeu.example.com/a/b");
        Assert.NotNull(Parse("__Secure-a=1; Secure", secure));
        Assert.NotNull(Parse("__Host-a=1; Secure; Path=/", secure));
        Assert.Null(Parse("__Host-a=1; Secure; Path=/; Domain=example.com", secure));
        Assert.Null(Parse("__Host-a=1; Secure", secure));
        Assert.NotNull(Parse("a=1; Secure; SameSite=None", secure));
    }

    [Fact]
    public void A_script_cannot_set_an_http_only_cookie()
    {
        Assert.Null(Parse("a=1; HttpOnly", fromHttp: false));
        Assert.NotNull(Parse("a=1; HttpOnly", fromHttp: true));
        Assert.NotNull(Parse("a=1; Path=/", fromHttp: false));
    }

    [Fact]
    public void An_ip_address_only_accepts_itself_as_domain()
    {
        Uri ip = new("http://192.168.1.20/jeu/");
        Assert.True(Parse("a=1", ip)!.HostOnly);
        Assert.Null(Parse("a=1; Domain=168.1.20", ip));
    }
}
