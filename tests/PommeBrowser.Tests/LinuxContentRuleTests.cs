using System.Text.Json;
using System.Text.RegularExpressions;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Tests;

/// <summary>
/// Conversion des listes Adblock Plus en règles de blocage WebKit (édition Linux).
/// Le comportement réel a aussi été vérifié dans WebKitGTK avec EasyList et EasyPrivacy.
/// </summary>
public sealed class LinuxContentRuleTests
{
    static List<JsonElement> Rules(params string[] lines)
    {
        ContentRuleSet set = ContentRuleConverter.Convert(new[] { ("test", string.Join('\n', lines)) });
        using JsonDocument document = JsonDocument.Parse(set.Json);
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    static string Filter(JsonElement rule) => rule.GetProperty("trigger").GetProperty("url-filter").GetString()!;
    static string Action(JsonElement rule) => rule.GetProperty("action").GetProperty("type").GetString()!;

    static string[] Array(JsonElement rule, string name)
        => rule.GetProperty("trigger").TryGetProperty(name, out JsonElement values)
            ? values.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : System.Array.Empty<string>();

    /// <summary>Le sous-ensemble d'expressions de WebKit est aussi valide pour .NET (insensible à la casse).</summary>
    static bool Matches(string filter, string url) => Regex.IsMatch(url, filter, RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("https://example.com/pub.js", true)]
    [InlineData("https://cdn.ads.example.com/x", true)]
    [InlineData("http://example.com:8080/", true)]
    [InlineData("wss://example.com/socket", true)]
    [InlineData("https://notexample.com/", false)]
    [InlineData("https://example.com.evil.net/", false)]
    [InlineData("https://other.net/?u=example.com/", false)]
    public void HostAnchorMatchesTheDomainAndItsSubdomainsOnly(string url, bool expected)
    {
        string filter = ContentRuleConverter.ConvertPattern("||example.com^")!;
        Assert.Equal(expected, Matches(filter, url));
    }

    [Theory]
    [InlineData("/banner/*/img^", "https://site.fr/banner/x/img?y=1", true)]
    [InlineData("/banner/*/img^", "https://site.fr/banner/x/img", true)]
    [InlineData("/banner/*/img^", "https://site.fr/banner/x/imgs", false)]
    [InlineData("|https://tracker.", "https://tracker.net/p", true)]
    [InlineData("|https://tracker.", "http://x.fr/?https://tracker.", false)]
    [InlineData(".gif|", "https://x.fr/a.gif", true)]
    [InlineData(".gif|", "https://x.fr/a.gif?x", false)]
    [InlineData("-ad-", "https://x.fr/top-ad-banner.png", true)]
    public void PatternsKeepTheirMeaning(string pattern, string url, bool expected)
    {
        string? filter = ContentRuleConverter.ConvertPattern(pattern);
        Assert.NotNull(filter);
        Assert.Equal(expected, Matches(filter!, url));
    }

    [Theory]
    [InlineData("/ad[0-9]+\\.js/", "ad[0-9]+\\.js")]
    [InlineData("/\\/banners\\/.*\\.swf/", "/banners/.*\\.swf")]
    public void SupportedRegularExpressionsAreKept(string pattern, string expected)
        => Assert.Equal(expected, ContentRuleConverter.ConvertPattern(pattern));

    [Theory]
    [InlineData("/(ads|pub)\\.js/")]      // alternative
    [InlineData("/ad\\d+\\.js/")]         // classe \d
    [InlineData("/a{2,}/")]               // quantificateur
    [InlineData("/(?=x)y/")]              // assertion
    [InlineData("||publicité.fr^")]       // non ASCII dans l'adresse
    public void PatternsWebKitCannotExpressAreSkipped(string pattern)
        => Assert.Null(ContentRuleConverter.ConvertPattern(pattern));

    [Fact]
    public void OptionsBecomeTriggerConditions()
    {
        JsonElement rule = Assert.Single(Rules("||ads.net^$third-party,script,image,domain=site.fr|news.fr,match-case"));

        Assert.Equal("block", Action(rule));
        Assert.Equal(new[] { "third-party" }, Array(rule, "load-type"));
        Assert.Equal(new[] { "script", "image" }, Array(rule, "resource-type"));
        Assert.Equal(new[] { "*site.fr", "*news.fr" }, Array(rule, "if-domain"));
        Assert.True(rule.GetProperty("trigger").GetProperty("url-filter-is-case-sensitive").GetBoolean());
    }

    [Fact]
    public void SubdocumentsAreDocumentsLoadedInAFrame()
    {
        List<JsonElement> rules = Rules("||frames.net^$subdocument,script");

        Assert.Equal(2, rules.Count);
        Assert.Equal(new[] { "script" }, Array(rules[0], "resource-type"));
        Assert.Equal(new[] { "document" }, Array(rules[1], "resource-type"));
        Assert.Equal(new[] { "child-frame" }, Array(rules[1], "load-context"));
    }

    [Fact]
    public void ExcludedTypesAreSubtractedFromTheDefaultTypes()
    {
        List<JsonElement> rules = Rules("/pub/$~script,~image");
        string[] types = Array(rules[0], "resource-type");

        Assert.DoesNotContain("script", types);
        Assert.DoesNotContain("image", types);
        Assert.Contains("style-sheet", types);
        Assert.DoesNotContain("popup", types);
        // Les cadres font partie des types par défaut : règle séparée.
        Assert.Equal(new[] { "child-frame" }, Array(rules[1], "load-context"));
    }

    [Theory]
    [InlineData("||x.net^$redirect=noopjs")]
    [InlineData("||x.net^$csp=script-src 'none'")]
    [InlineData("||x.net^$removeparam=utm_source")]
    [InlineData("||x.net^$domain=a.fr|~b.a.fr")]    // inclusion et exclusion : inexprimable
    [InlineData("||x.net^$domain=google.*")]         // domaine générique
    [InlineData("example.com##.x:has-text(Pub)")]
    [InlineData("example.com#$#abort-on-property-read x")]
    [InlineData("example.com##+js(nowebrtc)")]
    [InlineData("||x.net^$unknownoption")]
    public void UnsupportedFiltersAreSkippedRatherThanApproximated(string line)
    {
        ContentRuleSet set = ContentRuleConverter.Convert(new[] { ("test", line) });

        Assert.Equal(0, set.RuleCount);
        Assert.Equal(1, set.SkippedFilters);
    }

    [Fact]
    public void CosmeticRulesAreGenericOrLimitedToTheirSites()
    {
        List<JsonElement> rules = Rules("##.pub", "exemple.fr,news.fr##.bandeau", "~forum.fr##.sponsor");

        JsonElement generic = rules.Single(r => r.GetProperty("action").GetProperty("selector").GetString() == ".pub");
        Assert.Equal(".*", Filter(generic));
        Assert.Empty(Array(generic, "if-domain"));

        JsonElement specific = rules.Single(r => r.GetProperty("action").GetProperty("selector").GetString() == ".bandeau");
        Assert.Equal(new[] { "*exemple.fr", "*news.fr" }, Array(specific, "if-domain"));

        JsonElement excluded = rules.Single(r => r.GetProperty("action").GetProperty("selector").GetString() == ".sponsor");
        Assert.Equal(new[] { "*forum.fr" }, Array(excluded, "unless-domain"));
    }

    [Fact]
    public void CosmeticExceptionsRemoveOrRestrictSelectors()
    {
        List<JsonElement> rules = Rules("##.pub", "##.promo", "blog.fr#@#.pub", "#@#.promo");

        JsonElement pub = Assert.Single(rules);
        Assert.Equal(".pub", pub.GetProperty("action").GetProperty("selector").GetString());
        Assert.Equal(new[] { "*blog.fr" }, Array(pub, "unless-domain"));
    }

    [Fact]
    public void EachSelectorHasItsOwnRuleSoAnInvalidOneOnlyLosesItself()
    {
        // WebKit écarte une règle de masquage dont le sélecteur est invalide : groupés, les autres seraient perdus.
        List<JsonElement> rules = Rules("##.a", "##.b", "##div:unknown-pseudo");
        Assert.Equal(3, rules.Count);
        Assert.All(rules, r => Assert.DoesNotContain(",", r.GetProperty("action").GetProperty("selector").GetString()));
    }

    [Fact]
    public void RulesAreOrderedSoExceptionsOnlyCancelWhatTheyShould()
    {
        List<JsonElement> rules = Rules(
            "@@||ok.fr^$document",
            "@@||forum.fr^$generichide",
            "@@/pub.js",
            "exemple.fr##.bandeau",
            "/pub.js",
            "##.pub");

        int generic = rules.FindIndex(r => r.GetProperty("action").TryGetProperty("selector", out JsonElement s) && s.GetString() == ".pub");
        int genericHide = rules.FindIndex(r => Action(r) == "ignore-previous-rules" && Array(r, "if-domain").Contains("*forum.fr"));
        int block = rules.FindIndex(r => Action(r) == "block");
        int specific = rules.FindIndex(r => r.GetProperty("action").TryGetProperty("selector", out JsonElement s) && s.GetString() == ".bandeau");
        int exception = rules.FindIndex(r => Action(r) == "ignore-previous-rules" && Filter(r) == "/pub\\.js");
        int page = rules.FindIndex(r => Action(r) == "ignore-previous-rules" && Array(r, "if-domain").Contains("*ok.fr"));

        // $generichide n'annule que le masquage générique, placé avant lui.
        Assert.True(generic < genericHide && genericHide < block);
        Assert.True(block < specific && specific < exception);
        Assert.Equal(rules.Count - 1, page);
    }

    [Fact]
    public void NetworkExceptionsWithoutTypeDoNotDisableHidingOnThePage()
    {
        // Sans type, une exception s'appliquerait à la page elle-même et annulerait son masquage.
        List<JsonElement> rules = Rules("/pub.js", "@@||cdn.fr^");
        List<JsonElement> exceptions = rules.Where(r => Action(r) == "ignore-previous-rules").ToList();

        Assert.Equal(2, exceptions.Count);
        Assert.DoesNotContain("document", Array(exceptions[0], "resource-type"));
        Assert.Equal(new[] { "document" }, Array(exceptions[1], "resource-type"));
        Assert.Equal(new[] { "child-frame" }, Array(exceptions[1], "load-context"));
    }

    [Fact]
    public void InternationalDomainsAreConvertedToPunycode()
    {
        JsonElement rule = Assert.Single(Rules("||pub.net^$domain=café.fr"));
        Assert.Equal(new[] { "*xn--caf-dma.fr" }, Array(rule, "if-domain"));
    }

    [Fact]
    public void CosmeticFilteringCanBeTurnedOff()
    {
        ContentRuleSet set = ContentRuleConverter.Convert(new[] { ("test", "##.pub\n||ads.net^") }, cosmeticFiltering: false);
        Assert.Equal(1, set.RuleCount);
        Assert.Equal(0, set.CosmeticFilters);
    }

    [Fact]
    public void OutputIsValidJsonWithTheExpectedShape()
    {
        ContentRuleSet set = ContentRuleConverter.Convert(new[]
        {
            ("liste", "! Commentaire\n[Adblock Plus 2.0]\n||ads.net^\n@@||ads.net/ok.js\n##.pub\nsite.fr##.x\n\n   ")
        });

        using JsonDocument document = JsonDocument.Parse(set.Json);
        Assert.Equal(set.RuleCount, document.RootElement.GetArrayLength());
        foreach (JsonElement rule in document.RootElement.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(rule.GetProperty("trigger").GetProperty("url-filter").GetString()));
            Assert.Contains(rule.GetProperty("action").GetProperty("type").GetString(), new[] { "block", "css-display-none", "ignore-previous-rules" });
        }
    }
}
