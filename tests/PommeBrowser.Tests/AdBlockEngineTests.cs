using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;

namespace PommeBrowser.Tests;

public class AdBlockEngineTests
{
    private static AdBlockEngine CreateEngine(params string[] rules)
    {
        var engine = new AdBlockEngine();
        engine.ReplaceRules(new[] { ("test", string.Join('\n', rules)) });
        return engine;
    }

    private static bool Blocks(AdBlockEngine engine, string url, string documentHost = "site.fr", AdBlockResourceType type = AdBlockResourceType.Script)
    {
        var uri = new Uri(url);
        string doc = AdBlockDomain.NormalizeHost(documentHost);
        return engine.ShouldBlock(new AdBlockRequestContext
        {
            RequestUri = uri,
            DocumentHost = doc,
            ResourceType = type,
            IsThirdParty = !AdBlockDomain.IsSameSite(doc, uri.Host)
        });
    }

    [Fact]
    public void Host_rules_block_the_domain_and_its_subdomains()
    {
        var engine = CreateEngine("||ads.example^");

        Assert.True(Blocks(engine, "https://ads.example/pixel.gif"));
        Assert.True(Blocks(engine, "https://cdn.ads.example/a.js"));
        Assert.False(Blocks(engine, "https://notads.example/a.js"));
    }

    [Fact]
    public void Tokens_embedded_in_longer_words_still_match()
    {
        // Régression : l'index retenait « -ad-banner », jamais présent tel quel dans l'URL.
        var engine = CreateEngine("-ad-banner.");

        Assert.True(Blocks(engine, "https://site.fr/img/top-ad-banner.png", type: AdBlockResourceType.Image));
    }

    [Fact]
    public void Wildcard_prefixed_exceptions_apply()
    {
        var engine = CreateEngine("||fwmrm.net^", "@@||v.fwmrm.net/ad/g/*Nelonen$script");

        Assert.False(Blocks(engine, "https://v.fwmrm.net/ad/g/1Nelonen"));
        Assert.True(Blocks(engine, "https://v.fwmrm.net/ad/g/1Other"));
    }

    [Fact]
    public void Options_restrict_rules()
    {
        var engine = CreateEngine("/tracker.js$third-party,domain=news.fr|~sport.news.fr", "||cdn.test^$image");

        Assert.True(Blocks(engine, "https://other.com/tracker.js", "news.fr"));
        Assert.False(Blocks(engine, "https://other.com/tracker.js", "sport.news.fr"));
        Assert.False(Blocks(engine, "https://news.fr/tracker.js", "news.fr")); // première partie
        Assert.False(Blocks(engine, "https://other.com/tracker.js", "blog.fr"));

        Assert.True(Blocks(engine, "https://cdn.test/a.png", type: AdBlockResourceType.Image));
        Assert.False(Blocks(engine, "https://cdn.test/a.js", type: AdBlockResourceType.Script));
    }

    [Fact]
    public void Page_exceptions_disable_blocking_on_the_site()
    {
        var engine = CreateEngine("||ads.example^", "@@||trusted.fr^$document");

        Assert.False(Blocks(engine, "https://ads.example/a.js", "www.trusted.fr"));
        Assert.True(Blocks(engine, "https://ads.example/a.js", "other.fr"));
    }

    [Fact]
    public void Regex_rules_are_supported()
    {
        var engine = CreateEngine(@"/\/ad[0-9]{2}\.js/");

        Assert.True(Blocks(engine, "https://site.fr/static/ad42.js"));
        Assert.False(Blocks(engine, "https://site.fr/static/add.js"));
    }

    [Fact]
    public void Cosmetic_selectors_respect_domains_and_exceptions()
    {
        var engine = CreateEngine(
            "##.pub-generique",
            "news.fr##.pub-news",
            "~sport.news.fr##.pub-sauf-sport",
            "news.fr#@#.pub-generique");

        var news = engine.GetCosmeticSelectors("www.news.fr");
        Assert.Contains(".pub-news", news);
        Assert.Contains(".pub-sauf-sport", news);
        Assert.DoesNotContain(".pub-generique", news);

        var sport = engine.GetCosmeticSelectors("sport.news.fr");
        Assert.DoesNotContain(".pub-sauf-sport", sport);

        var other = engine.GetCosmeticSelectors("blog.fr");
        Assert.Contains(".pub-generique", other);
        Assert.DoesNotContain(".pub-news", other);
    }

    [Theory]
    [InlineData("WWW.Example.COM", "example.com")]
    [InlineData("https://www.example.com/path", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("", "")]
    public void Hosts_are_normalized(string input, string expected)
        => Assert.Equal(expected, AdBlockDomain.NormalizeHost(input));

    [Theory]
    [InlineData("a.b.bbc.co.uk", "bbc.co.uk")]
    [InlineData("cdn.example.com", "example.com")]
    [InlineData("example.com", "example.com")]
    public void Registrable_domain(string host, string expected)
        => Assert.Equal(expected, AdBlockDomain.GetRegistrableDomain(host));
}
